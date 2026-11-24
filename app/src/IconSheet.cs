using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MouseTranslate
{
    /// <summary>生成三版图标的对比评审图（亮/暗各一组，含 1x 实景与 4x 放大）。</summary>
    internal static class IconSheet
    {
        private static readonly string[] Names = new string[]
        {
            "V1 线稿 Line",
            "V2 品牌章 Badge",
            "V3 胶囊 Chip"
        };

        private static readonly string[] Blurbs = new string[]
        {
            "白底圆角 + 线性翻译标 · 轻、克制",
            "渐变圆 + 白色「译」· 品牌感强",
            "实色药丸 + 光点 · 工具条语义"
        };

        public static void Render(string path)
        {
            int w = 960, h = 700;
            using (Bitmap bmp = new Bitmap(w, h))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.FromArgb(232, 235, 241));

                using (Font f = new Font("Microsoft YaHei UI", 15f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(15, 23, 42)))
                    g.DrawString("光标徽标 · 三版方案评审", f, b, 34, 22);

                using (Font f = new Font("Microsoft YaHei UI", 9f))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(100, 116, 139)))
                    g.DrawString("1x 为实际尺寸（150% DPI），4x 用于观察工艺细节", f, b, 36, 54);

                DrawSection(g, 20, true);
                DrawSection(g, 360, false);

                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        private static void DrawSection(Graphics g, int top, bool light)
        {
            bool dark = !light;
            bool savedDark = Theme.Dark;
            Theme.Init(dark);

            try
            {
                Rectangle panel = new Rectangle(20, top, 920, 320);
                Theme.FillRound(g, panel, 14, Theme.Canvas);
                Theme.DrawRound(g, panel, 14, Theme.Line, 1f);

                using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
                using (SolidBrush b = new SolidBrush(Theme.Muted))
                    g.DrawString(light ? "亮色主题" : "暗色主题", f, b, 40, top + 14);

                for (int i = 0; i < 3; i++)
                {
                    int x = 44 + i * 300;
                    DrawCell(g, x, top + 44, (IconStyle)i, dark);
                }
            }
            finally
            {
                Theme.Init(savedDark);
            }
        }

        private static void DrawCell(Graphics g, int x, int y, IconStyle style, bool dark)
        {
            int idx = (int)style;

            using (Font f = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold))
            using (SolidBrush b = new SolidBrush(Theme.Text))
                g.DrawString(Names[idx], f, b, x, y);

            using (Font f = new Font("Microsoft YaHei UI", 7.6f))
            using (SolidBrush b = new SolidBrush(Theme.Muted))
                g.DrawString(Blurbs[idx], f, b, x, y + 18);

            // 实景：模拟一段文字，徽标出现在行尾
            Rectangle stage = new Rectangle(x, y + 40, 280, 104);
            Theme.FillRound(g, stage, 10, Theme.Surface);

            using (Pen line = new Pen(dark ? Color.FromArgb(70, 78, 92) : Color.FromArgb(206, 213, 224), 2f))
            {
                line.StartCap = LineCap.Round;
                line.EndCap = LineCap.Round;
                g.DrawLine(line, stage.X + 16, stage.Y + 26, stage.X + 196, stage.Y + 26);
                g.DrawLine(line, stage.X + 16, stage.Y + 50, stage.X + 150, stage.Y + 50);
                g.DrawLine(line, stage.X + 16, stage.Y + 74, stage.X + 182, stage.Y + 74);
            }

            Size real = IconArt.Measure(style);
            Rectangle realBox = new Rectangle(stage.X + 158, stage.Y + 38 - real.Height / 2 + 4, real.Width, real.Height);
            IconArt.Draw(g, style, realBox, false, dark);

            // 4x 放大
            Rectangle zoom = new Rectangle(x, y + 156, 280, 116);
            Theme.FillRound(g, zoom, 10, Theme.Subtle);

            Size big = IconArt.Measure(style);
            big = new Size(big.Width * 4, big.Height * 4);
            Rectangle bigBox = new Rectangle(
                zoom.X + (zoom.Width - big.Width) / 2,
                zoom.Y + (zoom.Height - big.Height) / 2,
                big.Width, big.Height);
            IconArt.Draw(g, style, bigBox, false, dark);
        }
    }
}
