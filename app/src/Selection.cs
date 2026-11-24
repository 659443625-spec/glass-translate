using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace MouseTranslate
{
    internal struct PointText
    {
        public string Word;
        public string Line;
        public bool HasTextPattern;   // 诊断用
        public string ControlType;    // 诊断用
    }

    /// <summary>
    /// 从任意软件里取出「用户选中的文字」或「鼠标所指的文字」。
    /// 只认真正能提供文本的控件（TextPattern），绝不再拿控件名字凑数——
    /// 那是上一版"没划词也弹图标"的元凶。
    /// </summary>
    internal static class Selection
    {
        // ---------------- UIA 并发闸门 / 熔断（R3） ----------------
        // UIA 调用可能一卡好几秒。以前每次调用都 new Thread 再抢 _uiaLock：一次卡住，
        // 后续线程就全堆在锁上。现在改成：
        //   · _uiaBusy：同一时刻只允许一次 UIA 调用，来晚了立即返回空（不再 new Thread）；
        //   · _uiaHotUntil：UIA 超时后熔断 30 秒，期间所有 UIA 调用直接返回空 → 上层走剪贴板兜底。
        // 用 int + Interlocked 而不是 bool：bool 没法做无锁 CAS，并发判断会有竞态。
        private static int _uiaBusy;
        private static DateTime _uiaHotUntil = DateTime.MinValue;

        /// <summary>抢占 UIA 执行权：忙 / 熔断中立即返回 false，绝不排队、不新建线程。</summary>
        private static bool TryEnterUia()
        {
            if (DateTime.UtcNow < _uiaHotUntil) return false;
            return Interlocked.CompareExchange(ref _uiaBusy, 1, 0) == 0;
        }

        /// <summary>释放 UIA 执行权（工作线程真正结束 / 线程没起来时调用）。</summary>
        private static void ExitUia()
        {
            Interlocked.Exchange(ref _uiaBusy, 0);
        }

        // ---------------- 公共入口 ----------------

        public static string GetSelectedText(int timeoutMs)
        {
            string s = RunUia(delegate { return ReadSelectionCore(); }, timeoutMs);
            return Clean(s);
        }

        /// <summary>一次 UIA 调用同时拿到「光标下的词」和「它所在的整行」，避免两次查询。</summary>
        public static PointText GetTextAtPoint(int x, int y, int timeoutMs)
        {
            PointText result = default(PointText);
            RunUia(delegate
            {
                result = ReadTextAtPointCore(x, y);
                return string.Empty;
            }, timeoutMs);
            result.Word = Clean(result.Word);
            result.Line = Clean(result.Line);
            return result;
        }

        /// <summary>在后台 STA 线程里跑剪贴板取词，避免 150ms+ 的阻塞卡住界面。</summary>
        public static void CopySelectionAsync(Action<string> done, int timeoutMs)
        {
            CopySelectionAsync(done, timeoutMs, IntPtr.Zero, null);
        }

        /// <summary>
        /// 带「请求有效性 + 目标窗口」校验的剪贴板取词（R1）：
        ///   · stillValid：STA 线程真正开始干活前先问一次"这次取词请求还有效吗"
        ///     （序号没被新的 mousedown 作废、程序还在运行）；不满足直接放弃，不碰键盘/剪贴板；
        ///   · expectedHwnd：发 Ctrl+C 之前再核对一次前台窗口还是不是划词时那个窗口，
        ///     防止用户松手后点到别处（典型是终端）时 Ctrl+C 落到新前台窗口（终端里 = SIGINT）。
        /// expectedHwnd == IntPtr.Zero 表示不校验窗口；stillValid == null 表示不校验有效性。
        /// </summary>
        public static void CopySelectionAsync(Action<string> done, int timeoutMs, IntPtr expectedHwnd, Func<bool> stillValid)
        {
            Thread t = new Thread(delegate ()
            {
                string text = string.Empty;
                try
                {
                    if (stillValid == null || stillValid())
                        text = GetSelectedTextViaClipboard(timeoutMs, expectedHwnd);
                }
                catch { }
                try { if (done != null) done(text); }
                catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        /// <summary>剪贴板兜底（不校验前台窗口，保留旧调用方式）。</summary>
        public static string GetSelectedTextViaClipboard(int timeoutMs)
        {
            return GetSelectedTextViaClipboard(timeoutMs, IntPtr.Zero);
        }

        /// <summary>
        /// 剪贴板兜底：模拟一次 Ctrl+C，读完把原剪贴板（全格式）还原。
        /// 关键安全点：
        ///  ① 用 GetClipboardSequenceNumber 判定 Ctrl+C 前后剪贴板是否真的变了——没变说明目标程序
        ///     没响应 Ctrl+C（常见于管理员窗口 / 非文本控件），直接返回空串，绝不把旧剪贴板当选区；
        ///  ② 备份 / 还原都是全格式（IDataObject 逐格式克隆），图片、文件列表、HTML 不会丢；
        ///  ③ 只有确认剪贴板被改过才还原，避免无谓地重写用户剪贴板；
        ///  ④ R1：发键前校验前台窗口仍是调用方记录的窗口，用户已切走就一个键都不按；
        ///  ⑤ R2：Ctrl+C 后剪贴板里出现文件列表（Explorer 拖文件 / 拖单元格）→ 判失败并还原。
        /// 必须在 STA 线程调用（Clipboard 的要求）。
        /// </summary>
        /// <param name="expectedHwnd">
        /// R1：调用方在 mouseup 时记录的前台窗口。发 Ctrl+C 前核对一次，不一致说明用户已经切到
        /// 别的窗口（典型是终端），直接放弃——后台 Ctrl+C 落到终端里就是 SIGINT，会打断用户命令。
        /// IntPtr.Zero 表示不校验。
        /// </param>
        public static string GetSelectedTextViaClipboard(int timeoutMs, IntPtr expectedHwnd)
        {
            lock (_clipboardLock)   // 两次取词并发时，别让备份/还原互相踩
            {
                int budget = Math.Max(300, timeoutMs);
                int deadline = Environment.TickCount + budget;

                IDataObject backup = BackupClipboard();
                uint seqBefore = Native.GetClipboardSequenceNumber();
                bool clipboardChanged = false;
                try
                {
                    // R1：发键前最后一道校验——前台窗口必须还是划词时那个窗口。
                    // 不满足就放弃（不读剪贴板、一个键都不按）；用户松手后 100~300ms 内点到终端时，
                    // 后台 Ctrl+C 绝不能落到新前台窗口上（终端里 Ctrl+C = SIGINT，会打断用户命令）。
                    if (expectedHwnd != IntPtr.Zero && Native.GetForegroundWindow() != expectedHwnd)
                        return string.Empty;

                    Native.SendCtrlC();

                    // ① 等剪贴板序列号变化（目标程序处理 Ctrl+C 需要时间，某些网页要 300ms+）
                    Thread.Sleep(90);
                    while (Environment.TickCount < deadline)
                    {
                        if (Native.GetClipboardSequenceNumber() != seqBefore) { clipboardChanged = true; break; }
                        Thread.Sleep(35);
                    }
                    if (!clipboardChanged) return string.Empty;   // Ctrl+C 没生效：不读、不猜、不还原

                    // R2：Explorer 拖文件、Excel 拖单元格也会让剪贴板出现"文本形态"的东西，被当成选区。
                    // 只要剪贴板里有文件列表就判失败；finally 已因 clipboardChanged 而还原原剪贴板。
                    // 文件列表格式可能比文本晚一步落盘，所以轮询里每轮都查，读完再确认一次。
                    string got = null;
                    while (Environment.TickCount < deadline)
                    {
                        if (ClipboardContainsFileDropList()) return string.Empty;
                        try { if (Clipboard.ContainsText()) got = Clipboard.GetText(); }
                        catch { }
                        if (!string.IsNullOrEmpty(got)) break;
                        Thread.Sleep(35);
                    }
                    if (ClipboardContainsFileDropList()) return string.Empty;
                    return Clean(got ?? string.Empty);
                }
                catch { return string.Empty; }
                finally
                {
                    // 只还原"确实被 Ctrl+C 改过"的剪贴板；
                    // 另外补一次序列号复查，防 Ctrl+C 处理得特别慢、在超时之后才落盘导致丢原内容
                    bool changed = clipboardChanged;
                    if (!changed)
                    {
                        try { changed = Native.GetClipboardSequenceNumber() != seqBefore; }
                        catch { }
                    }
                    if (changed) RestoreClipboard(backup);
                }
            }
        }

        /// <summary>R2：剪贴板当前是否带文件拖放列表（Explorer 拖文件 / 拖单元格的典型形态）。</summary>
        private static bool ClipboardContainsFileDropList()
        {
            try { return Clipboard.ContainsFileDropList(); }
            catch { return false; }
        }

        // ---------------- 剪贴板全格式备份 / 还原 ----------------

        private static readonly object _clipboardLock = new object();

        /// <summary>
        /// 把当前剪贴板逐格式克隆到新的 DataObject。
        /// 必须先克隆：一旦发出 Ctrl+C，原来拿到的 IDataObject 就可能失效（延迟渲染）。
        /// 返回 null 表示没备份到任何可用格式（此时不做还原，免得把剪贴板清空）。
        /// </summary>
        private static IDataObject BackupClipboard()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    IDataObject src = Clipboard.GetDataObject();
                    if (src == null) return null;

                    string[] formats = null;
                    try { formats = src.GetFormats(); }
                    catch { }
                    if (formats == null || formats.Length == 0) return null;

                    DataObject copy = new DataObject();
                    int copied = 0;
                    foreach (string f in formats)
                    {
                        if (string.IsNullOrEmpty(f)) continue;
                        try
                        {
                            object data = src.GetData(f);
                            object safe = CloneFormatData(data);
                            if (safe == null) continue;
                            copy.SetData(f, safe);
                            copied++;
                        }
                        catch { }
                    }
                    return copied > 0 ? (IDataObject)copy : null;
                }
                catch { Thread.Sleep(30); }   // 剪贴板被别的程序占用：稍等重试
            }

            // 最后兜底：整块 DataObject 读不出来时，至少把用户最常丢的纯文本保住
            try
            {
                if (Clipboard.ContainsText())
                {
                    DataObject textOnly = new DataObject();
                    textOnly.SetText(Clipboard.GetText());
                    return textOnly;
                }
            }
            catch { }
            return null;
        }

        private static bool RestoreClipboard(IDataObject backup)
        {
            if (backup == null) return false;
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetDataObject(backup, true);   // copy=true：数据留在本进程，剪贴板独立持有
                    return true;
                }
                catch { Thread.Sleep(50); }
            }
            return false;
        }

        /// <summary>逐格式安全复制：流要倒回 0 位置，不可序列化的对象跳过（否则还原会整体失败）。</summary>
        private static object CloneFormatData(object data)
        {
            if (data == null) return null;

            MemoryStream ms = data as MemoryStream;
            if (ms != null)
            {
                // ToArray 取整个缓冲区，不受 Position 影响
                byte[] bytes;
                try { bytes = ms.ToArray(); } catch { return null; }
                if (bytes.Length == 0) return null;
                return new MemoryStream(bytes);   // Position = 0，还原后对方能从头上读
            }

            Stream st = data as Stream;
            if (st != null)
            {
                try
                {
                    st.Position = 0;
                    MemoryStream copy = new MemoryStream();
                    byte[] buf = new byte[8192];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0) copy.Write(buf, 0, n);
                    copy.Position = 0;
                    return copy;
                }
                catch { return null; }
            }

            if (!IsRestorableData(data)) return null;
            return data;
        }

        private static bool IsRestorableData(object data)
        {
            if (data == null) return false;
            if (data is string || data is string[] || data is byte[]) return true;
            try { return data.GetType().IsSerializable; } catch { return false; }
        }

        // ---------------- 落点过滤（标题栏 / 滚动条 / 非客户区） ----------------

        /// <summary>
        /// 快速版落点判断（纯 Win32，微秒级，可直接在鼠标钩子里调用）：
        /// 光标下窗口是滚动条/表头，或者点落在顶层窗口客户区之外（标题栏、边框）→ true。
        /// </summary>
        public static bool IsChromePointFast(Point p)
        {
            try
            {
                Native.POINT pt = new Native.POINT(p.X, p.Y);
                IntPtr hwnd = Native.WindowFromPoint(pt);
                if (hwnd == IntPtr.Zero) return true;

                StringBuilder cls = new StringBuilder(256);
                if (Native.GetClassName(hwnd, cls, cls.Capacity) > 0)
                {
                    string name = cls.ToString();
                    if (name.IndexOf("ScrollBar", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                    if (name.IndexOf("SysHeader", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                }

                IntPtr root = Native.GetAncestor(hwnd, Native.GA_ROOT);
                if (root == IntPtr.Zero) root = hwnd;

                // ① 光标下窗口自己的非客户区（原生滚动条、边框、子窗口标题栏）
                if (IsOutsideClientArea(hwnd, p)) return true;
                // ② 顶层窗口客户区之外 = 标题栏 / 边框 / 菜单栏
                if (root != hwnd && IsOutsideClientArea(root, p)) return true;
                return false;
            }
            catch { return false; }
        }

        private static bool IsOutsideClientArea(IntPtr hwnd, Point p)
        {
            if (hwnd == IntPtr.Zero) return false;
            Native.POINT cp = new Native.POINT(p.X, p.Y);
            if (!Native.ScreenToClient(hwnd, ref cp)) return false;
            Native.RECT rc;
            if (!Native.GetClientRect(hwnd, out rc)) return false;
            return cp.X < rc.Left || cp.Y < rc.Top || cp.X >= rc.Right || cp.Y >= rc.Bottom;
        }

        /// <summary>
        /// UIA 版落点判断：标题栏 / 滚动条 / 滑块 / 拖拽源控件（列表项 / 数据项 / 树节点 / 表格单元格）→ true。
        /// R3：复用 RunUia 同一套 busy + 熔断闸门——UIA 忙或熔断中立即返回 false，
        ///     不排队、不 new Thread、不阻塞调用线程（绝不因为 _uiaBusy 为真把界面卡住）。
        /// R4：不再把 ControlType.Window 视为 chrome——自绘程序（部分 PDF 阅读器 / 终端 / Java 编辑器）
        ///     整个客户区常被 UIA 报成 Window，加这条会让划词彻底失效；真正的标题栏 / 非客户区
        ///     已由 Win32 快判 IsChromePointFast 覆盖。
        /// waitLockMs 参数保留只为兼容旧调用（不再等待锁）。
        /// </summary>
        public static bool IsChromePointUia(int x, int y, int waitLockMs)
        {
            if (!TryEnterUia()) return false;   // 已有 UIA 在跑 / 熔断中：立即放弃，绝不阻塞
            try
            {
                AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                if (el == null) return false;
                ControlType ct = el.Current.ControlType;
                if (ct == ControlType.TitleBar) return true;
                if (ct == ControlType.ScrollBar) return true;
                if (ct == ControlType.Thumb) return true;
                // R2：这些控件通常是拖拽源（Explorer 文件列表、Excel 单元格、树形目录等），
                // 在上面拖拽会写剪贴板 / 触发拖放，不是划词。注意不放 Document / Text，
                // 网页正文、PDF 页面不能被误伤。
                if (ct == ControlType.ListItem) return true;
                if (ct == ControlType.DataItem) return true;
                if (ct == ControlType.TreeItem) return true;
                // .NET 4.0 的 ControlType 没有 Cell 静态属性（UIA 标准控制类型里也没有 Cell），
                // 用 ProgrammaticName 字符串兜底：第三方 Provider 可能把表格单元格报成 ControlType.Cell。
                string ctName = null;
                try { ctName = ct.ProgrammaticName; } catch { }
                if (string.Equals(ctName, "ControlType.Cell", StringComparison.Ordinal)) return true;
                return false;
            }
            catch { return false; }
            finally
            {
                ExitUia();   // 异常路径同样释放 busy
            }
        }

        /// <summary>
        /// 前台窗口是否"收不到我们发的 Ctrl+C"：目标进程以管理员身份运行时会被 UIPI 拦截。
        /// 查询失败按"没发现"处理（失败就算了），不阻塞正常兜底。
        /// </summary>
        public static bool ForegroundBlocksClipboardInjection()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return false;
                int pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return false;

                bool targetElevated;
                if (!Native.TryGetProcessElevation(pid, out targetElevated)) return false;  // 查不到就算了
                if (!targetElevated) return false;

                bool selfElevated;
                if (!Native.TryGetProcessElevation((int)Native.GetCurrentProcessId(), out selfElevated)) return false;
                return !selfElevated;   // 对方是管理员窗口、我们不是 → 会被 UIPI 拦，跳过兜底
            }
            catch { return false; }
        }

        // ---------------- UI Automation 实现 ----------------

        private static string ReadSelectionCore()
        {
            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused == null) return string.Empty;

            object pattern;
            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return string.Empty;

            TextPattern tp = (TextPattern)pattern;
            TextPatternRange[] ranges = tp.GetSelection();
            if (ranges == null || ranges.Length == 0) return string.Empty;

            StringBuilder sb = new StringBuilder();
            foreach (TextPatternRange r in ranges)
            {
                string t = r.GetText(-1);
                if (!string.IsNullOrEmpty(t)) sb.Append(t);
            }
            return sb.ToString();
        }

        private static PointText ReadTextAtPointCore(int x, int y)
        {
            PointText result = default(PointText);

            AutomationElement el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (el == null) return result;

            try { result.ControlType = el.Current.ControlType.ProgrammaticName; }
            catch { }

            // 排除典型的"界面零件"：按钮、菜单、滚动条之类，避免误弹
            if (!IsTextualControl(el)) return result;

            object pattern;
            if (!el.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return result;

            result.HasTextPattern = true;
            TextPattern tp = (TextPattern)pattern;
            TextPatternRange range = tp.RangeFromPoint(new System.Windows.Point(x, y));
            if (range == null) return result;

            try
            {
                range.ExpandToEnclosingUnit(TextUnit.Word);
                result.Word = range.GetText(-1);
            }
            catch { }

            try
            {
                range.ExpandToEnclosingUnit(TextUnit.Line);
                result.Line = range.GetText(-1);
            }
            catch { }

            return result;
        }

        private static bool IsTextualControl(AutomationElement el)
        {
            try
            {
                ControlType ct = el.Current.ControlType;
                if (ct == ControlType.Button) return false;
                if (ct == ControlType.MenuItem) return false;
                if (ct == ControlType.Menu) return false;
                if (ct == ControlType.MenuBar) return false;
                if (ct == ControlType.TabItem) return false;
                if (ct == ControlType.ScrollBar) return false;
                if (ct == ControlType.TitleBar) return false;
                if (ct == ControlType.ToolBar) return false;
                if (ct == ControlType.ToolTip) return false;
                if (ct == ControlType.StatusBar) return false;
                if (ct == ControlType.ComboBox) return false;
                if (ct == ControlType.CheckBox) return false;
                if (ct == ControlType.RadioButton) return false;
                if (ct == ControlType.Slider) return false;
                if (ct == ControlType.ProgressBar) return false;
                if (ct == ControlType.Thumb) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 在后台 MTA 线程里跑 UIA，并设硬超时——某些窗口的 UIA 调用会卡好几秒。
        /// R3：同一时刻只允许一次 UIA 调用；来晚了立即返回空（不再 new Thread 堆在锁上）；
        ///     超时后熔断 30 秒（_uiaHotUntil），期间所有 UIA 调用直接返回空，让上层走剪贴板 / OCR 兜底。
        /// </summary>
        private static string RunUia(Func<string> job, int timeoutMs)
        {
            if (!TryEnterUia()) return string.Empty;   // 已有 UIA 在跑 / 熔断中：立即放弃

            string result = string.Empty;
            Thread t = new Thread(delegate ()
            {
                try { result = job() ?? string.Empty; }
                catch { result = string.Empty; }
                finally { ExitUia(); }   // 工作线程真正结束才释放 busy；异常路径同样释放
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            try
            {
                t.Start();
            }
            catch
            {
                ExitUia();   // 线程没起来就不会有上面的 finally，这里兜底释放
                return string.Empty;
            }

            try
            {
                if (!t.Join(Math.Max(120, timeoutMs)))
                {
                    // 超时：UIA 工作线程可能还卡在调用里，不能在这里清 busy（否则又会堆线程）。
                    // 只有"给了足够预算仍然超时"才判定 UIA 有问题；悬停探测预算很短（280ms），
                    // 在浏览器/PDF 这类重应用里超时是常态，不能因此熔断 30 秒把划词逼到剪贴板。
                    if (timeoutMs >= 400) _uiaHotUntil = DateTime.UtcNow.AddSeconds(15);
                    return string.Empty;
                }
            }
            catch
            {
                // Join 异常（线程仍在跑）：busy 交给工作线程自己的 finally 释放
                return string.Empty;
            }
            return result ?? string.Empty;
        }

        // ---------------- 校验与清洗 ----------------

        /// <summary>去掉词首尾的标点，便于判定（学术文本里 "BLEU," "28.4" 这种很常见）。</summary>
        public static string NormalizeWord(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Trim();
            char[] trimChars = new char[]
            {
                '.', ',', ';', ':', '!', '?', '"', '\'', '’', '“', '”', '(', ')', '[', ']', '{', '}',
                '·', '、', '。', '，', '；', '：', '！', '？'
            };
            s = s.Trim(trimChars);
            return s.Trim();
        }

        /// <summary>是不是一个"看起来像词/短句"的文本，用来挡掉误识别。</summary>
        public static bool LooksLikeWord(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length < 2 || s.Length > 40) return false;
            if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) return false;

            bool hasLetter = false;
            bool hasDigit = false;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) { hasLetter = true; continue; }
                if (char.IsDigit(c)) { hasDigit = true; continue; }
                if (c == ' ' || c == '-' || c == '\'' || c == '’' || c == '.') continue;
                return false;   // 含其它符号，多半不是正文单词
            }
            // 纯数字（页码、年份）没翻译价值
            if (!hasLetter && hasDigit) return false;
            return hasLetter;
        }

        public static bool LooksLikeSentence(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length < 3) return false;
            int letters = 0;
            foreach (char c in s) if (char.IsLetter(c)) letters++;
            return letters >= 2;
        }

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (s.Length > 3000) s = s.Substring(0, 3000);
            return s;
        }

        /// <summary>当前前台进程名，用于避免在我们自己的窗口上取词。</summary>
        public static string ForegroundProcessName()
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero) return string.Empty;
                int pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid == 0) return string.Empty;
                return Process.GetProcessById(pid).ProcessName;
            }
            catch { return string.Empty; }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
    }
}
