using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace MouseTranslate
{
    internal enum IconStyle
    {
        Line,    // V1 线稿：白底圆角方块 + 「文A」
        Badge,   // V2 品牌章：渐变圆形 + 白色「译」
        Chip     // V3 胶囊：浅色药丸 + 蓝色「译」
    }

    /// <summary>光标旁小徽标的三种视觉语言（按排版精度绘制，任意尺寸都清晰）。</summary>
    internal static class IconArt
    {
        public const int LogicalSize = 24;

        // 品牌章：视觉圆 30 DIP（真玻璃：抓帧→模糊→提饱和→叠色→描边→投影），
        // 分层窗口 40 DIP（给柔光投影留空间，也是抓帧区域），热区圆 29 DIP（不小于改造前的 29）。
        public const int BadgeVisual = 30;
        public const int BadgeWindow = 40;
        public const int BadgeHit = 29;

        private static readonly StringFormat Typo = BuildTypographic();

        private static StringFormat BuildTypographic()
        {
            StringFormat sf = StringFormat.GenericTypographic;
            sf.Alignment = StringAlignment.Near;
            sf.LineAlignment = StringAlignment.Near;
            sf.FormatFlags |= StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
            return sf;
        }

        public static void Draw(Graphics g, IconStyle style, Rectangle box, bool hover, bool dark)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            switch (style)
            {
                case IconStyle.Line: DrawLine(g, box, hover, dark); break;
                case IconStyle.Badge: DrawBadge(g, box, hover, dark); break;
                default: DrawChip(g, box, hover, dark); break;
            }
        }

        /// <summary>按字形实际包围盒做光学居中，比 RectangleF+StringFormat 更准。</summary>
        private static void DrawCentered(Graphics g, string text, Font f, Brush brush, RectangleF area, float dyRatio)
        {
            SizeF ts = g.MeasureString(text, f, new PointF(0, 0), Typo);
            float x = area.X + (area.Width - ts.Width) / 2f;
            float y = area.Y + (area.Height - ts.Height) / 2f + area.Height * dyRatio;
            g.DrawString(text, f, brush, x, y, Typo);
        }

        // ---------------- V1 线稿 ----------------

        private static void DrawLine(Graphics g, Rectangle box, bool hover, bool dark)
        {
            int side = Math.Min(box.Width, box.Height);
            int radius = Math.Max(3, side / 4);
            Rectangle body = new Rectangle(box.X, box.Y, side - 1, side - 1);

            Color bg = hover
                ? (dark ? Color.FromArgb(36, 41, 51) : Color.FromArgb(242, 246, 255))
                : (dark ? Color.FromArgb(27, 31, 38) : Color.White);
            Color border = hover ? Theme.Accent : (dark ? Color.FromArgb(56, 63, 76) : Color.FromArgb(216, 222, 232));
            Color ink = hover ? (dark ? Color.White : Theme.Accent)
                              : (dark ? Color.FromArgb(208, 218, 232) : Theme.Accent);

            using (GraphicsPath p = Theme.Round(body, radius))
            using (SolidBrush b = new SolidBrush(bg))
                g.FillPath(b, p);
            using (GraphicsPath p = Theme.Round(body, radius))
            using (Pen pen = new Pen(border, 1f))
                g.DrawPath(pen, p);

            // 「文A」作为一个整体居中：先量总宽，再逐字绘制
            Font f = Theme.F(side * 0.33f * 72f / (96f * Dpi.Factor), FontStyle.Bold);
            string g1 = "文", g2 = "A";
            SizeF sa = g.MeasureString(g1, f, new PointF(0, 0), Typo);
            SizeF sb = g.MeasureString(g2, f, new PointF(0, 0), Typo);
            float gap = side * 0.035f;
            float total = sa.Width + gap + sb.Width;
            float x = body.X + (body.Width - total) / 2f;
            float yBase = body.Y + (body.Height - Math.Max(sa.Height, sb.Height)) / 2f + side * 0.015f;

            using (SolidBrush tb = new SolidBrush(ink))
            {
                g.DrawString(g1, f, tb, x, yBase, Typo);
                g.DrawString(g2, f, tb, x + sa.Width + gap, yBase, Typo);
            }
        }

        // ---------------- V2 品牌章（Apple Liquid Glass） ----------------

        /// <summary>
        /// 中性黑外投影：6 层极淡黑椭圆（累计不透明度 ≤0.18），扩散 ≤6 DIP。
        /// 不用彩色/白色投影，避免亮背景下出现“白光晕”。
        /// </summary>
        private static void DrawBadgeShadow(Graphics g, Rectangle visual, bool dark)
        {
            int spread = Math.Max(2, Dpi.S(5));            // 5 DIP ≤ 6 DIP
            int dy = Math.Max(1, Dpi.S(1));
            int layers = 6;
            // 每层 alpha：浅色 8/255（累计 1-(1-8/255)^6 ≈ 17.4%），深色 6/255（≈13.3%）
            int alpha = dark ? 6 : 8;
            for (int i = layers; i >= 1; i--)
            {
                int inflate = spread * i / layers;
                Rectangle r = new Rectangle(
                    visual.X - inflate,
                    visual.Y - inflate + dy,
                    visual.Width + inflate * 2,
                    visual.Height + inflate * 2);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(alpha, 0, 0, 0)))
                    g.FillEllipse(b, r);
            }
        }

        /// <summary>顶部一道极淡的受光（模拟玻璃厚度），必须已经裁剪在圆内调用。</summary>
        private static void DrawBadgeSpecular(Graphics g, Rectangle visual, bool hover, bool dark)
        {
            Rectangle top = new Rectangle(visual.X, visual.Y, visual.Width, Math.Max(6, (int)(visual.Height * 0.62f)));
            int a = dark ? 18 : 70;
            if (hover) a += dark ? 8 : 18;
            if (a > 255) a = 255;
            using (LinearGradientBrush lg = new LinearGradientBrush(
                top, Color.FromArgb(a, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                g.FillRectangle(lg, top);

            // 左上角一小块柔光高光：液体玻璃的“流动感”（保持克制，避免糊掉字形）
            Rectangle glow = new Rectangle(
                visual.X + (int)(visual.Width * 0.10f),
                visual.Y + (int)(visual.Height * 0.08f),
                Math.Max(8, (int)(visual.Width * 0.52f)),
                Math.Max(6, (int)(visual.Height * 0.36f)));
            int ga = dark ? (hover ? 40 : 28) : (hover ? 66 : 46);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(glow);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(ga, 255, 255, 255);
                    pb.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillPath(pb, gp);
                }
            }
        }

        /// <summary>1px 玻璃描边 + 顶部内高光（苹果玻璃的“边”）。</summary>
        private static void DrawBadgeEdge(Graphics g, Rectangle visual, bool hover, bool dark)
        {
            float w = Math.Max(1f, Dpi.Sf(1f));
            Color outer = hover
                ? (dark ? Color.FromArgb(200, 10, 132, 255) : Color.FromArgb(210, 0, 122, 255))
                : (dark ? Color.FromArgb(58, 255, 255, 255) : Color.FromArgb(225, 255, 255, 255));
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(new RectangleF(visual.X + w / 2f, visual.Y + w / 2f,
                    visual.Width - w, visual.Height - w));
                using (Pen pen = new Pen(outer, w)) g.DrawPath(pen, p);
            }

            float hw = Math.Max(1f, Dpi.Sf(1.1f));
            using (GraphicsPath hp = new GraphicsPath())
            {
                RectangleF hr = new RectangleF(visual.X + hw, visual.Y + hw,
                    visual.Width - hw * 2, visual.Height - hw * 2);
                hp.AddArc(hr, 198f, 144f);
                using (Pen hi = new Pen(Color.FromArgb(dark ? (hover ? 120 : 92) : (hover ? 190 : 150), 255, 255, 255), hw))
                    g.DrawPath(hi, hp);
            }
        }

        /// <summary>
        /// 细线条「文A」图标：矢量描边（≥1.2 DIP），两个字之间留足间距，任意 DPI 都锐利。
        /// inkDark：亮内衬上画深字(#1d1d1f)，暗内衬上画白字 —— 由宿主按合成亮度自动选。
        /// </summary>
        private static void DrawBadgeGlyph(Graphics g, Rectangle visual, bool hover, bool dark, bool inkDark)
        {
            int side = Math.Min(visual.Width, visual.Height);
            float inset = side * 0.17f;
            RectangleF area = new RectangleF(visual.X + inset, visual.Y + inset,
                side - inset * 2, side - inset * 2);
            Color ink;
            if (hover)
                ink = dark ? Color.FromArgb(255, 110, 190, 255) : Color.FromArgb(255, 0, 96, 214);
            else
                ink = inkDark ? Color.FromArgb(246, 24, 24, 27) : Color.FromArgb(246, 248, 248, 250);
            float stroke = Math.Max(1.2f, Dpi.Sf(1.2f));

            float s = Math.Min(area.Width, area.Height);
            float x = area.X + s * 0.005f;
            float y = area.Y + s * 0.075f;      // 光学居中：笔画重心偏下，稍微下移才看着在中间

            using (Pen pen = new Pen(ink, stroke))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                // 文：点 / 横 / 撇捺（左半区 0.02~0.415，横与撇捺拉开，避免看成除号）
                g.DrawLine(pen, x + s * 0.200f, y + s * 0.055f, x + s * 0.228f, y + s * 0.155f);
                g.DrawLine(pen, x + s * 0.020f, y + s * 0.275f, x + s * 0.415f, y + s * 0.275f);
                g.DrawLine(pen, x + s * 0.075f, y + s * 0.415f, x + s * 0.355f, y + s * 0.775f);
                g.DrawLine(pen, x + s * 0.355f, y + s * 0.415f, x + s * 0.075f, y + s * 0.775f);

                // A：两撇 + 横（右半区 0.60~0.99，和「文」留出干净的空隙）
                g.DrawLine(pen, x + s * 0.600f, y + s * 0.775f, x + s * 0.795f, y + s * 0.170f);
                g.DrawLine(pen, x + s * 0.795f, y + s * 0.170f, x + s * 0.990f, y + s * 0.775f);
                g.DrawLine(pen, x + s * 0.688f, y + s * 0.550f, x + s * 0.902f, y + s * 0.550f);
            }
        }

        /// <summary>
        /// 画真玻璃徽标：frosted = 背后 40x40 抓帧经模糊/饱和后的图（null 时回退纯色玻璃）。
        /// windowBox 是 40 DIP 分层窗口，visualBox 是居中的 30 DIP 圆；frosted 按 windowBox 对齐、
        /// 随 scale 围绕中心缩放（入场 0.92→1 / 悬停 1.03 时玻璃内容也跟着轻微缩）。
        /// 玻璃色与字形之间固定加一层内衬（scrim）：浅色白 0.55 / 深色黑 0.45，保证细线对比。
        /// inkDark 由 WebUi.PickIconInkDark 按「玻璃+内衬」合成亮度自动判定。
        /// </summary>
        public static void DrawBadgeGlass(Graphics g, Rectangle windowBox, Rectangle visualBox,
            bool hover, bool dark, Bitmap frosted, double scale, bool inkDark)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DrawBadgeShadow(g, visualBox, dark);

            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(visualBox);
                GraphicsState st = g.Save();
                g.SetClip(p, CombineMode.Replace);

                if (frosted != null)
                {
                    int iw = Math.Max(8, (int)Math.Round(windowBox.Width * scale));
                    int ih = Math.Max(8, (int)Math.Round(windowBox.Height * scale));
                    int ix = windowBox.X + (windowBox.Width - iw) / 2;
                    int iy = windowBox.Y + (windowBox.Height - ih) / 2;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(frosted, new Rectangle(ix, iy, iw, ih));
                }
                else
                {
                    using (SolidBrush b = new SolidBrush(dark
                        ? Color.FromArgb(250, 36, 36, 40)
                        : Color.FromArgb(250, 252, 253, 255)))
                        g.FillRectangle(b, visualBox);
                }

                // 内衬（scrim）：把彩色背景压中性，给细线一个稳定的对比底
                using (SolidBrush sb = new SolidBrush(dark
                    ? Color.FromArgb(115, 0, 0, 0)        // 黑 0.45
                    : Color.FromArgb(140, 255, 255, 255))) // 白 0.55
                    g.FillRectangle(sb, visualBox);

                if (hover)
                    using (SolidBrush hb = new SolidBrush(dark
                        ? Color.FromArgb(30, 10, 132, 255)
                        : Color.FromArgb(22, 0, 122, 255)))
                        g.FillRectangle(hb, visualBox);

                DrawBadgeSpecular(g, visualBox, hover, dark);
                g.Restore(st);
            }

            DrawBadgeEdge(g, visualBox, hover, dark);
            DrawBadgeGlyph(g, visualBox, hover, dark, inkDark);
        }

        /// <summary>--icon-sheet 评审图用：没有真实桌面的场合，用一块渐变“假壁纸”模拟玻璃。</summary>
        private static void DrawBadge(Graphics g, Rectangle box, bool hover, bool dark)
        {
            int side = Math.Min(box.Width, box.Height);
            Rectangle visual = new Rectangle(
                box.X + (box.Width - side) / 2,
                box.Y + (box.Height - side) / 2,
                side, side);

            DrawBadgeShadow(g, visual, dark);

            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(visual);
                GraphicsState st = g.Save();
                g.SetClip(p, CombineMode.Replace);
                using (LinearGradientBrush lb = new LinearGradientBrush(
                    visual,
                    Color.FromArgb(255, 86, 172, 255),
                    Color.FromArgb(255, 172, 118, 255),
                    35f))
                    g.FillRectangle(lb, visual);
                using (SolidBrush tint = new SolidBrush(dark
                    ? Color.FromArgb(150, 22, 22, 25)
                    : Color.FromArgb(120, 255, 255, 255)))
                    g.FillRectangle(tint, visual);
                // 同样加内衬，评审图里的字形对比和真实徽标一致
                using (SolidBrush sb = new SolidBrush(dark
                    ? Color.FromArgb(115, 0, 0, 0)
                    : Color.FromArgb(140, 255, 255, 255)))
                    g.FillRectangle(sb, visual);
                DrawBadgeSpecular(g, visual, hover, dark);
                g.Restore(st);
            }

            DrawBadgeEdge(g, visual, hover, dark);
            DrawBadgeGlyph(g, visual, hover, dark, !dark);
        }
        // ---------------- V3 胶囊 ----------------

        private static void DrawChip(Graphics g, Rectangle box, bool hover, bool dark)
        {
            Rectangle body = new Rectangle(box.X, box.Y, box.Width - 1, box.Height - 1);
            int radius = body.Height / 2;

            Color bg = hover ? Theme.Accent : (dark ? Color.FromArgb(30, 35, 43) : Color.White);
            Color border = hover ? Theme.Accent : (dark ? Color.FromArgb(56, 63, 76) : Color.FromArgb(218, 224, 234));

            using (GraphicsPath p = Theme.Round(body, radius))
            using (SolidBrush b = new SolidBrush(bg))
                g.FillPath(b, p);
            using (GraphicsPath p = Theme.Round(body, radius))
            using (Pen pen = new Pen(border, 1f))
                g.DrawPath(pen, p);

            if (!hover)
            {
                using (Pen hi = new Pen(Color.FromArgb(dark ? 16 : 130, 255, 255, 255), 1f))
                    g.DrawLine(hi, body.X + radius, body.Y + 1, body.Right - radius, body.Y + 1);
            }

            Color ink = hover ? Color.White : (dark ? Color.FromArgb(228, 234, 244) : Theme.Accent);
            Font f = Theme.F(body.Height * 0.47f * 72f / (96f * Dpi.Factor), FontStyle.Bold);
            using (SolidBrush tb = new SolidBrush(ink))
                DrawCentered(g, "译", f, tb, new RectangleF(body.X, body.Y, body.Width, body.Height), 0.005f);
        }

        /// <summary>视觉尺寸（Badge = 30 DIP 圆）。</summary>
        public static Size Measure(IconStyle style)
        {
            if (style == IconStyle.Chip) return new Size(Dpi.S(34), Dpi.S(21));
            if (style == IconStyle.Badge) return new Size(Dpi.S(BadgeVisual), Dpi.S(BadgeVisual));
            return new Size(Dpi.S(LogicalSize), Dpi.S(LogicalSize));
        }

        /// <summary>分层窗口尺寸：Badge 用 40 DIP 窗口包住 30 DIP 玻璃 + 柔光投影。</summary>
        public static Size HitSize(IconStyle style)
        {
            if (style == IconStyle.Badge) return new Size(Dpi.S(BadgeWindow), Dpi.S(BadgeWindow));
            return Measure(style);
        }

        /// <summary>徽标热区圆（29 DIP，不小于改造前）。分层窗口按像素 alpha 命中，这里用 alpha≈3 撑出可点范围。</summary>
        public static Size HitCircle(IconStyle style)
        {
            if (style == IconStyle.Badge) return new Size(Dpi.S(BadgeHit), Dpi.S(BadgeHit));
            return Measure(style);
        }

        /// <summary>
        /// 画命中热区：分层窗口按像素 alpha 做命中测试，alpha=0 的透明像素会让点击穿透到下面的网页。
        /// 这里用 alpha=3 的近透明圆把可点范围撑到整个 box —— 肉眼几乎不可见，鼠标能命中。
        /// </summary>
        public static void DrawHitArea(Graphics g, Rectangle box)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(3, 16, 18, 24)))
                g.FillEllipse(b, box);
        }
    }
}
