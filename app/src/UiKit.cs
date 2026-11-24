using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>iOS 风格开关，替代老式 CheckBox。</summary>
    internal sealed class ToggleSwitch : Control
    {
        private bool _checked;

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(Dpi.S(46), Dpi.S(26));
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                Invalidate();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            _checked = !_checked;
            Invalidate();
            if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int h = Height;
            int w = Width;
            int pad = Dpi.S(2);
            Rectangle track = new Rectangle(0, (h - Dpi.S(22)) / 2, w, Dpi.S(22));
            Color c = _checked ? Theme.Accent : Theme.Track;
            Theme.FillRound(g, track, track.Height / 2, c);

            int kd = Dpi.S(18);
            int kx = _checked ? track.Right - kd - pad : track.Left + pad;
            int ky = track.Top + (track.Height - kd) / 2;
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(38, 0, 0, 0)))
                g.FillEllipse(sh, kx, ky + Dpi.S(1), kd, kd);
            using (SolidBrush b = new SolidBrush(Color.White))
                g.FillEllipse(b, kx, ky, kd, kd);
        }
    }

    /// <summary>分段选择器，替代下拉框。</summary>
    internal sealed class Segmented : Control
    {
        private string[] _items = new string[0];
        private int _index;

        public event EventHandler SelectionChanged;

        public Segmented()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Height = Dpi.S(34);
        }

        public string[] Items
        {
            get { return _items; }
            set { _items = value ?? new string[0]; Invalidate(); }
        }

        public int SelectedIndex
        {
            get { return _index; }
            set
            {
                if (value < 0) value = 0;
                if (value >= _items.Length) value = Math.Max(0, _items.Length - 1);
                if (_index == value) return;
                _index = value;
                Invalidate();
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            }
        }

        protected override void OnClick(EventArgs e)
        {
            if (_items.Length == 0) return;
            int seg = Width / _items.Length;
            int idx = seg <= 0 ? 0 : Math.Min(_items.Length - 1, (PointToClient(Cursor.Position).X) / seg);
            SelectedIndex = idx;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(0, 0, Width, Height);
            Theme.FillRound(g, r, Dpi.S(9), Theme.Subtle);

            if (_items.Length == 0) return;
            int seg = Width / _items.Length;
            Rectangle pill = new Rectangle(_index * seg + Dpi.S(2), Dpi.S(2), seg - Dpi.S(4), Height - Dpi.S(4));
            Theme.FillRound(g, pill, Dpi.S(7), Theme.Surface);
            Theme.DrawRound(g, pill, Dpi.S(7), Theme.Line, 1f);

            using (StringFormat sf = Theme.Center())
            {
                for (int i = 0; i < _items.Length; i++)
                {
                    Rectangle cell = new Rectangle(i * seg, 0, seg, Height);
                    bool on = i == _index;
                    Theme.DrawText(g, _items[i], Theme.F(i == _index ? 9f : 9f, on ? FontStyle.Bold : FontStyle.Regular),
                        on ? Theme.Text : Theme.Muted, cell, sf);
                }
            }
        }
    }

    internal enum PillKind { Primary, Ghost, Danger }

    /// <summary>圆角按钮。</summary>
    internal sealed class PillButton : Control
    {
        private bool _hover;
        private bool _press;
        private PillKind _kind = PillKind.Ghost;

        public PillKind Kind
        {
            get { return _kind; }
            set { _kind = value; Invalidate(); }
        }

        public PillButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _press = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _press = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _press = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Rectangle r = new Rectangle(0, 0, Width, Height);
            int radius = Dpi.S(10);
            Color fore;

            if (_kind == PillKind.Primary)
            {
                Color a = Theme.Accent;
                Color b = Theme.Accent2;
                if (_hover) { a = Theme.Blend(a, Color.White, .10f); b = Theme.Blend(b, Color.White, .10f); }
                if (_press) { a = Theme.Blend(a, Color.Black, .08f); b = Theme.Blend(b, Color.Black, .08f); }
                Theme.FillRound(g, r, radius, a, b, 0f);
                fore = Color.White;
            }
            else if (_kind == PillKind.Danger)
            {
                Color c = _hover ? Color.FromArgb(248, 113, 113) : Theme.Danger;
                Theme.FillRound(g, r, radius, c);
                fore = Color.White;
            }
            else
            {
                Color bg = _hover
                    ? (Theme.Dark ? Color.FromArgb(64, 255, 255, 255) : Color.FromArgb(210, 255, 255, 255))
                    : (Theme.Dark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(120, 255, 255, 255));
                Theme.FillRound(g, r, radius, bg);
                Theme.DrawRound(g, r, radius, _hover
                    ? (Theme.Dark ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255))
                    : (Theme.Dark ? Color.FromArgb(44, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)), 1f);
                fore = Theme.Text2;
            }

            using (StringFormat sf = Theme.Center())
            {
                Font f = Theme.F(9.5f, _kind == PillKind.Primary ? FontStyle.Bold : FontStyle.Regular);
                Theme.DrawText(g, Text, f, fore, r, sf);
            }
        }
    }

    internal enum Glyph { Close, Minimize, Pin, Copy, Speaker, Check }

    /// <summary>只画图标的小按钮（矢量绘制，不用 emoji）。</summary>
    internal sealed class IconButton : Control
    {
        private bool _hover;
        private bool _active;

        public Glyph Glyph { get; set; }
        public bool Active { get { return _active; } set { _active = value; Invalidate(); } }

        public IconButton()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(Dpi.S(28), Dpi.S(28));
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);

            if (_hover || _active)
                Theme.FillRound(g, r, Dpi.S(7), _active
                    ? Theme.AccentSoft
                    : (Theme.Dark ? Color.FromArgb(44, 255, 255, 255) : Color.FromArgb(150, 255, 255, 255)));

            Color c = _active ? Theme.Accent : (_hover ? Theme.Text : Theme.Muted);
            using (Pen pen = new Pen(c, Dpi.Sf(1.6f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                float cx = Width / 2f;
                float cy = Height / 2f;
                float s = Dpi.Sf(4.2f);

                switch (Glyph)
                {
                    case Glyph.Close:
                        g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                        g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                        break;
                    case Glyph.Minimize:
                        g.DrawLine(pen, cx - s, cy, cx + s, cy);
                        break;
                    case Glyph.Pin:
                        g.DrawLine(pen, cx - s, cy - s * 0.6f, cx + s, cy - s * 0.6f);
                        g.DrawLine(pen, cx - s * 0.45f, cy - s * 0.6f, cx - s * 0.45f, cy + s * 0.25f);
                        g.DrawLine(pen, cx + s * 0.45f, cy - s * 0.6f, cx + s * 0.45f, cy + s * 0.25f);
                        g.DrawLine(pen, cx - s * 0.9f, cy + s * 0.25f, cx + s * 0.9f, cy + s * 0.25f);
                        g.DrawLine(pen, cx, cy + s * 0.25f, cx, cy + s);
                        break;
                    case Glyph.Copy:
                        g.DrawRectangle(pen, cx - s, cy - s * 0.6f, s * 1.25f, s * 1.6f);
                        g.DrawLine(pen, cx - s * 0.5f, cy + s, cx + s, cy + s);
                        g.DrawLine(pen, cx + s, cy + s, cx + s, cy - s);
                        break;
                    case Glyph.Speaker:
                        {
                            PointF[] tri = new PointF[]
                            {
                                new PointF(cx - s, cy - s * 0.45f),
                                new PointF(cx - s * 0.35f, cy - s * 0.45f),
                                new PointF(cx + s * 0.25f, cy - s),
                                new PointF(cx + s * 0.25f, cy + s),
                                new PointF(cx - s * 0.35f, cy + s * 0.45f),
                                new PointF(cx - s, cy + s * 0.45f)
                            };
                            g.DrawPolygon(pen, tri);
                            g.DrawArc(pen, cx + s * 0.15f, cy - s * 0.85f, s * 1.5f, s * 1.7f, -55, 110);
                        }
                        break;
                    case Glyph.Check:
                        g.DrawLine(pen, cx - s, cy, cx - s * 0.15f, cy + s * 0.8f);
                        g.DrawLine(pen, cx - s * 0.15f, cy + s * 0.8f, cx + s, cy - s * 0.8f);
                        break;
                }
            }
        }
    }
}
