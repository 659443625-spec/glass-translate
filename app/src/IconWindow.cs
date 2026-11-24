using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>
    /// 光标旁的真玻璃徽标（V3 Apple Liquid Glass）。
    ///
    /// 平台事实：分层窗口（WS_EX_LAYERED + UpdateLayeredWindow）拿不到桌面合成层，
    /// CSS 的 backdrop-filter 也管不到窗口背后的桌面，所以徽标必须自己抓帧：
    /// 显示前抓背后 40x40 DIP 屏幕 → 缩小 + 盒式模糊 + 提饱和 → 叠玻璃色 → 1px 描边 +
    /// 顶部内高光 + 多层柔光外投影，再以逐像素 alpha 贴上去。
    ///
    /// 视觉圆 30 DIP、分层窗口 40 DIP（投影空间）、热区圆 29 DIP；悬停 0.4s 出「点击翻译」气泡。
    /// </summary>
    internal sealed class IconWindow : Form
    {
        private const int AnimFrames = 12;       // 12 × 15ms ≈ 180ms（规范 160~220ms）
        private const int AnimIntervalMs = 15;

        private bool _hover;
        private byte _alpha = 255;
        private readonly IconTip _tip;
        private readonly Timer _tipTimer;

        private Bitmap _frosted;                 // 背后抓帧玻璃（窗口尺寸）
        // 字形颜色：亮内衬画深字 / 暗内衬画白字。默认跟随主题，保证抓帧失败走纯色兜底时也看得见
        private bool _inkDark = !Theme.Dark;

        // 入场：scale(0.92) → 1 + 淡入
        private readonly Timer _anim;
        private int _animStep;
        private double _scale = 1.0;
        private bool _animating;

        public string Payload = string.Empty;
        public event EventHandler Clicked;
        public event EventHandler RightClicked;

        public IconWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = IconArt.HitSize(IconStyle.Badge);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            // 徽标视觉 30 DIP，热区圆 29 DIP —— 悬停给个「点击翻译」提示。
            // 分层窗口上系统 ToolTip 不弹，所以自己画一个小气泡（跟卡片同材质语言）。
            _tip = new IconTip();
            _tipTimer = new Timer();
            _tipTimer.Interval = 420;
            _tipTimer.Tick += delegate
            {
                _tipTimer.Stop();
                ShowTip();
            };

            _anim = new Timer();
            _anim.Interval = AnimIntervalMs;
            _anim.Tick += delegate { AnimTick(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        public void ShowAt(Point p, string payload)
        {
            Payload = payload ?? string.Empty;
            HideTip();
            Size = IconArt.HitSize(IconStyle.Badge);

            Point pt = ClampToScreen(p);
            bool appeared = !Visible;

            if (Visible) Hide();                 // 抓帧前先让开，否则会把自己的旧一帧抓进去
            SetBounds(pt.X, pt.Y, Width, Height);
            CaptureGlass();                      // 抓背后 40x40 → 模糊 → 提饱和 → 叠玻璃色
            Show();
            TopMost = true;

            if (appeared)
            {
                StartAnim();
            }
            else
            {
                _animating = false;
                _scale = 1.0;
                _alpha = 255;
                Push();
            }
        }

        private Point ClampToScreen(Point p)
        {
            Rectangle wa = Screen.FromPoint(p).WorkingArea;
            Size win = IconArt.HitSize(IconStyle.Badge);
            int x = Math.Min(Math.Max(p.X, wa.Left + 2), wa.Right - win.Width - 4);
            int y = Math.Min(Math.Max(p.Y, wa.Top + 2), wa.Bottom - win.Height - 4);
            return new Point(x, y);
        }

        /// <summary>
        /// 真玻璃的来源：显示前抓自己将要占用的那块屏幕。
        /// 抓帧失败（远屏 / 屏幕锁 / CopyFromScreen 抛错）时 _frosted 为 null，
        /// IconArt 会退回高不透明度纯色玻璃 —— 这是兜底红线之一。
        /// </summary>
        private void CaptureGlass()
        {
            DisposeFrosted();
            try
            {
                Rectangle r = new Rectangle(Left, Top, Width, Height);
                r.Intersect(Screen.FromRectangle(r).WorkingArea);
                if (r.Width < Dpi.S(16) || r.Height < Dpi.S(16)) { _inkDark = !Theme.Dark; return; }

                Bitmap raw = Glass.Capture(r);
                if (raw == null) { _inkDark = !Theme.Dark; return; }

                BackdropStats st = WebUi.Analyze(raw);
                double alpha = WebUi.PickIconAlpha(Theme.Dark, st);   // 玻璃底色只负责一点氛围，字形对比交给内衬
                _inkDark = WebUi.PickIconInkDark(Theme.Dark, alpha, st.Mean);
                LogGlass(st, alpha);
                // 1.12 饱和（≤1.15）+ 单次盒式模糊：读作中性玻璃，不是彩色果冻
                Bitmap frosted = Glass.FrostedFrom(raw, Theme.Dark, alpha, 2, 1, 1.12);
                raw.Dispose();
                if (frosted == null) { _inkDark = !Theme.Dark; return; }

                _frosted = frosted;
            }
            catch
            {
                _frosted = null;
                _inkDark = !Theme.Dark;
            }
        }

        /// <summary>自检用：设置 MT_GLASS_LOG 后把徽标抓帧统计写进文件（默认不写）。</summary>
        private void LogGlass(BackdropStats st, double alpha)
        {
            try
            {
                string path = Environment.GetEnvironmentVariable("MT_GLASS_LOG");
                if (string.IsNullOrEmpty(path)) return;
                string line = string.Format(
                    "icon dark={0} alpha={1:0.00} ink={2} scrimLuma={3} glyphContrast={4:0.00} mean={5} p5={6} p95={7} sd={8} sat={9} n={10}",
                    Theme.Dark, alpha, _inkDark ? "dark" : "white",
                    WebUi.IconCompositeLuma(Theme.Dark, alpha, st.Mean),
                    WebUi.IconInkContrast(Theme.Dark, alpha, st.Mean),
                    st.Mean, st.P5, st.P95, st.StdDev, st.MeanSat, st.Samples);
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch { }
        }

        private void DisposeFrosted()
        {
            if (_frosted != null)
            {
                try { _frosted.Dispose(); } catch { }
                _frosted = null;
            }
        }

        private void ShowTip()
        {
            if (!Visible || !_hover) return;
            try
            {
                Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
                // 间距从“视觉圆”的边算起：分层窗口 40 DIP 里视觉圆只有 30 DIP，上下各留 5 DIP 投影空间
                int inset = Dpi.S((IconArt.BadgeWindow - IconArt.BadgeVisual) / 2);
                int y = Bottom - inset + Dpi.S(8);                                // 徽标下方，间距 8 DIP
                if (y + _tip.BubbleHeight > wa.Bottom - Dpi.S(4))
                    y = Top + inset - Dpi.S(8) - _tip.BubbleHeight;               // 空间不够就放上方
                _tip.ShowAt(Left + Width / 2, y);
            }
            catch { }
        }

        private void HideTip()
        {
            try { _tipTimer.Stop(); _tip.Hide(); } catch { }
        }

        // ---------------- 分层绘制 ----------------

        private void Push()
        {
            if (!IsHandleCreated) return;
            try
            {
                int w = Math.Max(8, Width);
                int h = Math.Max(8, Height);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        Rectangle win = new Rectangle(0, 0, w, h);

                        // 热区圆：分层窗口按像素 alpha 命中，用 alpha≈3 把可点范围撑到 29 DIP
                        Size hit = IconArt.HitCircle(IconStyle.Badge);
                        Rectangle hitBox = new Rectangle(
                            (w - hit.Width) / 2, (h - hit.Height) / 2, hit.Width, hit.Height);
                        IconArt.DrawHitArea(g, hitBox);

                        // 视觉圆：30 DIP，入场上浮/悬停微放大
                        Size vis = IconArt.Measure(IconStyle.Badge);
                        double s = _animating ? _scale : (_hover ? 1.03 : 1.0);
                        int vw = Math.Max(8, (int)Math.Round(vis.Width * s));
                        int vh = Math.Max(8, (int)Math.Round(vis.Height * s));
                        Rectangle badge = new Rectangle((w - vw) / 2, (h - vh) / 2, vw, vh);

                        IconArt.DrawBadgeGlass(g, win, badge, _hover, Theme.Dark, _frosted, s, _inkDark);
                    }

                    IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                    IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                    IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    IntPtr old = Native.SelectObject(memDc, hBmp);

                    Native.SIZE size = new Native.SIZE(w, h);
                    Native.POINT src = new Native.POINT(0, 0);
                    Native.POINT dst = new Native.POINT(Left, Top);
                    Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                    blend.BlendOp = 0;
                    blend.BlendFlags = 0;
                    blend.SourceConstantAlpha = _alpha;
                    blend.AlphaFormat = Native.AC_SRC_ALPHA;

                    Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);

                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBmp);
                    Native.DeleteDC(memDc);
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            catch { }
        }

        private void StartAnim()
        {
            _animating = true;
            _animStep = 0;
            _scale = 0.92;
            _alpha = 0;
            Push();
            try { _anim.Start(); } catch { _animating = false; _scale = 1.0; _alpha = 255; }
        }

        private void AnimTick()
        {
            _animStep++;
            double p = _animStep / (double)AnimFrames;
            if (p > 1.0) p = 1.0;
            double e = 1.0 - Math.Pow(1.0 - p, 3.0);      // ease-out，贴近 cubic-bezier(.22,1,.36,1)
            _scale = 0.92 + 0.08 * e;
            _alpha = (byte)Math.Round(255 * e);
            Push();
            if (p >= 1.0)
            {
                _animating = false;
                _scale = 1.0;
                _alpha = 255;
                _anim.Stop();
                Push();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 分层窗口由 Push() 直接合成，这里不需要绘制
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Push();
            _tipTimer.Stop();
            _tipTimer.Start();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Push();
            HideTip();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            HideTip();
            if (e.Button == MouseButtons.Left && Clicked != null) Clicked(this, EventArgs.Empty);
            else if (e.Button == MouseButtons.Right && RightClicked != null) RightClicked(this, EventArgs.Empty);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            HideTip();
            if (Visible) Push();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible)
            {
                HideTip();
                try { _anim.Stop(); } catch { }
                _animating = false;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _anim.Dispose(); } catch { }
                try { _tipTimer.Dispose(); } catch { }
                try { _tip.Dispose(); } catch { }
                DisposeFrosted();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 徽标悬停提示：7 DIP 圆角小玻璃、3 层柔和外阴影、1px 顶部高光（alpha 24）。
    /// 只有分层窗口才能让圆角与阴影边缘都是逐像素 alpha —— 普通 Form 的 Region 只能切硬边，
    /// 阴影画出来会变成窗口内部的灰边。仍然 WS_EX_TRANSPARENT：不吃鼠标、不抢焦点。
    /// </summary>
    internal sealed class IconTip : Form
    {
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const string TipText = "点击翻译";
        private readonly Font _font;
        private readonly int _margin;    // 外阴影留白（DIP→像素），窗口比气泡本体大一圈
        private readonly Size _bubble;   // 气泡本体尺寸（像素，不含阴影留白）

        public IconTip()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);

            // GDI+ 没有真正的 Medium 字重（YaHei UI 只有 Regular/Bold）：取 Bold 近似 Medium 的视觉重量
            _font = Theme.F(9f, FontStyle.Bold);
            Size ts = TextRenderer.MeasureText(TipText, _font,
                new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            _bubble = new Size(ts.Width + Dpi.S(20), Math.Max(Dpi.S(24), ts.Height + Dpi.S(10)));
            _margin = Dpi.S(6);
            ClientSize = new Size(_bubble.Width + _margin * 2, _bubble.Height + _margin * 2);
        }

        /// <summary>气泡本体高度（不含阴影留白），给宿主算“在徽标下方 8 DIP”用。</summary>
        public int BubbleHeight { get { return _bubble.Height; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                // 不吃鼠标（点击穿透），不抢焦点，不进 Alt+Tab；分层才能逐像素 alpha
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST
                              | Native.WS_EX_LAYERED | WS_EX_TRANSPARENT;
                return cp;
            }
        }

        /// <summary>anchorY 是气泡顶部（不含阴影留白），x 是期望的中心；自动夹进屏幕。</summary>
        public void ShowAt(int centerX, int topY)
        {
            Rectangle wa = Screen.FromPoint(new Point(centerX, topY)).WorkingArea;
            int x = centerX - Width / 2;
            if (x + Width > wa.Right - Dpi.S(4)) x = wa.Right - Dpi.S(4) - Width;
            if (x < wa.Left + Dpi.S(4)) x = wa.Left + Dpi.S(4);
            int y = topY - _margin;
            if (y + Height > wa.Bottom - Dpi.S(4)) y = wa.Bottom - Dpi.S(4) - Height;
            if (y < wa.Top + Dpi.S(4)) y = wa.Top + Dpi.S(4);
            SetBounds(x, y, Width, Height);
            if (!Visible) Show();
            Push();
        }

        /// <summary>把一整颗气泡（底 + 顶部高光 + 3 层外阴影）画进 32bpp 位图再逐像素贴上去。</summary>
        private void Push()
        {
            if (!IsHandleCreated) return;
            try
            {
                int w = Math.Max(8, Width);
                int h = Math.Max(8, Height);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    // Graphics.FromImage 用位图自身的 DPI：不设 144 的话 9pt 字会按 96 画，小一圈
                    bmp.SetResolution(96f * Dpi.Factor, 96f * Dpi.Factor);
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                        Rectangle r = new Rectangle(_margin, _margin, w - _margin * 2, h - _margin * 2);
                        int radius = Dpi.S(7);   // 7 DIP 圆角

                        DrawShadow(g, r, radius);

                        Color back = Theme.Dark
                            ? Color.FromArgb(240, 44, 46, 51)      // rgba(44,46,51,.94)
                            : Color.FromArgb(250, 250, 251, 253);  // rgba(250,251,253,.98)
                        using (GraphicsPath p = Theme.Round(r, radius))
                        using (SolidBrush b = new SolidBrush(back))
                            g.FillPath(b, p);

                        // 1px 顶部高光（alpha 24）：只画顶部直线段，像玻璃受光的那道边
                        int hiRadius = Math.Min(radius, Math.Max(1, r.Width / 2));
                        using (Pen pen = new Pen(Color.FromArgb(24, 255, 255, 255), 1f))
                        {
                            pen.StartCap = LineCap.Round;
                            pen.EndCap = LineCap.Round;
                            g.DrawLine(pen, r.X + hiRadius, r.Y + 0.5f, r.Right - hiRadius, r.Y + 0.5f);
                        }

                        Color ink = Theme.Dark
                            ? Color.FromArgb(236, 240, 247)
                            : Color.FromArgb(38, 44, 58);
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            using (SolidBrush tb = new SolidBrush(ink))
                                g.DrawString(TipText, _font, tb, new RectangleF(r.X, r.Y, r.Width, r.Height), sf);
                        }
                    }

                    IntPtr screenDc = Native.GetDC(IntPtr.Zero);
                    IntPtr memDc = Native.CreateCompatibleDC(screenDc);
                    IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                    IntPtr old = Native.SelectObject(memDc, hBmp);

                    Native.SIZE size = new Native.SIZE(w, h);
                    Native.POINT src = new Native.POINT(0, 0);
                    Native.POINT dst = new Native.POINT(Left, Top);
                    Native.BLENDFUNCTION blend = new Native.BLENDFUNCTION();
                    blend.BlendOp = 0;
                    blend.BlendFlags = 0;
                    blend.SourceConstantAlpha = 255;
                    blend.AlphaFormat = Native.AC_SRC_ALPHA;

                    Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);

                    Native.SelectObject(memDc, old);
                    Native.DeleteObject(hBmp);
                    Native.DeleteDC(memDc);
                    Native.ReleaseDC(IntPtr.Zero, screenDc);
                }
            }
            catch { }
        }

        /// <summary>3 层柔和外阴影：由大到小叠圆角矩形，靠近贴边的 alpha 高、越远越淡。</summary>
        private void DrawShadow(Graphics g, Rectangle r, int radius)
        {
            int[] dy = new int[] { Dpi.S(1), Dpi.S(2), Dpi.S(4) };
            int[] grow = new int[] { Dpi.S(1), Dpi.S(3), Dpi.S(6) };
            int[] alpha = Theme.Dark ? new int[] { 26, 16, 8 } : new int[] { 16, 10, 6 };
            for (int i = 2; i >= 0; i--)
            {
                Rectangle sr = new Rectangle(
                    r.X - grow[i], r.Y - grow[i] + dy[i],
                    r.Width + grow[i] * 2, r.Height + grow[i] * 2);
                using (GraphicsPath p = Theme.Round(sr, radius + grow[i]))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha[i], 0, 0, 0)))
                    g.FillPath(b, p);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // 分层窗口由 Push() 直接合成，这里不需要绘制
        }
    }
}
