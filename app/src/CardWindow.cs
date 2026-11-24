using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>WebView2 卡片：玻璃底 + CSS 排版，高度由页面测量后回报。</summary>
    internal sealed class CardWindow : Form
    {
        private const int W = 430;
        private const int H_MIN = 80;              // 卡片最小高度（DIP）
        private const int GAP = 10;                // 卡片与取词锚点的间距
        private const int EDGE = 8;                // 贴屏幕边缘时至少留这么多
        private const double H_MAX_RATIO = 0.60;   // 卡片最高占工作区 60%，再长就在卡片内部滚动
        private const int SizeAnimMs = 180;        // 高度过渡总时长
        private const int SizeAnimIntervalMs = 15; // 逐帧步长（12 帧 ≈ 180ms）

        private readonly WebPanel _web;
        private readonly Timer _sizeAnim;          // 高度/位置逐帧插值（结果回来不再硬跳）
        private Rectangle _sizeFrom;
        private Rectangle _sizeTo;
        private int _sizeElapsed;
        private bool _snapNextHeight = true;       // 刚出现后的第一次测量直接落位（窗口正在淡入）
        private string _payload = "{}";
        private string _bg = string.Empty;         // 最近一次按卡片最终矩形抓的屏幕背景（base64）
        private bool _hasBg;                       // 抓帧是否成功（失败 → 纯色兜底）
        private int _mode = 0;                     // 玻璃档位：0 标准 / 1 加厚 / 2 纯色（WebUi.PickCardMode）
        private bool _truncated;                   // 原文被截断 → 卡片底部显示提示条
        private Rectangle _anchor;
        private bool _ready;
        private int _lastCssHeight;
        private string _lastWord = string.Empty;
        private string _lastPhonetic = string.Empty;
        private string _lastDefs = string.Empty;
        private string _lastSentence = string.Empty;
        private bool _isSentence;

        public bool Pinned { get; private set; }

        /// <summary>点卡片上的「记录」时回调（参数为标签页：history / vocab）。</summary>
        public Action<string> RecordsRequested;

        public CardWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "翻译";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(20, 22, 27);
            Width = Dpi.S(W);
            Height = Dpi.S(140);

            _web = new WebPanel();
            _web.Dock = DockStyle.Fill;
            _web.Message += OnMessage;
            Controls.Add(_web);

            _sizeAnim = new Timer();
            _sizeAnim.Interval = SizeAnimIntervalMs;
            _sizeAnim.Tick += delegate { SizeAnimTick(); };

            Tts.StateChanged += delegate (string state)
            {
                // 契约（与 card.html 三态按钮并行）：loading / playing / idle
                // Tts 可能在后台 STA 线程触发，必须切回 UI 线程操作 WebView
                try
                {
                    if (IsDisposed || !IsHandleCreated) return;
                    BeginInvoke(new Action(delegate
                    {
                        try { _web.RunScript("window.__setSpeak && window.__setSpeak('" + state + "');"); } catch { }
                    }));
                }
                catch { }
            };
        }

        // ---------------- 对外接口（与旧 CardForm 对齐） ----------------

        public void ShowLoading(string source, bool wordMode)
        {
            Tts.Stop();             // 新取词一律先静音：固定卡片/悬停路径下卡片还开着，旧朗读不能继续读旧内容
            Tts.Prefetch(source);   // 卡片刚出现就预取，给用户点击留出时间差
            _truncated = false;     // 新一轮翻译：先收掉上一轮的截断提示（结果回来再按需打开）
            if (wordMode)
                _payload = "{\"mode\":\"word\",\"word\":" + Q(source) + ",\"phonetic\":\"\",\"groups\":[],\"provider\":\"\",\"ms\":0,\"pending\":true}";
            else
                _payload = "{\"mode\":\"sentence\",\"source\":" + Q(source) + ",\"translation\":\"\",\"meta\":\"\",\"pending\":true}";
            PushPayload();
            PushTruncated();
            PushSpeakState();       // Stop 已抛 idle，这里把按钮状态跟 Tts 对齐（新卡片不残留上一轮的三态）
            _web.RunScript("window.__setStar && window.__setStar(false);");   // 换了新词：收藏态先复位
        }

        public void ShowWord(string word, DictEntry d)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"mode\":\"word\",\"word\":").Append(Q(word));
            sb.Append(",\"phonetic\":").Append(Q(d != null ? d.Phonetic : string.Empty));
            sb.Append(",\"groups\":[");
            if (d != null && d.Ok)
            {
                for (int i = 0; i < d.Groups.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"pos\":").Append(Q(d.Groups[i].Pos)).Append(",\"senses\":[");
                    for (int j = 0; j < d.Groups[i].Senses.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        sb.Append(Q(d.Groups[i].Senses[j]));
                    }
                    sb.Append("]}");
                }
            }
            sb.Append("],\"provider\":").Append(Q(d != null ? d.Provider : string.Empty));
            sb.Append(",\"ms\":").Append(d != null ? d.Ms : 0);
            sb.Append(",\"ok\":").Append(d != null && d.Ok ? "true" : "false");
            sb.Append('}');
            _payload = sb.ToString();
            _lastWord = word;
            _lastPhonetic = d != null ? d.Phonetic : string.Empty;
            _lastDefs = BuildDefs(d);
            _lastSentence = string.Empty;
            _isSentence = false;
            PushPayload();
            _web.RunScript("window.__inResult && window.__inResult();");   // 结果到达：内容 120ms 淡入（不缩放）
            Tts.Prefetch(word);                                   // 预取音频，点朗读几乎零等待
            _web.RunScript("window.__setStar && window.__setStar(" + (Store.IsStarred(word) ? "true" : "false") + ");");
            PushSpeakState();                                     // 内容重建会把按钮重置成 idle，按 Tts 真实状态补一次
        }

        private static string BuildDefs(DictEntry d)
        {
            if (d == null || !d.Ok) return string.Empty;
            StringBuilder sb = new StringBuilder();
            foreach (DictSense g in d.Groups)
            {
                if (g.Pos.Length > 0) sb.Append(g.Pos).Append(' ');
                for (int i = 0; i < g.Senses.Count; i++)
                {
                    if (i > 0) sb.Append("；");
                    sb.Append(g.Senses[i]);
                }
                sb.AppendLine();
            }
            return sb.ToString().Trim();
        }

        public void ShowSentence(string source, TranslateResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"mode\":\"sentence\",\"source\":").Append(Q(source));
            sb.Append(",\"translation\":").Append(Q(r != null && r.Ok ? r.Text : (r != null ? r.Error : "翻译失败")));
            string meta = r != null && r.Ok
                ? Translator.DirectionLabel(source) + (r.Provider.Length > 0 ? " · " + r.Provider : "") + " · " + r.Ms + " ms"
                : string.Empty;   // 失败态的说明在 .lg-error 里，页脚不再挂一个没意义的 “0 ms”
            sb.Append(",\"meta\":").Append(Q(meta));
            sb.Append(",\"ok\":").Append(r != null && r.Ok ? "true" : "false");
            sb.Append('}');
            _payload = sb.ToString();
            _lastSentence = source ?? string.Empty;
            _lastWord = source ?? string.Empty;
            Tts.Prefetch(source ?? string.Empty);                  // 预取原文音频
            _lastPhonetic = string.Empty;
            _lastDefs = r != null && r.Ok ? r.Text : string.Empty;
            _isSentence = true;
            PushPayload();
            _web.RunScript("window.__inResult && window.__inResult();");   // 结果到达：内容 120ms 淡入（不缩放）
            _web.RunScript("window.__setStar && window.__setStar(" + (Store.IsStarred(source ?? string.Empty) ? "true" : "false") + ");");
            PushSpeakState();                                     // 内容重建会把按钮重置成 idle，按 Tts 真实状态补一次
        }

        public void TogglePin()
        {
            Pinned = !Pinned;
            _web.RunScript("window.__setPin && window.__setPin(" + (Pinned ? "true" : "false") + ");");
        }

        public void ResetPin()
        {
            Pinned = false;
            _web.RunScript("window.__setPin && window.__setPin(false);");
        }

        // ---------------- 定位与玻璃背景 ----------------

        /// <summary>算出卡片最终应该待在的矩形（x/y/w/h）。页面测量出高度之前，先用当前高度估。</summary>
        private Rectangle ComputeBounds(int height)
        {
            Rectangle wa = Screen.FromRectangle(_anchor).WorkingArea;
            int w = Dpi.S(W);
            int maxH = (int)(wa.Height * H_MAX_RATIO);
            int minH = Dpi.S(H_MIN);
            if (maxH < minH) maxH = minH;                 // 屏幕再矮也留一个最小可读高度
            if (height > maxH) height = maxH;
            if (height < minH) height = minH;

            int x = _anchor.Right + Dpi.S(GAP);
            if (x + w > wa.Right - Dpi.S(EDGE)) x = _anchor.Left - w - Dpi.S(GAP);
            if (x < wa.Left + Dpi.S(EDGE)) x = wa.Left + Dpi.S(EDGE);
            if (x + w > wa.Right - Dpi.S(EDGE)) x = wa.Right - w - Dpi.S(EDGE);

            int y = _anchor.Top - Dpi.S(8);
            if (y + height > wa.Bottom - Dpi.S(EDGE)) y = wa.Bottom - height - Dpi.S(EDGE);
            if (y < wa.Top + Dpi.S(EDGE)) y = wa.Top + Dpi.S(EDGE);
            return new Rectangle(x, y, w, height);
        }

        /// <summary>把矩形夹回工作区：固定卡片改高度 / 被拖到屏幕外时用，保证不出屏。</summary>
        private Rectangle ClampToWorkArea(Rectangle b)
        {
            Rectangle wa = Screen.FromRectangle(b).WorkingArea;
            int maxH = (int)(wa.Height * H_MAX_RATIO);
            int minH = Dpi.S(H_MIN);
            if (maxH < minH) maxH = minH;
            if (b.Height > maxH) b.Height = maxH;
            if (b.Height < minH) b.Height = minH;

            int maxW = wa.Width - Dpi.S(EDGE) * 2;
            if (maxW < Dpi.S(120)) maxW = Dpi.S(120);
            if (b.Width > maxW) b.Width = maxW;

            if (b.Right > wa.Right - Dpi.S(EDGE)) b.X = wa.Right - Dpi.S(EDGE) - b.Width;
            if (b.X < wa.Left + Dpi.S(EDGE)) b.X = wa.Left + Dpi.S(EDGE);
            if (b.Bottom > wa.Bottom - Dpi.S(EDGE)) b.Y = wa.Bottom - Dpi.S(EDGE) - b.Height;
            if (b.Y < wa.Top + Dpi.S(EDGE)) b.Y = wa.Top + Dpi.S(EDGE);
            return b;
        }

        private void ApplyBounds(Rectangle b)
        {
            if (Bounds != b) SetBounds(b.X, b.Y, b.Width, b.Height);
            Theme.ApplyRoundRegion(this, Dpi.S(20));   // 与 card.html 的 border-radius:20px 对齐
        }

        /// <summary>
        /// 结果到达时的高度过渡：15ms 一帧、总时长 180ms、ease-out 1-(1-p)^3，
        /// 每帧同时插值高度与 y 并重设圆角区域 —— 取代以前一次 SetBounds 的硬跳。
        /// </summary>
        private void AnimateBounds(Rectangle target)
        {
            if (Bounds == target) { ApplyBounds(target); return; }
            _sizeFrom = Bounds;
            _sizeTo = target;
            _sizeElapsed = 0;
            try
            {
                if (!_sizeAnim.Enabled) _sizeAnim.Start();
            }
            catch
            {
                ApplyBounds(target);
            }
        }

        private void SizeAnimTick()
        {
            try
            {
                _sizeElapsed += SizeAnimIntervalMs;
                double p = Math.Min(1.0, _sizeElapsed / (double)SizeAnimMs);
                double e = 1.0 - Math.Pow(1.0 - p, 3.0);
                if (p >= 1.0)
                {
                    _sizeAnim.Stop();
                    ApplyBounds(_sizeTo);
                    return;
                }
                int x = _sizeFrom.X + (int)Math.Round((_sizeTo.X - _sizeFrom.X) * e);
                int y = _sizeFrom.Y + (int)Math.Round((_sizeTo.Y - _sizeFrom.Y) * e);
                int w = _sizeFrom.Width + (int)Math.Round((_sizeTo.Width - _sizeFrom.Width) * e);
                int h = _sizeFrom.Height + (int)Math.Round((_sizeTo.Height - _sizeFrom.Height) * e);
                ApplyBounds(new Rectangle(x, y, Math.Max(1, w), Math.Max(1, h)));
            }
            catch
            {
                try { _sizeAnim.Stop(); } catch { }
                try { ApplyBounds(_sizeTo); } catch { }
            }
        }

        /// <summary>
        /// 按卡片最终矩形抓屏 → 量亮度/对比度/饱和度 → base64。
        /// 玻璃必须能显示“卡片背后那一块桌面”：CSS backdrop-filter 只能模糊页面内部的层，
        /// 模糊不到窗口背后的桌面，所以本平台用「抓帧贴图」等价实现 CSS 的 backdrop-filter。
        /// 卡片自己正压在这块区域上时先藏一帧再抓，否则玻璃里会印出上一张卡片
        /// （罕见路径：卡片可见时从记录窗口/图标再翻一次）。
        /// </summary>
        private void CaptureBackdrop(Rectangle cardRect)
        {
            _hasBg = false;
            _bg = string.Empty;
            _mode = 2;   // 抓帧失败/被裁得太小时按纯色兜底，绝不露出宿主窗口底色
            try
            {
                Rectangle cap = cardRect;
                cap.Intersect(Screen.FromRectangle(cardRect).WorkingArea);
                if (cap.Width < Dpi.S(60) || cap.Height < Dpi.S(40)) return;   // 基本在屏幕外：纯色兜底

                if (Visible && cap.IntersectsWith(Bounds))
                {
                    Hide();
                    try { System.Threading.Thread.Sleep(24); } catch { }      // 等桌面把让出来的那块重绘一帧
                }

                Bitmap bg = Glass.Capture(cap);
                if (bg == null) return;

                try
                {
                    BackdropStats st = WebUi.Analyze(bg);
                    _mode = WebUi.PickCardMode(Theme.Dark, st);
                    _bg = WebUi.ToBase64(bg);
                    _hasBg = _bg.Length > 0;
                    LogGlass(st);
                }
                finally
                {
                    bg.Dispose();
                }
                if (!_hasBg) _mode = 2;
            }
            catch
            {
                _hasBg = false;
                _bg = string.Empty;
                _mode = 2;
            }
        }

        private void PushBackground()
        {
            if (!_ready) return;
            // 一次调用同时热更新贴图 + 档位，避免中间帧出现“旧档位配新贴图”
            if (_hasBg)
                _web.RunScript("window.__setGlass && window.__setGlass('" + _bg + "', " + _mode.ToString() + ");");
            else
                _web.RunScript("window.__setGlass && window.__setGlass('', " + _mode.ToString() + ");");
        }

        /// <summary>自检用：设置 MT_GLASS_LOG 后把每次抓帧统计写进文件（默认不写）。</summary>
        private void LogGlass(BackdropStats st)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("MT_GLASS_LOG");
                if (string.IsNullOrEmpty(path)) return;
                int worstDark = Math.Min(255, st.P95 + 8);
                int worstLight = Math.Max(0, st.P5 - 8);
                string line = string.Format(
                    "dark={0} mode={1} mean={2} p5={3} p95={4} sd={5} sat={6} n={7} " +
                    "contrast(primary)~{8:0.00} contrast(secondary)~{9:0.00}",
                    Theme.Dark, _mode, st.Mean, st.P5, st.P95, st.StdDev, st.MeanSat, st.Samples,
                    WebUi.PrimaryContrast(Theme.Dark, _mode, Theme.Dark ? worstDark : worstLight),
                    WebUi.SecondaryContrast(Theme.Dark, _mode, Theme.Dark ? worstDark : worstLight));
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }

        private void PushTruncated()
        {
            if (!_ready) return;
            _web.RunScript("window.__setTruncated && window.__setTruncated(" + (_truncated ? "true" : "false") + ");");
        }

        public void ShowNear(Rectangle anchor)
        {
            _anchor = anchor;
            bool wasVisible = Visible;
            bool stayPinned = Pinned && wasVisible;   // 已固定的卡片钉在原地，不跟着新取词跑

            if (stayPinned)
            {
                // 位置不动、背景不重抓（卡片就压在这块屏幕上，抓到的会是它自己），只把位置夹回工作区
                Rectangle pinned = ClampToWorkArea(Bounds);
                if (Bounds != pinned) ApplyBounds(pinned);
            }
            else
            {
                // 先算出卡片最终 x/y/w/h，按这个矩形抓背景 —— 截图和卡片才是同一块屏幕区域
                Rectangle target = ComputeBounds(Height);
                // 已可见且新旧矩形相交：沿用上一张抓帧玻璃，不 Hide、不重抓，
                // 避免再次取词时整卡消失 60~100ms（唯一会走 Hide 的只剩“卡片真的挪到别处”）
                bool reuseGlass = wasVisible && _hasBg && target.IntersectsWith(Bounds);
                if (!reuseGlass) CaptureBackdrop(target);
                ApplyBounds(target);
            }

            if (!_ready)
            {
                // 首次：背景直接嵌进 HTML（页面还没加载，RunScript 到不了）；页面 ready 后会再补一次
                _web.Html = WebUi.CardShell(Theme.Dark, _bg, _mode);
                _ready = true;
            }
            else
            {
                // 已加载过：只热更新背景/材质，不再整页 Reload，页面脚本状态（朗读、滚动）不丢
                PushBackground();
            }

            if (!Visible)
            {
                Show();
                _snapNextHeight = true;          // 刚出现：窗口正在淡入，第一次高度直接落位，不做高度动画
                if (_ready) _web.RunScript("window.__in && window.__in();");   // 内容 160ms 入场（窗口只淡入不位移）
                UiAnim.FadeIn(this, 160, 0);
            }
            else
            {
                // 已可见再取词：窗口原地不动，只播内容 160ms 入场；结果到达再走 120ms 淡入
                _web.RunScript("window.__in && window.__in();");
                TopMost = true;
                Invalidate();
            }
        }

        private void PushPayload()
        {
            if (!_ready) return;
            _web.RunScript("window.__setData && window.__setData(" + _payload + ");");
        }

        /// <summary>
        /// 把 Tts 当前状态重推给页面按钮（loading/playing/idle）。
        /// 场景：卡片内容重建（ShowWord/ShowSentence）或页面 reload 后，按钮的本地三态会被重置，
        /// 但朗读可能仍在进行——必须以 Tts 的真实状态为准，否则会出现"底栏显示 idle、声音还在读"。
        /// </summary>
        private void PushSpeakState()
        {
            if (!_ready) return;
            string state = Tts.LastState;
            if (state != "loading" && state != "playing") state = "idle";
            _web.RunScript("window.__setSpeak && window.__setSpeak('" + state + "');");
        }

        private static string Q(string s)
        {
            if (s == null) s = string.Empty;
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append(" "); break;
                    default:
                        if (c < 32) sb.Append(' ');
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ---------------- 页面消息 ----------------

        private void OnMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> m = ser.DeserializeObject(raw) as Dictionary<string, object>;
                if (m == null) return;
                string type = m.ContainsKey("type") ? Convert.ToString(m["type"]) : string.Empty;

                switch (type)
                {
                    case "ready":
                        PushPayload();
                        PushBackground();   // 首次 HTML 里的背景可能已过期：页面一就绪就补成最新
                        PushTruncated();
                        PushSpeakState();   // 页面 reload 会丢掉按钮三态：以 Tts 真实状态为准补一次
                        if (Pinned) _web.RunScript("window.__setPin && window.__setPin(true);");
                        _web.RunScript("window.__in && window.__in();");   // 首屏内容 160ms 入场
                        break;
                    case "height":
                        {
                            int css = m.ContainsKey("v") ? Convert.ToInt32(m["v"]) : 0;
                            if (css <= 0) return;
                            if (Math.Abs(css - _lastCssHeight) < 2) return;
                            _lastCssHeight = css;

                            Rectangle target;
                            if (Pinned && Visible)
                            {
                                // 固定分支：只改高度，左上角尽量保持不动，但必须夹回工作区
                                Rectangle b = Bounds;
                                b.Height = Dpi.S(css);
                                target = ClampToWorkArea(b);
                            }
                            else
                            {
                                target = ComputeBounds(Dpi.S(css));
                            }

                            if (!Visible || _snapNextHeight)
                            {
                                _snapNextHeight = false;
                                ApplyBounds(target);    // 隐藏中 / 刚出现：直接落位
                            }
                            else
                            {
                                AnimateBounds(target);  // 结果回来：180ms 逐帧插值高度 + y，不再硬跳
                            }
                            break;
                        }
                    case "close": CloseCard(); break;
                    case "star":
                        if (_lastWord.Length > 0)
                        {
                            Store.AddVocab(_lastWord, _lastPhonetic, _lastDefs,
                                _isSentence ? _lastWord : _lastSentence,
                                _isSentence ? "sentence" : "word");
                            _web.RunScript("window.__setStar && window.__setStar(true);");
                            _web.RunScript("window.__toastStar && window.__toastStar();");
                        }
                        break;
                    case "unstar":
                        if (_lastWord.Length > 0)
                        {
                            Store.RemoveVocab(_lastWord);
                            _web.RunScript("window.__setStar && window.__setStar(false);");
                            _web.RunScript("window.__toastUnstar && window.__toastUnstar();");
                        }
                        break;
                    case "records":
                        {
                            string tab = m.ContainsKey("tab") ? Convert.ToString(m["tab"]) : "history";
                            if (RecordsRequested != null) RecordsRequested(tab);
                            break;
                        }
                    case "drag": Native.DragWindow(Handle); break;
                    case "pin":
                        Pinned = !Pinned;
                        _web.RunScript("window.__setPin && window.__setPin(" + (Pinned ? "true" : "false") + ");");
                        break;
                    case "copy":
                        {
                            string text = m.ContainsKey("text") ? Convert.ToString(m["text"]) : string.Empty;
                            bool ok = false;
                            for (int i = 0; i < 3 && !ok; i++)
                            {
                                try { if (text.Length > 0) { Clipboard.SetText(text); ok = true; } }
                                catch { try { System.Threading.Thread.Sleep(16); } catch { } }
                            }
                            // 成功：复制图标切成绿色对勾（1200ms 后复原）；失败：保持原图标 + toast
                            if (ok) _web.RunScript("window.__copyOk && window.__copyOk(true);");
                            _web.RunScript("window.__toastCopy && window.__toastCopy(" + (ok ? "true" : "false") + ");");
                            break;
                        }
                    case "speak":
                        {
                            string text = m.ContainsKey("text") ? Convert.ToString(m["text"]) : string.Empty;
                            if (string.IsNullOrWhiteSpace(text))
                            {
                                // 空/空白文本不要乐观停在 loading：直接回 idle，避免按钮卡在加载态
                                _web.RunScript("window.__setSpeak && window.__setSpeak('idle');");
                                break;
                            }
                            // 契约：立刻切 loading，再调 Tts（Tts 的 StateChanged 会把 loading/playing/idle 推回来）
                            _web.RunScript("window.__setSpeak && window.__setSpeak('loading');");
                            Tts.Speak(text);
                            break;
                        }
                    case "stop":
                        {
                            // 契约：页面的"停止"入口 → 立刻停 MCI/SAPI，UI 切 idle
                            _web.RunScript("window.__setSpeak && window.__setSpeak('idle');");
                            Tts.Stop();
                            break;
                        }
                }
            }
            catch { }
        }

        // ---------------- 关闭与截断提示 ----------------

        /// <summary>关闭的唯一出口：停朗读 + 清固定态 + 隐藏。命中「关闭」按钮和 Esc 都走这里。</summary>
        private void CloseCard()
        {
            try { Tts.Stop(); } catch { }
            ResetPin();
            Hide();
        }

        /// <summary>原文过长（只翻了前 1200 字）时在卡片底部显示提示条；false 则收起。</summary>
        public void ShowTruncatedHint(bool truncated)
        {
            _truncated = truncated;
            PushTruncated();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) return;
            // Hide() 的统一出口：Program 点卡片外部、停翻译、关闭都收敛到这里。
            // 新卡片必须默认未固定（Pinned 会跨次保留，以前忘了清，一出现就是「已固定」）。
            ResetPin();
            try { _sizeAnim.Stop(); } catch { }   // 窗口都藏了，别再逐帧改尺寸
            try { Tts.Stop(); } catch { }   // 卡片一走就停朗读，别让声音留在背后
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _sizeAnim.Stop(); _sizeAnim.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { CloseCard(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
