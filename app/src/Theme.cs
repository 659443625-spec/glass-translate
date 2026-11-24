using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using Microsoft.Win32;

namespace MouseTranslate
{
    /// <summary>设计系统：配色（亮/暗）、字体、圆角、绘制工具。</summary>
    internal static class Theme
    {
        public static bool Dark { get; private set; }

        // 主色（两套主题共用，保证品牌一致）
        public static readonly Color Accent = Color.FromArgb(79, 107, 255);
        public static readonly Color Accent2 = Color.FromArgb(124, 92, 255);

        // 语义色
        public static readonly Color Success = Color.FromArgb(16, 185, 129);
        public static readonly Color Danger = Color.FromArgb(239, 68, 68);

        // 可切换的语义槽位
        public static Color AccentSoft;
        public static Color Text;
        public static Color Text2;
        public static Color Muted;
        public static Color Faint;
        public static Color Surface;
        public static Color Canvas;
        public static Color Subtle;
        public static Color Line;
        public static Color LineSoft;
        public static Color Track;
        public static Color ShadowEdge;

        static Theme()
        {
            Init(false);
        }

        public static void Init(bool dark)
        {
            Dark = dark;
            if (dark)
            {
                Canvas = Color.FromArgb(18, 20, 24);
                Surface = Color.FromArgb(26, 29, 35);
                Subtle = Color.FromArgb(34, 38, 46);
                Line = Color.FromArgb(46, 51, 61);
                LineSoft = Color.FromArgb(38, 42, 50);
                Track = Color.FromArgb(58, 65, 80);
                Text = Color.FromArgb(237, 241, 247);
                Text2 = Color.FromArgb(182, 192, 206);
                Muted = Color.FromArgb(138, 148, 166);
                Faint = Color.FromArgb(107, 116, 132);
                AccentSoft = Color.FromArgb(28, 37, 64);
                ShadowEdge = Color.FromArgb(8, 9, 11);
            }
            else
            {
                Canvas = Color.FromArgb(247, 248, 252);
                Surface = Color.White;
                Subtle = Color.FromArgb(241, 245, 249);
                Line = Color.FromArgb(226, 232, 240);
                LineSoft = Color.FromArgb(238, 242, 247);
                Track = Color.FromArgb(203, 213, 225);
                Text = Color.FromArgb(15, 23, 42);
                Text2 = Color.FromArgb(71, 85, 105);
                Muted = Color.FromArgb(100, 116, 139);
                Faint = Color.FromArgb(148, 163, 184);
                AccentSoft = Color.FromArgb(238, 242, 255);
                ShadowEdge = Color.FromArgb(190, 197, 210);
            }
        }

        /// <summary>读系统「应用颜色」设置；读不到就当亮色。</summary>
        public static bool DetectDark()
        {
            try
            {
                object v = Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (v is int) return (int)v == 0;
            }
            catch { }
            return false;
        }

        // ---------------- 字体 ----------------

        private static readonly Dictionary<string, Font> _fonts = new Dictionary<string, Font>();
        private static readonly object _lock = new object();
        public const string FontFamily = "Microsoft YaHei UI";

        public static Font F(float pt, FontStyle style)
        {
            string key = pt.ToString("0.##") + "/" + (int)style;
            lock (_lock)
            {
                Font f;
                if (_fonts.TryGetValue(key, out f)) return f;
                f = new Font(FontFamily, pt, style);
                _fonts[key] = f;
                return f;
            }
        }

        public static Font Bold(float pt) { return F(pt, FontStyle.Bold); }
        public static Font Regular(float pt) { return F(pt, FontStyle.Regular); }

        // ---------------- 形状 ----------------

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color color)
        {
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(color))
                g.FillPath(b, p);
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c1, Color c2, float angle)
        {
            using (GraphicsPath p = Round(r, radius))
            using (LinearGradientBrush b = new LinearGradientBrush(
                new Rectangle(r.X, r.Y, Math.Max(2, r.Width), Math.Max(2, r.Height)), c1, c2, angle))
                g.FillPath(b, p);
        }

        public static void DrawRound(Graphics g, Rectangle r, int radius, Color color, float width)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(color, width))
                g.DrawPath(pen, p);
        }

        /// <summary>用几层递减透明度的圆角描边模拟柔和投影（无分层窗口时最接近真实阴影的做法）。</summary>
        public static void DrawSoftShadow(Graphics g, Rectangle r, int radius, int layers)
        {
            for (int i = layers; i >= 1; i--)
            {
                int alpha = Dark ? 40 - i * 8 : 16 - i * 4;
                if (alpha <= 0) continue;
                Rectangle rr = new Rectangle(r.X - i, r.Y - i + 1, r.Width + i * 2, r.Height + i * 2);
                using (GraphicsPath p = Round(rr, radius + i))
                using (Pen pen = new Pen(Color.FromArgb(alpha, ShadowEdge), 1.2f))
                    g.DrawPath(pen, p);
            }
        }

        public static Color Blend(Color a, Color b, float t)
        {
            if (t < 0) t = 0;
            if (t > 1) t = 1;
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int alpha)
        {
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        public static void DrawText(Graphics g, string s, Font f, Color c, Rectangle r, StringFormat sf)
        {
            using (SolidBrush b = new SolidBrush(c))
                g.DrawString(s, f, b, r, sf);
        }

        public static StringFormat Near()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Center;
            sf.Trimming = StringTrimming.EllipsisCharacter;
            return sf;
        }

        public static StringFormat Center()
        {
            StringFormat sf = new StringFormat();
            sf.Alignment = StringAlignment.Center;
            sf.LineAlignment = StringAlignment.Center;
            return sf;
        }

        public static void ApplyRoundRegion(System.Windows.Forms.Form f, int radius)
        {
            using (GraphicsPath p = Round(new Rectangle(0, 0, f.Width - 1, f.Height - 1), radius))
                f.Region = new Region(p);
        }
    }
}
