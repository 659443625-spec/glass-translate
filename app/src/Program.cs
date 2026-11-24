using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace MouseTranslate
{
    internal static class Program
    {
        internal static void WriteCrash(string tag, Exception ex)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseTranslate");
                Directory.CreateDirectory(dir);
                string msg = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + tag + "] "
                    + (ex == null ? "(null)" : (ex.GetType().FullName + ": " + ex.Message + Environment.NewLine + ex.StackTrace))
                    + Environment.NewLine + "-----" + Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "crash.log"), msg);
            }
            catch { }
        }

        [STAThread]
        private static void Main(string[] args)
        {
            try { Native.SetProcessDPIAware(); } catch { }

            // 未处理异常统一落盘，避免只弹一个看不懂的系统对话框
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                WriteCrash("AppDomain", e.ExceptionObject as Exception);
            };
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                WriteCrash("UI", e.Exception);
            };

            // --theme dark|light 可以放在最前面（用于截图与手动指定），其余参数照旧
            string themeArg = string.Empty;
            if (args.Length >= 2 && args[0].ToLowerInvariant() == "--theme")
            {
                themeArg = args[1].ToLowerInvariant();
                string[] rest = new string[args.Length - 2];
                Array.Copy(args, 2, rest, 0, rest.Length);
                args = rest;
            }
            bool dark;
            if (themeArg == "dark") dark = true;
            else if (themeArg == "light") dark = false;
            else dark = Theme.DetectDark();
            Theme.Init(dark);

            string mode = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;

            if (mode == "--selftest")
            {
                string outFile = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.txt");
                int code = SelfTest.Run(outFile);
                Environment.Exit(code);
                return;
            }

            if (mode == "--dpi")
            {
                string outFile = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dpi.txt");
                StringBuilder info = new StringBuilder();
                info.AppendLine("GetDpiForSystem=" + Native.GetDpiForSystem());
                try
                {
                    using (Graphics gg = Graphics.FromHwnd(IntPtr.Zero))
                        info.AppendLine("Graphics.FromHwnd(0).DpiX=" + gg.DpiX);
                }
                catch (Exception ex) { info.AppendLine("gfx err=" + ex.Message); }
                info.AppendLine("Screen.PrimaryScreen.Bounds=" + Screen.PrimaryScreen.Bounds);
                try
                {
                    Form probe = new Form();
                    IntPtr ph = probe.Handle;
                    info.AppendLine("Form.DeviceDpi=" + probe.DeviceDpi);
                    probe.Dispose();
                }
                catch (Exception ex) { info.AppendLine("form err=" + ex.Message); }
                info.AppendLine("Dpi.Factor=" + Dpi.Factor);
                info.AppendLine("Dpi.S(560)=" + Dpi.S(560));
                File.WriteAllText(outFile, info.ToString(), new UTF8Encoding(false));
                Environment.Exit(0);
                return;
            }

            if (mode == "--tts-file")
            {
                string path = args.Length > 1 ? args[1] : string.Empty;
                string say = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "missing file";
                Tts.Speak(say);
                System.Threading.Thread.Sleep(20000);
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tts-status.txt"),
                        Tts.LastStatus + " | firstAudio=" + Tts.FirstAudioMs + "ms | chars=" + say.Length);
                }
                catch { }
                return;
            }

            if (mode == "--tts-test")
            {
                string say = args.Length > 1 ? args[1] : "Hello, this is a natural voice test.";
                Tts.Speak(say);
                System.Threading.Thread.Sleep(7000);
                try
                {
                    System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tts-status.txt"),
                        Tts.LastStatus + " | firstAudio=" + Tts.FirstAudioMs + "ms");
                }
                catch { }
                return;
            }

            if (mode == "--records")
            {
                // 预览记录窗口（调试用）
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                RecordsWindow rec = new RecordsWindow();
                rec.StartPosition = FormStartPosition.Manual;
                Rectangle wa2 = Screen.PrimaryScreen.WorkingArea;
                rec.SetBounds(wa2.Left + Dpi.S(120), wa2.Top + Dpi.S(60), Dpi.S(460), Dpi.S(600));
                rec.Prepare();
                rec.Show();
                for (int ai = 1; ai < args.Length; ai++)
                    if (args[ai] == "vocab") { System.Windows.Forms.Timer vt = new System.Windows.Forms.Timer(); vt.Interval = 1800; vt.Tick += delegate { vt.Stop(); vt.Dispose(); rec.ShowTab("vocab"); }; vt.Start(); }
                Application.Run(rec);
                return;
            }

            if (mode == "--card")
            {
                // 预览翻译卡片（调试用）：--card "要翻译的文字"
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                CardWindow card = new CardWindow();
                string sample = args.Length > 1 ? args[1] : "The best interface is the one you forget you are using.";
                bool isWord = App.IsSingleWord(sample);
                card.ShowLoading(sample, isWord);
                Rectangle waiver = Screen.PrimaryScreen.WorkingArea;
                card.ShowNear(new Rectangle(waiver.Left + Dpi.S(80), waiver.Top + Dpi.S(120), Dpi.S(28), Dpi.S(28)));
                for (int ai = 2; ai < args.Length; ai++)
                    if (args[ai] == "pinned") { System.Windows.Forms.Timer pt = new System.Windows.Forms.Timer(); pt.Interval = 1500; pt.Tick += delegate { pt.Stop(); pt.Dispose(); card.TogglePin(); }; pt.Start(); }
                Translator tr = new Translator();
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (isWord)
                    {
                        DictEntry d;
                        try { d = tr.LookupAsync(sample).GetAwaiter().GetResult(); }
                        catch (Exception ex) { d = new DictEntry(); d.Word = sample; d.Ok = false; d.Error = ex.Message; }
                        DictEntry dd = d;
                        try { card.BeginInvoke(new Action(delegate { card.ShowWord(sample, dd); })); } catch { }
                        return;
                    }
                    TranslateResult r;
                    try { r = tr.TranslateAsync(sample, "zh-CN").GetAwaiter().GetResult(); }
                    catch (Exception ex) { r = new TranslateResult(); r.Ok = false; r.Error = ex.Message; }
                    TranslateResult rr = r;
                    try { card.BeginInvoke(new Action(delegate { card.ShowSentence(sample, rr); })); } catch { }
                });
                Application.Run(card);
                return;
            }

            if (mode == "--webview-test")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                int w = Dpi.S(480), h = Dpi.S(360);
                Rectangle scr = Screen.PrimaryScreen.WorkingArea;
                int x = scr.Left + (scr.Width - w) / 2;
                int y = scr.Top + (scr.Height - h) / 2;

                Bitmap bg = Glass.Capture(new Rectangle(x, y, w, h));
                PanelForm panel = new PanelForm(new App());
                panel.StartPosition = FormStartPosition.Manual;
                panel.SetBounds(x, y, w, h);
                panel.Show();

                System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                t.Interval = 25000;
                t.Tick += delegate { Application.Exit(); };
                t.Start();
                Application.Run(panel);
                return;
            }

            if (mode == "--icon-live")
            {
                // 把徽标固定在屏幕上若干秒，便于截图检查透明与工艺
                int lx = args.Length > 1 ? int.Parse(args[1]) : 600;
                int ly = args.Length > 2 ? int.Parse(args[2]) : 400;
                int secs = args.Length > 3 ? int.Parse(args[3]) : 8;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                IconWindow live = new IconWindow();
                live.ShowAt(new Point(lx, ly), "preview");
                System.Windows.Forms.Timer quit = new System.Windows.Forms.Timer();
                quit.Interval = secs * 1000;
                quit.Tick += delegate { quit.Stop(); Application.Exit(); };
                quit.Start();
                Application.Run();
                return;
            }

            if (mode == "--icon-sheet")
            {
                string file = args.Length > 1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icons.png");
                IconSheet.Render(file);
                Environment.Exit(0);
                return;
            }

            if (mode == "--probe")
            {
                // 取词诊断：--probe <x> <y> <输出文件>
                int px = args.Length > 1 ? int.Parse(args[1]) : 0;
                int py = args.Length > 2 ? int.Parse(args[2]) : 0;
                string outFile = args.Length > 3 ? args[3] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "probe.txt");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== 取词诊断 @ " + px + "," + py + " ===");

                PointText pt = Selection.GetTextAtPoint(px, py, 1200);
                sb.AppendLine("UIA 控件类型: " + (pt.ControlType ?? ""));
                sb.AppendLine("UIA TextPattern: " + (pt.HasTextPattern ? "有" : "无"));
                sb.AppendLine("UIA 单词: '" + (pt.Word ?? "") + "'");
                sb.AppendLine("UIA 整行: '" + (pt.Line ?? "") + "'");
                sb.AppendLine("UIA 判定可用: " + (Selection.LooksLikeWord(pt.Word) ? "是" : "否"));

                OcrHost ocr = new OcrHost();
                try
                {
                    bool ok = ocr.Start();
                    sb.AppendLine();
                    sb.AppendLine("OCR 启动: " + (ok ? "成功" : "失败 " + ocr.LastError));
                    if (ok)
                    {
                        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                        OcrResult r = ocr.CaptureAndRecognize(px - 110, py - 34, 620, 104);
                        sw.Stop();
                        sb.AppendLine("OCR 用时: " + sw.ElapsedMilliseconds + " ms  ok=" + r.Ok + " " + r.Error);
                        if (r.Ok)
                        {
                            sb.AppendLine("OCR 全文: '" + App.Shorten(r.Text, 200) + "'");
                            sb.AppendLine("OCR 行数: " + r.Lines.Count);
                            OcrWord w = r.WordAtScreenPoint(px, py);
                            sb.AppendLine("命中单词: '" + (w != null ? w.Text : "") + "'");
                            OcrLine l = r.WordAtScreenPoint(px, py) != null ? r.LineOf(w) : App.NearestLine(r, new Point(px, py));
                            sb.AppendLine("命中整行: '" + (l != null ? App.Shorten(l.Text, 160) : "") + "'");
                        }
                    }
                }
                finally { try { ocr.Dispose(); } catch { } }

                File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false));
                Environment.Exit(0);
                return;
            }

            if (mode == "--translate")
            {
                string text = args.Length > 1 ? args[1] : "Hello, world.";
                string outFile = args.Length > 2 ? args[2] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "translate.txt");
                int code = SelfTest.RunTranslate(text, outFile);
                Environment.Exit(code);
                return;
            }

            bool created;
            using (Mutex mutex = new Mutex(true, "MouseTranslate_SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("鼠标翻译已经在运行了，请到系统托盘里找它。", "鼠标翻译",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                App app = new App();
                if (mode == "--autostart") app.Start();   // 启动即生效（配合开机自启用）
                Application.Run(app);
            }
        }
    }

    /// <summary>程序主体：托盘、主窗口、小图标、卡片、取词与翻译调度。</summary>
    internal sealed class App : ApplicationContext
    {
        public readonly Settings Settings;
        private readonly Control _ui = new Control();   // UI 线程调度器（把后台结果切回 UI 线程）
        private readonly OcrHost _ocr = new OcrHost();
        private readonly Translator _translator = new Translator();
        private readonly PanelForm _form;
        private readonly IconWindow _icon;
        private readonly CardWindow _card;
        private RecordsWindow _records;
        private readonly NotifyIcon _tray;

        private IntPtr _hookId = IntPtr.Zero;
        private Native.LowLevelMouseProc _hookProc;   // 必须持有引用，否则会被 GC 回收
        private readonly System.Windows.Forms.Timer _hoverTimer;
        private readonly System.Windows.Forms.Timer _selectionTimer;

        private Point _downPoint;
        private bool _verticalDrag;
        private Point _lastPos;
        private DateTime _idleStart = DateTime.UtcNow;
        private DateTime _suppressHoverUntil = DateTime.MinValue;
        private DateTime _lastOcrAt = DateTime.MinValue;
        private bool _probing;
        private string _hoverLine = string.Empty;
        private bool _running;
        private bool _ocrStarted;
        private DateTime _iconShownAt = DateTime.MinValue;
        private string _iconSource = string.Empty;   // "selection" | "hover"
        private string _ocrState = "未启动";

        // --- v2 节流与防误弹 ---
        private bool _dragButtonDown;
        private DateTime _iconIgnoreUntil = DateTime.MinValue;
        private Point _lastProbePoint = new Point(-99999, -99999);
        private DateTime _lastProbeAt = DateTime.MinValue;
        private string _fgProc = string.Empty;
        private DateTime _fgProcAt = DateTime.MinValue;

        // --- F1 落点过滤：标题栏 / 滚动条 / 非客户区不取词 ---
        private readonly object _downGate = new object();
        private int _downSeq;              // 每次 mousedown 递增，用来丢弃过期的落点判断结果
        private bool _skipSelection;       // 本次按下落在不该取词的地方 → 放弃本次取词

        // --- F2 翻译请求序号：旧请求晚到不许覆盖新卡片 ---
        private long _translateSeq;

        // --- F3 双击 / 三击选词 ---
        private DateTime _lastDownAt = DateTime.MinValue;
        private Point _lastDownPoint = new Point(-99999, -99999);
        private int _clickCount;           // 1 = 单击，2 = 双击，3 = 三击
        private readonly System.Windows.Forms.Timer _multiClickTimer;

        // --- F4 长文截断状态 ---
        private bool _selectionTruncated;  // 最近一次划词是否在 1200 字处被截断
        private string _selectionText = string.Empty;   // 最近一次划词最终展示的原文（截断后的）
        private bool _lastTruncated;       // 最近一次翻译结果对应的原文是否被截断

        // --- F5 取词请求序号：后台 UIA 结果回来时校验，过期就丢 ---
        private int _selectionSeq;

        /// <summary>最近一次翻译的原文是否被截断（超过 1200 字只翻了前面部分）。</summary>
        public bool LastTruncated { get { return _lastTruncated; } }

        public App()
        {
            Settings = Settings.Load();
            IntPtr ready = _ui.Handle;   // 用来把后台线程的结果切回 UI 线程

            _icon = new IconWindow();
            _icon.Clicked += delegate { TranslatePayload(_icon.Payload); };
            _icon.RightClicked += delegate
            {
                string t = _hoverLine.Length > 0 ? _hoverLine : _icon.Payload;
                TranslatePayload(t);
            };

            _card = new CardWindow();

            _form = new PanelForm(this);
            _form.RecordsRequested = delegate { ShowRecords(); };
            _card.RecordsRequested = delegate (string tab) { ShowRecords(tab); };
            _form.SetRunning(false);

            _hoverTimer = new System.Windows.Forms.Timer();
            _hoverTimer.Interval = 150;
            _hoverTimer.Tick += delegate { HoverTick(); };

            _selectionTimer = new System.Windows.Forms.Timer();
            _selectionTimer.Interval = 130;
            _selectionTimer.Tick += delegate
            {
                _selectionTimer.Stop();
                CaptureSelection(_lastPos);
            };

            // 双击 / 三击：等 150ms 让选区稳定下来（此时 UIA 选区已是整词 / 整段），再取词
            _multiClickTimer = new System.Windows.Forms.Timer();
            _multiClickTimer.Interval = 150;
            _multiClickTimer.Tick += delegate
            {
                _multiClickTimer.Stop();
                OnMultiClickCapture();
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("打开控制面板", null, delegate { ShowMain(); });
            menu.Items.Add("启动 / 停止翻译", null, delegate { Toggle(); });
            menu.Items.Add("翻译记录 / 生词本", null, delegate { ShowRecords(); });
            menu.Items.Add("清除朗读缓存", null, delegate
            {
                long freed = Tts.ClearCache();
                try
                {
                    _tray.BalloonTipTitle = "鼠标翻译";
                    _tray.BalloonTipText = freed > 0
                        ? ("已清理 " + (freed / 1024) + " KB 朗读缓存")
                        : "朗读缓存本来就是空的";
                    _tray.ShowBalloonTip(2500);
                }
                catch { }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { Exit(); });

            _tray = new NotifyIcon();
            try
            {
                Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                _tray.Icon = ico != null ? ico : SystemIcons.Application;
            }
            catch { _tray.Icon = SystemIcons.Application; }
            _tray.Text = "鼠标翻译";
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowMain(); };

            _form.Show();
            RefreshCapabilities();
        }

        // ---------------- 生命周期 ----------------

        /// <summary>打开「翻译记录 / 生词本」窗口。</summary>
        private void ShowRecords() { ShowRecords(null); }

        private void ShowRecords(string tab)
        {
            try
            {
                if (_records == null)
                {
                    _records = new RecordsWindow();
                    _records.TranslateRequested = delegate (string t)
                    {
                        Rectangle anchor = new Rectangle(_records.Left + 40, _records.Top + 90, 28, 28);
                        TranslatePayload(t, anchor);
                    };
                }

                // 卡片可见时，记录窗口贴着卡片打开
                if (_card.Visible)
                {
                    Rectangle wa = Screen.FromRectangle(_card.Bounds).WorkingArea;
                    int x = _card.Right + Dpi.S(10);
                    if (x + _records.Width > wa.Right - Dpi.S(8)) x = _card.Left - _records.Width - Dpi.S(10);
                    if (x < wa.Left + Dpi.S(8)) x = wa.Left + Dpi.S(8);
                    int y = Math.Max(wa.Top + Dpi.S(8), Math.Min(_card.Top, wa.Bottom - _records.Height - Dpi.S(8)));
                    _records.SetBounds(x, y, _records.Width, _records.Height);
                }

                _records.PendingTab = tab;
                _records.TopMost = true;
                _records.Prepare();
                _records.Show();
                _records.BringToFront();
                _records.Activate();
            }
            catch (Exception ex) { Program.WriteCrash("ShowRecords", ex); }
        }

        public void Start()
        {
            if (_running) return;
            _hookProc = HookCallback;
            _hookId = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _hookProc, Native.GetModuleHandle(null), 0);
            if (Settings.HoverMode) _hoverTimer.Start();   // 悬停关闭时连计时器都不跑
            _running = true;
            _form.SetRunning(true);
            _tray.Text = "鼠标翻译（运行中）";
        }

        public void Stop()
        {
            if (!_running) return;
            _hoverTimer.Stop();
            _selectionTimer.Stop();
            _multiClickTimer.Stop();
            if (_hookId != IntPtr.Zero)
            {
                Native.UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            _running = false;
            _icon.Hide();
            if (!_card.Pinned) _card.Hide();
            _form.SetRunning(false);
            _tray.Text = "鼠标翻译（已停止）";
        }

        /// <summary>设置变更后同步计时器（关闭悬停就彻底不探测）。</summary>
        public void OnSettingsChanged()
        {
            if (!_running) return;
            if (Settings.HoverMode)
            {
                if (!_hoverTimer.Enabled) _hoverTimer.Start();
            }
            else
            {
                _hoverTimer.Stop();
                if (_icon.Visible && _iconSource == "hover") _icon.Hide();
            }
        }

        public void Toggle()
        {
            if (_running)
            {
                Stop();
                ShowMain();
            }
            else
            {
                Start();
                _form.HideToTray();   // 启动后收起面板，别挡着要翻译的页面
            }
        }

        public void ShowMain()
        {
            _form.Show();
            if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
            _form.Activate();
        }

        public void Exit()
        {
            Stop();
            try { _ocr.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { _icon.Dispose(); } catch { }
            try { _card.Dispose(); } catch { }
            try { _form.Dispose(); } catch { }
            Application.Exit();
        }

        // ---------------- 全局鼠标钩子 ----------------

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                // 只处理按下/抬起；鼠标移动的消息一律直接放行，不解析结构体。
                // 高分鼠标能到 1000Hz，这条早退是「卡」的关键优化。
                if (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_LBUTTONUP)
                {
                    try
                    {
                        Native.MSLLHOOKSTRUCT data = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(
                            lParam, typeof(Native.MSLLHOOKSTRUCT));
                        Point pt = new Point(data.pt.X, data.pt.Y);
                        if (msg == Native.WM_LBUTTONDOWN)
                        {
                            _downPoint = pt;
                            _dragButtonDown = true;
                            TrackClick(pt);          // F3：记录连击次数（双击/三击）
                            OnGlobalMouseDown(pt);
                        }
                        else
                        {
                            _dragButtonDown = false;
                            OnGlobalMouseUp(pt);
                        }
                    }
                    catch { }
                }
            }
            return Native.CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        /// <summary>F3：在 mousedown 里维护连击计数（用系统双击时间 / 双击范围判定）。</summary>
        private void TrackClick(Point p)
        {
            DateTime now = DateTime.UtcNow;
            Size dblSize = SystemInformation.DoubleClickSize;
            int tolX = Math.Max(2, dblSize.Width / 2);
            int tolY = Math.Max(2, dblSize.Height / 2);
            bool near = Math.Abs(p.X - _lastDownPoint.X) <= tolX && Math.Abs(p.Y - _lastDownPoint.Y) <= tolY;
            bool quick = _lastDownAt != DateTime.MinValue &&
                         now.Subtract(_lastDownAt).TotalMilliseconds <= SystemInformation.DoubleClickTime;

            if (_clickCount == 0 || !near || !quick) _clickCount = 1;
            else if (_clickCount < 3) _clickCount = _clickCount + 1;   // 第二下 = 双击，第三下 = 三击
            else _clickCount = 1;                                      // 第四下重新算

            _lastDownAt = now;
            _lastDownPoint = p;
        }

        private void OnGlobalMouseDown(Point p)
        {
            // 任何新的按下都作废还没执行的延迟取词：
            // 「划完马上点别处」绝不能在 130ms / 150ms 后再补发 Ctrl+C 或弹出旧选区
            _selectionTimer.Stop();
            _multiClickTimer.Stop();
            _verticalDrag = false;
            // 新的按下 = 用户已经开始下一个动作：作废还在后台跑的取词结果，别让旧图标晚到再弹出来
            Interlocked.Increment(ref _selectionSeq);

            _iconIgnoreUntil = DateTime.UtcNow.AddMilliseconds(220);   // 拖动/点击期间别探测
            bool onOurs = IsOurPoint(p);
            if (!onOurs)
            {
                if (_icon.Visible) { _icon.Hide(); _iconSource = ""; }
                if (_card.Visible && !_card.Pinned) _card.Hide();
                BeginDownPointCheck(p);   // F1：落在标题栏 / 滚动条 / 非客户区 → 本次不取词
            }
            else
            {
                ResetDownPointCheck();
            }
        }

        /// <summary>
        /// 记录本次按下落点是否"不该取词"。
        /// 快判在钩子线程里同步做（纯 Win32，微秒级，不拖慢全局鼠标钩子）；
        /// UIA 精确判（标题栏/滚动条/滑块/窗口本体）丢后台线程，mouseup 前一般已就绪。
        /// </summary>
        private void BeginDownPointCheck(Point p)
        {
            bool fastSkip = false;
            try { fastSkip = Selection.IsChromePointFast(p); } catch { }

            int seq;
            lock (_downGate)
            {
                _downSeq++;
                seq = _downSeq;
                _skipSelection = fastSkip;
            }
            if (fastSkip) return;              // 已经确定跳过，不必再查 UIA
            if (!Settings.SelectionMode) return;

            Point down = p;
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    bool uiaSkip = false;
                    try { uiaSkip = Selection.IsChromePointUia(down.X, down.Y, 200); }
                    catch { }
                    if (!uiaSkip) return;
                    lock (_downGate)
                    {
                        if (seq == _downSeq) _skipSelection = true;   // 过期结果不污染新的按下
                    }
                });
            }
            catch { }
        }

        private void ResetDownPointCheck()
        {
            lock (_downGate)
            {
                _downSeq++;
                _skipSelection = false;
            }
        }

        private bool SkipDownPoint()
        {
            lock (_downGate) { return _skipSelection; }
        }

        private void OnGlobalMouseUp(Point p)
        {
            if (!Settings.SelectionMode) return;
            if (IsOurPoint(p)) return;

            int dx = p.X - _downPoint.X;
            int dy = p.Y - _downPoint.Y;
            if (dx * dx + dy * dy < 60)                                // 位移≈0：点击类
            {
                // 双击第二下 / 三击第三下：选区已经在这次 mouseup 后落定（整词 / 整段），
                // 延迟 150ms 再取词，避开程序自己处理选区的那几帧。
                if (_clickCount >= 2 && _running && !SkipDownPoint())
                {
                    _lastPos = p;
                    _multiClickTimer.Stop();
                    _multiClickTimer.Start();
                }
                return;
            }

            _clickCount = 0;   // 拖拽打断连击序列

            // 只有"几乎笔直往下拖很长一段"才当成拖滚动条（选长段落也会往下拖，不能一刀切）
            _verticalDrag = (Math.Abs(dx) < Dpi.S(6) && Math.Abs(dy) > Dpi.S(150));

            if (SkipDownPoint())
            {
                Log("selection skipped: down point on title bar / scroll bar / non-client");
                return;
            }

            _lastPos = p;
            _selectionTimer.Stop();
            _selectionTimer.Start();
        }

        private void OnMultiClickCapture()
        {
            if (!_running || !Settings.SelectionMode) return;
            if (SkipDownPoint()) return;
            CaptureSelection(_lastPos);
        }

        private bool IsOurPoint(Point p)
        {
            try
            {
                if (_icon.Visible && _icon.Bounds.Contains(p)) return true;
                if (_card.Visible && _card.Bounds.Contains(p)) return true;
                // 记录窗口也是我们自己的窗口：点它不该触发取词
                if (_records != null && _records.Visible && _records.Bounds.Contains(p)) return true;
            }
            catch { }
            return false;
        }

        // ---------------- 划词取词 ----------------

        /// <summary>
        /// 划词取词入口（F5）：UIA 查询放到后台线程（以前在 UI 线程最长堵 600ms），
        /// 结果回 UI 线程；_selectionSeq 保证过期结果不会弹出来。
        /// </summary>
        private void CaptureSelection(Point p)
        {
            if (!_running) return;
            if (SkipDownPoint()) return;

            long seq = Interlocked.Increment(ref _selectionSeq);
            Point keep = p;
            // R1：记下 mouseup（此刻）的前台窗口。后台兜底发 Ctrl+C 前要再核对一次——
            // 用户松手后 100~300ms 内点到别的窗口（典型是终端）时，Ctrl+C 不能打到新前台窗口上。
            IntPtr fgHwnd = Native.GetForegroundWindow();

            ThreadPool.QueueUserWorkItem(delegate
            {
                string text = string.Empty;
                try { text = Selection.GetSelectedText(600); }
                catch { }

                if (!string.IsNullOrEmpty(text))
                {
                    try
                    {
                        _ui.BeginInvoke(new Action(delegate
                        {
                            if (seq != _selectionSeq || !_running) return;   // 过期 / 已停止
                            ShowSelectionIcon(text, keep, "uia");
                        }));
                    }
                    catch { }
                    return;
                }

                // UIA 取不到（Wiley 这类不暴露文本层的网页很常见）→ 后台线程模拟 Ctrl+C
                if (!Settings.ClipboardFallback) return;
                if (_verticalDrag) return;                 // 疑似拖滚动条，不碰剪贴板
                if (Native.IsLeftButtonDown()) return;
                // R1：请求已过期（用户已开始下一个动作 / 点了别处）或程序已停止 → 绝不碰键盘
                if (!_running || seq != _selectionSeq) return;
                if (fgHwnd == IntPtr.Zero) return;         // 拿不到前台窗口就无法做发键前校验，放弃
                if (Selection.ForegroundBlocksClipboardInjection())
                {
                    Log("clipboard fallback skipped: foreground window is elevated / UIPI protected");
                    return;
                }

                // expectedHwnd + validator：发 Ctrl+C 前 Selection 会再核对一次前台窗口和请求有效性（R1）
                Selection.CopySelectionAsync(delegate (string got)
                {
                    if (string.IsNullOrEmpty(got)) return;
                    try
                    {
                        _ui.BeginInvoke(new Action(delegate
                        {
                            if (seq != _selectionSeq || !_running) return;
                            ShowSelectionIcon(got, keep, "clipboard");
                        }));
                    }
                    catch { }
                }, 800, fgHwnd, delegate
                {
                    return _running && seq == _selectionSeq;
                });
            });
        }

        private void ShowSelectionIcon(string text, Point p, string source)
        {
            if (string.IsNullOrEmpty(text)) return;
            bool truncated = text.Length > 1200;      // F4：记下这次原文是否被截断
            if (truncated) text = text.Substring(0, 1200);
            _selectionTruncated = truncated;
            _selectionText = text;

            Log(string.Format("select ({0},{1}) {2} len={3}", p.X, p.Y, source, text.Length));

            _hoverLine = text;
            _suppressHoverUntil = DateTime.UtcNow.AddMilliseconds(4000);
            _iconShownAt = DateTime.UtcNow;
            _iconSource = "selection";
            _icon.ShowAt(new Point(p.X + 16, p.Y + 18), text);
        }

        // ---------------- 悬停取词 ----------------

        private void HoverTick()
        {
            DateTime now = DateTime.UtcNow;

            if (_icon.Visible && now.Subtract(_iconShownAt).TotalSeconds > 8) _icon.Hide();
            if (!Settings.HoverMode) return;

            // 拖动中、刚点完、卡片在前台时都不探测——这是"卡"和"乱弹"的主要来源
            if (_dragButtonDown) return;
            if (now < _iconIgnoreUntil) return;
            if (_probing) return;
            if (now < _suppressHoverUntil) return;

            Point p = Native.GetCursorPosition();
            if (p != _lastPos)
            {
                _lastPos = p;
                _idleStart = now;
                return;
            }

            if (now.Subtract(_idleStart).TotalMilliseconds < Settings.HoverDelayMs) return;
            if (IsOurPoint(p)) return;

            // 同一个点只探一次；挪开 16px 以上才重新探，避免停在那儿 4Hz 反复查
            double moved = Math.Sqrt(
                (p.X - _lastProbePoint.X) * (double)(p.X - _lastProbePoint.X) +
                (p.Y - _lastProbePoint.Y) * (double)(p.Y - _lastProbePoint.Y));
            if (moved < 16 && now.Subtract(_lastProbeAt).TotalSeconds < 6) return;

            if (Settings.HoverNeedAlt && !Native.IsAltDown()) return;

            if (now.Subtract(_fgProcAt).TotalMilliseconds > 900)
            {
                _fgProc = Selection.ForegroundProcessName();
                _fgProcAt = now;
            }
            if (_fgProc.Equals("MouseTranslate", StringComparison.OrdinalIgnoreCase)) return;

            _probing = true;
            _lastProbePoint = p;
            _lastProbeAt = now;
            Point probePoint = p;
            ThreadPool.QueueUserWorkItem(delegate { DoProbe(probePoint); });
        }

        private void DoProbe(Point p)
        {
            string word = string.Empty;
            string line = string.Empty;
            string source = string.Empty;

            try
            {
                // ① 一次 UIA 调用同时取词和整行
                PointText pt = Selection.GetTextAtPoint(p.X, p.Y, 280);
                string uiaWord = Selection.NormalizeWord(pt.Word);
                if (Selection.LooksLikeWord(uiaWord))
                {
                    word = uiaWord;
                    line = pt.Line.Trim();
                    source = "uia";
                }

                // ② UIA 不给文本（PDF 阅读器、图片、视频、Canvas 渲染的页面）→ 屏幕 OCR
                if (word.Length == 0)
                {
                    DateTime now = DateTime.UtcNow;
                    if (now.Subtract(_lastOcrAt).TotalMilliseconds > 260)
                    {
                        _lastOcrAt = now;
                        EnsureOcr();
                        // 区域比 v1 更大，兼顾逐行密集的论文排版
                        OcrResult r = _ocr.CaptureAndRecognize(p.X - 110, p.Y - 34, 620, 104);
                        if (r.Ok)
                        {
                            OcrWord w = r.WordAtScreenPoint(p.X, p.Y);
                            string ocrWord = w != null ? Selection.NormalizeWord(w.Text) : string.Empty;
                            if (Selection.LooksLikeWord(ocrWord))
                            {
                                word = ocrWord;
                                OcrLine l = r.LineOf(w);
                                if (l != null) line = l.Text.Trim();
                                source = "ocr";
                            }
                            else if (Selection.LooksLikeSentence(r.Text))
                            {
                                // 定位不到具体单词，但有文字：用最近的一行，至少能翻句子
                                OcrLine nearest = NearestLine(r, p);
                                if (nearest != null && Selection.LooksLikeSentence(nearest.Text))
                                {
                                    line = nearest.Text.Trim();
                                    word = line.Length > 120 ? line.Substring(0, 120) : line;
                                    source = "ocr-line";
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            finally { _probing = false; }

            if (word.Length == 0) return;
            if (word.Length > 400) word = word.Substring(0, 400);

            Log(string.Format("probe ({0},{1}) {2} word='{3}' line='{4}'", p.X, p.Y, source,
                Shorten(word, 40), Shorten(line, 60)));

            string w2 = word;
            string l2 = line;
            try
            {
                _ui.BeginInvoke(new Action(delegate
                {
                    if (!_running) return;
                    if (IsOurPoint(p)) return;
                    // 划词产生的图标优先，别被悬停探测覆盖成单个单词
                    if (_icon.Visible && _iconSource == "selection") return;
                    _iconSource = "hover";
                    _hoverLine = l2;
                    _iconShownAt = DateTime.UtcNow;
                    _icon.ShowAt(new Point(p.X + 16, p.Y + 18), w2);
                }));
            }
            catch { }
        }

        /// <summary>OCR 里离光标最近的一行。</summary>
        internal static OcrLine NearestLine(OcrResult r, Point p)
        {
            OcrLine best = null;
            int bestDist = int.MaxValue;
            foreach (OcrLine l in r.Lines)
            {
                int cy = r.OriginY + l.Y + l.H / 2;
                int dist = Math.Abs(cy - p.Y);
                if (dist < bestDist) { bestDist = dist; best = l; }
            }
            return bestDist <= 60 ? best : null;
        }

        internal static string Shorten(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\n", " ").Replace("\r", " ");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>可选的调试日志（面板里勾选后才会写）。</summary>
        private void Log(string msg)
        {
            if (!Settings.DebugLog) return;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseTranslate");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }

        private void EnsureOcr()
        {
            if (_ocrStarted && _ocr.Ready) return;
            _ocrStarted = true;
            bool ok = _ocr.Start();
            _ocrState = ok ? ("可用（" + "Windows OCR" + "）") : ("不可用：" + _ocr.LastError);
            try { _ui.BeginInvoke(new Action(RefreshCapabilities)); } catch { }
        }

        // ---------------- 翻译 ----------------

        /// <summary>把词条压成一行，供历史记录显示。</summary>
        private static string DefSummary(DictEntry d)
        {
            if (d == null || !d.Ok) return d != null ? d.Error : string.Empty;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < d.Groups.Count && i < 2; i++)
            {
                if (i > 0) sb.Append(" / ");
                if (d.Groups[i].Pos.Length > 0) sb.Append(d.Groups[i].Pos).Append(' ');
                for (int j = 0; j < d.Groups[i].Senses.Count && j < 3; j++)
                {
                    if (j > 0) sb.Append('、');
                    sb.Append(d.Groups[i].Senses[j]);
                }
            }
            return sb.ToString();
        }

        private void TranslatePayload(string text)
        {
            TranslatePayload(text, Rectangle.Empty);
        }

        private void TranslatePayload(string text, Rectangle anchorOverride)
        {
            if (string.IsNullOrEmpty(text)) return;

            Rectangle anchor = anchorOverride.IsEmpty ? _icon.Bounds : anchorOverride;
            _icon.Hide();

            string payload = text.Trim();
            bool isWord = IsSingleWord(payload);
            long seq = ++_translateSeq;               // F2：本次请求号，晚到的旧结果直接丢弃
            bool truncated = _selectionTruncated &&
                             string.Equals(payload, _selectionText, StringComparison.Ordinal);   // F4
            _lastTruncated = false;

            try
            {
                _card.ShowLoading(payload, isWord);
                _card.ShowNear(anchor);
            }
            catch (Exception ex)
            {
                Program.WriteCrash("ShowCard", ex);
                return;
            }

            string target = Translator.TargetFor(payload);

            ThreadPool.QueueUserWorkItem(delegate
            {
                if (isWord)
                {
                    DictEntry d;
                    try { d = _translator.LookupAsync(payload).GetAwaiter().GetResult(); }
                    catch (Exception ex)
                    {
                        d = new DictEntry();
                        d.Word = payload;
                        d.Ok = false;
                        d.Error = ex.Message;
                    }
                    DictEntry dd = d;
                    try
                    {
                        _ui.BeginInvoke(new Action(delegate
                        {
                            if (seq != _translateSeq) return;   // 旧请求晚到：不许覆盖新卡片
                            if (!_card.Visible) return;
                            _card.ShowWord(payload, dd);
                            _lastTruncated = truncated;
                            _card.ShowTruncatedHint(truncated);
                            if (dd.Ok)   // 失败不写历史：查不到词的错误不该进翻译记录
                            {
                                try { Store.AddHistory("word", payload, DefSummary(dd), dd.Provider); } catch { }
                            }
                        }));
                    }
                    catch { }
                    return;
                }

                TranslateResult r;
                try { r = _translator.TranslateAsync(payload, target).GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    r = new TranslateResult();
                    r.Ok = false;
                    r.Error = ex.Message;
                }

                TranslateResult rr = r;
                try
                {
                    _ui.BeginInvoke(new Action(delegate
                    {
                        if (seq != _translateSeq) return;   // 旧请求晚到：不许覆盖新卡片
                        if (!_card.Visible) return;
                        _card.ShowSentence(payload, rr);
                        _lastTruncated = truncated;
                        _card.ShowTruncatedHint(truncated);
                        if (rr.Ok)   // 失败不写历史
                        {
                            try { Store.AddHistory("sentence", payload, rr.Text, rr.Provider); } catch { }
                        }
                    }));
                }
                catch { }
            });
        }

        /// <summary>是不是"单个词"（决定走词典还是走翻译）。</summary>
        internal static bool IsSingleWord(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 32) return false;
            if (s.IndexOf(' ') >= 0 || s.IndexOf('\n') >= 0) return false;
            if (Translator.IsChinese(s)) return false;
            bool letter = false;
            foreach (char c in s)
            {
                if (char.IsLetter(c)) { letter = true; continue; }
                if (c == '-' || c == '\'' || c == '’' || c == '.') continue;
                return false;
            }
            return letter;
        }

        public void RunChannelTest()
        {
            long seq = ++_translateSeq;   // F2：测试卡片也算一次新请求，作废还在路上的旧翻译结果
            _form.SetChannelStatus("正在测试翻译通道…");
            ThreadPool.QueueUserWorkItem(delegate
            {
                TranslateResult r;
                try { r = _translator.TranslateAsync("Hello, world. This is a test of the translation channel.", "zh-CN").GetAwaiter().GetResult(); }
                catch (Exception ex)
                {
                    r = new TranslateResult();
                    r.Ok = false;
                    r.Error = ex.Message;
                }
                TranslateResult rr = r;
                try
                {
                    _ui.BeginInvoke(new Action(delegate
                    {
                        if (seq != _translateSeq) return;   // 期间用户已经发起了新的翻译，别覆盖
                        if (rr.Ok)
                        {
                            _form.SetChannelStatus("翻译通道正常：" + rr.Provider + " · " + rr.Ms + " ms\n" + rr.Text);
                            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                            _card.ShowLoading("Hello, world. This is a test of the translation channel.", false);
                            _card.ShowNear(new Rectangle(wa.Left + wa.Width / 2 - 40, wa.Top + 140, 28, 28));
                            _card.ShowSentence("Hello, world. This is a test of the translation channel.", rr);
                        }
                        else
                        {
                            _form.SetChannelStatus("翻译通道失败：" + rr.Error);
                        }
                    }));
                }
                catch { }
            });
        }

        private void RefreshCapabilities()
        {
            if (!_ocrStarted) { _ocrState = "可用（按需启动）"; }
            string uia = "可用";
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null) uia = "未就绪";
            }
            catch { uia = "异常"; }

            string text =
                "UI Automation 取词：" + uia + "\n" +
                "屏幕 OCR 取词：" + _ocrState + "\n" +
                "全局鼠标钩子：" + (_hookId != IntPtr.Zero ? "已挂载" : "未挂载") + "\n" +
                "说明：UIA 覆盖浏览器 / Office / 大多原生应用；OCR 兜底图片、视频、游戏等无文本接口的画面。";

            _form.SetCapabilities(text);
        }

        public void OpenConfigFolder()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseTranslate");
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", dir);
            }
            catch { }
        }
    }
}
