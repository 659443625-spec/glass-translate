using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>控制面板：macOS 风格 —— 红黄绿窗口键、居中标题、真正透光的毛玻璃。</summary>
    internal sealed class MainForm : Form
    {
        private const int W = 480;

        private readonly App _app;

        private readonly PillButton _btnToggle;
        private readonly PillButton _btnQuit;

        private readonly ToggleSwitch _tgSelect;
        private readonly ToggleSwitch _tgHover;
        private readonly ToggleSwitch _tgClip;
        private readonly ToggleSwitch _tgAlt;

        private bool _running;
        private bool _loading;
        private int _hintY;
        private int _dividerY;
        private Bitmap _frosted;
        private Bitmap _noise;

        private Rectangle _rcClose, _rcMin, _rcZoom;
        private int _hotDot = -1;

        public MainForm(App app)
        {
            _app = app;
            AutoScaleMode = AutoScaleMode.None;

            Text = "鼠标翻译";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Dpi.S(W), Dpi.S(400));
            BackColor = Theme.Canvas;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            int pad = Dpi.S(24);
            int innerW = Dpi.S(W) - pad * 2;

            _btnToggle = new PillButton();
            _btnToggle.Kind = PillKind.Primary;
            _btnToggle.Text = "开始翻译";
            _btnToggle.SetBounds(pad, Dpi.S(92), innerW, Dpi.S(46));
            _btnToggle.Click += delegate { _app.Toggle(); };

            _tgSelect = NewRow(pad, 162);
            _tgHover = NewRow(pad, 200);
            _tgClip = NewRow(pad, 238);
            _tgAlt = NewRow(pad, 276);

            _tgSelect.CheckedChanged += delegate { ApplySettings(); };
            _tgHover.CheckedChanged += delegate { ApplySettings(); };
            _tgClip.CheckedChanged += delegate { ApplySettings(); };
            _tgAlt.CheckedChanged += delegate { ApplySettings(); };

            _btnQuit = new PillButton();
            _btnQuit.Kind = PillKind.Ghost;
            _btnQuit.Text = "退出";
            _btnQuit.Click += delegate { _app.Exit(); };

            Controls.AddRange(new Control[]
            {
                _btnToggle, _tgSelect, _tgHover, _tgClip, _tgAlt, _btnQuit
            });

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_tgClip, "网页读不到选中文字时，自动复制一次再还原");
            tip.SetToolTip(_tgAlt, "只有按住 Alt 才响应悬停");
            tip.SetToolTip(_tgHover, "鼠标停在单词上就查，默认关");

            _noise = MakeNoise();
            LoadSettingsIntoUi();
            Relayout();
        }

        private ToggleSwitch NewRow(int pad, int y)
        {
            ToggleSwitch t = new ToggleSwitch();
            t.SetBounds(Dpi.S(W) - pad - Dpi.S(46), Dpi.S(y), Dpi.S(46), Dpi.S(26));
            return t;
        }

        /// <summary>64×64 的细颗粒噪点，叠在玻璃上消除色带、增加"材质感"。</summary>
        private static Bitmap MakeNoise()
        {
            try
            {
                Random rnd = new Random(7);
                Bitmap bmp = new Bitmap(64, 64);
                for (int y = 0; y < 64; y++)
                for (int x = 0; x < 64; x++)
                {
                    int v = rnd.Next(0, 255);
                    bmp.SetPixel(x, y, Color.FromArgb(v < 128 ? 8 : 4, v, v, v));
                }
                return bmp;
            }
            catch { return null; }
        }

        // ---------------- 状态同步 ----------------

        private void LoadSettingsIntoUi()
        {
            _loading = true;
            Settings s = _app.Settings;
            _tgSelect.Checked = s.SelectionMode;
            _tgHover.Checked = s.HoverMode;
            _tgClip.Checked = s.ClipboardFallback;
            _tgAlt.Checked = s.HoverNeedAlt;
            _tgAlt.Visible = _tgHover.Checked;
            _loading = false;
        }

        private void ApplySettings()
        {
            if (_loading) return;
            Settings s = _app.Settings;
            s.SelectionMode = _tgSelect.Checked;
            s.HoverMode = _tgHover.Checked;
            s.ClipboardFallback = _tgClip.Checked;
            s.HoverNeedAlt = _tgAlt.Checked;
            s.Save();
            _tgAlt.Visible = _tgHover.Checked;
            Relayout();
            _app.OnSettingsChanged();
        }

        public void SetRunning(bool running)
        {
            _running = running;
            _btnToggle.Text = running ? "停止翻译" : "开始翻译";
            Invalidate();
        }

        public void SetCapabilities(string text) { }
        public void SetChannelStatus(string text) { }

        public void HideToTray()
        {
            Hide();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
                return cp;
            }
        }

        // ---------------- 玻璃底 ----------------

        private void CaptureGlass()
        {
            try { if (_frosted != null) { _frosted.Dispose(); _frosted = null; } } catch { }
            _frosted = Glass.Frosted(new Rectangle(Left, Top, Width, Height), Theme.Dark,
                Theme.Dark ? 0.64 : 0.66, 26);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (value)
            {
                CaptureGlass();
                base.SetVisibleCore(value);
                UiAnim.SlideIn(this, 170, 10);  // 只位移，保证子控件正常渲染
                return;
            }
            base.SetVisibleCore(value);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_rcClose.Contains(e.Location)) { Hide(); return; }
                if (_rcMin.Contains(e.Location)) { Hide(); return; }
                if (e.Y < Dpi.S(46)) { Native.DragWindow(Handle); return; }
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hot = -1;
            if (_rcClose.Contains(e.Location)) hot = 0;
            else if (_rcMin.Contains(e.Location)) hot = 1;

            if (hot != _hotDot)
            {
                _hotDot = hot;
                Cursor = hot >= 0 ? Cursors.Hand : Cursors.Default;
                Invalidate(new Rectangle(0, 0, Dpi.S(180), Dpi.S(44)));
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hotDot != -1) { _hotDot = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Visible) { CaptureGlass(); Invalidate(); }
        }

        // ---------------- 布局 ----------------

        private void Relayout()
        {
            int pad = Dpi.S(24);
            int rows = _tgHover.Checked ? 4 : 3;

            _hintY = Dpi.S(162 + rows * 38 + 16);
            _dividerY = _hintY + Dpi.S(28);
            int quitY = _dividerY + Dpi.S(12);

            _btnQuit.SetBounds(Dpi.S(W) - pad - Dpi.S(64), quitY, Dpi.S(64), Dpi.S(30));

            int h = quitY + Dpi.S(30) + Dpi.S(16);
            if (ClientSize.Height != h) ClientSize = new Size(Dpi.S(W), h);

            // 红黄绿三个窗口键
            int d = Dpi.S(12);
            int y = Dpi.S(17);
            _rcClose = new Rectangle(pad, y, d, d);
            _rcMin = new Rectangle(pad + Dpi.S(20), y, d, d);
            _rcZoom = new Rectangle(pad + Dpi.S(40), y, d, d);

            Theme.ApplyRoundRegion(this, Dpi.S(18));
            Invalidate();
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int pad = Dpi.S(24);
            int innerW = Width - pad * 2;

            Glass.Paint(g, new Rectangle(0, 0, Width, Height), _frosted, Theme.Dark, Dpi.S(18));

            // 颗粒噪点：消除色带，增加材质
            if (_noise != null)
            {
                using (TextureBrush tb = new TextureBrush(_noise, WrapMode.Tile))
                using (SolidBrush o = new SolidBrush(Color.FromArgb(Theme.Dark ? 10 : 6, 0, 0, 0)))
                {
                    g.FillRectangle(tb, ClientRectangle);
                    g.FillRectangle(o, ClientRectangle);
                }
            }

            DrawTrafficLights(g);

            // 居中标题
            using (StringFormat sf = Theme.Center())
                Theme.DrawText(g, "鼠标翻译", Theme.F(9.5f, FontStyle.Bold), Theme.Text2,
                    new Rectangle(0, Dpi.S(12), Width, Dpi.S(22)), sf);

            // 状态
            Color dot = _running ? Theme.Success : Theme.Faint;
            using (SolidBrush db = new SolidBrush(Color.FromArgb(58, dot.R, dot.G, dot.B)))
                g.FillEllipse(db, pad - Dpi.S(2), Dpi.S(54), Dpi.S(12), Dpi.S(12));
            using (SolidBrush db = new SolidBrush(dot))
                g.FillEllipse(db, pad + Dpi.S(1), Dpi.S(57), Dpi.S(6), Dpi.S(6));

            using (StringFormat sf = Theme.Near())
                Theme.DrawText(g, _running ? "运行中" : "未启动", Theme.F(9f, FontStyle.Bold),
                    _running ? Theme.Text : Theme.Muted,
                    new Rectangle(pad + Dpi.S(16), Dpi.S(50), innerW, Dpi.S(20)), sf);

            // 开关行
            string[] labels = new string[] { "划词翻译", "悬停取词", "剪贴板兜底", "悬停需按住 Alt" };
            int visibleRows = _tgHover.Checked ? 4 : 3;
            for (int i = 0; i < visibleRows; i++)
            {
                int y = Dpi.S(162 + i * 38);
                using (StringFormat sf = Theme.Near())
                    Theme.DrawText(g, labels[i], Theme.F(9.5f, FontStyle.Regular), Theme.Text2,
                        new Rectangle(pad, y, innerW - Dpi.S(60), Dpi.S(26)), sf);
            }

            // 方向说明
            using (StringFormat sf = Theme.Near())
                Theme.DrawText(g, "自动判断方向：英文 → 中文，中文 → 英文", Theme.F(8.2f, FontStyle.Regular),
                    Theme.Muted, new Rectangle(pad, _hintY, innerW, Dpi.S(20)), sf);

            using (Pen p = new Pen(Theme.Dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(40, 15, 23, 42), 1f))
                g.DrawLine(p, pad, _dividerY, Width - pad, _dividerY);
        }

        private void DrawTrafficLights(Graphics g)
        {
            Color close = Color.FromArgb(255, 95, 87);
            Color min = Color.FromArgb(254, 188, 46);
            Color zoom = Color.FromArgb(40, 200, 64);

            DrawDot(g, _rcClose, _hotDot == 0 ? Theme.Blend(close, Color.White, .25f) : close, "✕");
            DrawDot(g, _rcMin, _hotDot == 1 ? Theme.Blend(min, Color.White, .25f) : min, "–");
            DrawDot(g, _rcZoom, Color.FromArgb(70, zoom), null);   // 第三颗固定灰掉（本窗口不可缩放）
        }

        private static void DrawDot(Graphics g, Rectangle r, Color c, string glyph)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.FillEllipse(b, r);
            using (Pen pen = new Pen(Color.FromArgb(28, 0, 0, 0), 1f))
                g.DrawEllipse(pen, r);

            if (string.IsNullOrEmpty(glyph)) return;
            Font f = Theme.F(5.6f, FontStyle.Bold);
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(120, 0, 0, 0)))
            using (StringFormat sf = Theme.Center())
                g.DrawString(glyph, f, tb, new RectangleF(r.X, r.Y - 0.5f, r.Width, r.Height), sf);
        }
    }
}
