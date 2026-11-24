using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MouseTranslate
{
    /// <summary>
    /// 毛玻璃材质：抓取窗口背后的屏幕 → 缩小 → 模糊 → 放大 → 叠色。
    /// 比系统 Acrylic 可控：圆角、描边、高光、透明度都能自己定，且能和 WinForms 控件共存。
    /// </summary>
    internal static class Glass
    {
        private static readonly object _lock = new object();

        /// <summary>抓取屏幕上的一块区域（窗口显示前调用，避免抓到自己）。</summary>
        public static Bitmap Capture(Rectangle screenRect)
        {
            try
            {
                if (screenRect.Width < 4 || screenRect.Height < 4) return null;
                Bitmap bmp = new Bitmap(screenRect.Width, screenRect.Height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(screenRect.Left, screenRect.Top, 0, 0, screenRect.Size, CopyPixelOperation.SourceCopy);
                return bmp;
            }
            catch { return null; }
        }

        /// <summary>缩小 → 多次盒式模糊 → 放大，得到廉价但足够漂亮的模糊。</summary>
        public static Bitmap Blur(Bitmap src, int downscale, int passes)
        {
            if (src == null) return null;
            try
            {
                int w = Math.Max(8, src.Width / Math.Max(2, downscale));
                int h = Math.Max(8, src.Height / Math.Max(2, downscale));

                using (Bitmap small = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }

                    BoxBlur(small, passes);

                    Bitmap big = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.DrawImage(small, new Rectangle(0, 0, src.Width, src.Height));
                    }
                    return big;
                }
            }
            catch { return null; }
        }

        private static void BoxBlur(Bitmap bmp, int passes)
        {
            for (int p = 0; p < passes; p++)
            {
                BlurPass(bmp, true);
                BlurPass(bmp, false);
            }
        }

        private static void BlurPass(Bitmap bmp, bool horizontal)
        {
            int w = bmp.Width, h = bmp.Height;
            Bitmap clone = (Bitmap)bmp.Clone();
            BitmapData sd = clone.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            BitmapData dd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = sd.Stride;
                byte[] src = new byte[stride * h];
                byte[] dst = new byte[stride * h];
                System.Runtime.InteropServices.Marshal.Copy(sd.Scan0, src, 0, src.Length);

                int r = 2;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int rr = 0, gg = 0, bb = 0, cnt = 0;
                        for (int k = -r; k <= r; k++)
                        {
                            int xx = horizontal ? x + k : x;
                            int yy = horizontal ? y : y + k;
                            if (xx < 0 || xx >= w || yy < 0 || yy >= h) continue;
                            int o = yy * stride + xx * 4;
                            bb += src[o]; gg += src[o + 1]; rr += src[o + 2]; cnt++;
                        }
                        int o2 = y * stride + x * 4;
                        dst[o2] = (byte)(bb / cnt);
                        dst[o2 + 1] = (byte)(gg / cnt);
                        dst[o2 + 2] = (byte)(rr / cnt);
                        dst[o2 + 3] = 255;
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(dst, 0, dd.Scan0, dst.Length);
            }
            finally
            {
                clone.UnlockBits(sd);
                bmp.UnlockBits(dd);
                clone.Dispose();
            }
        }

        /// <summary>把背景处理成"毛玻璃底"：模糊 + 明暗叠色。</summary>
        /// <summary>opacity：0=全透明，1=完全不透。卡片用厚材质，面板用薄材质。</summary>
        public static Bitmap Frosted(Rectangle screenRect, bool dark, double opacity)
        {
            return Frosted(screenRect, dark, opacity, 11);
        }

        public static Bitmap Frosted(Rectangle screenRect, bool dark, double opacity, int downscale)
        {
            Bitmap raw = Capture(screenRect);
            if (raw == null) return null;
            Bitmap blurred = Blur(raw, downscale, 3);
            raw.Dispose();
            if (blurred == null) return null;

            try
            {
                using (Graphics g = Graphics.FromImage(blurred))
                {
                    int a = (int)Math.Max(0, Math.Min(255, opacity * 255));
                    Color tint = dark
                        ? Color.FromArgb(a, 16, 17, 21)
                        : Color.FromArgb(a, 250, 251, 255);
                    using (SolidBrush b = new SolidBrush(tint))
                        g.FillRectangle(b, 0, 0, blurred.Width, blurred.Height);
                }
                return blurred;
            }
            catch { return blurred; }
        }

        /// <summary>厚材质：近似不透明的竖向渐变，用于信息密度高的卡片。</summary>
        public static void PaintSolid(Graphics g, Rectangle rect, bool dark, int radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath clip = Theme.Round(rect, radius))
            {
                GraphicsState state = g.Save();
                g.SetClip(clip, CombineMode.Replace);
                Color top = dark ? Color.FromArgb(30, 33, 40) : Color.FromArgb(253, 254, 255);
                Color bottom = dark ? Color.FromArgb(20, 22, 27) : Color.FromArgb(245, 247, 251);
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(rect.X, rect.Y, Math.Max(2, rect.Width), Math.Max(2, rect.Height)), top, bottom, 90f))
                    g.FillRectangle(lg, rect);
                g.Restore(state);
            }

            Color outer = dark ? Color.FromArgb(58, 255, 255, 255) : Color.FromArgb(210, 255, 255, 255);
            Color inner = dark ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(26, 15, 23, 42);
            using (GraphicsPath p = Theme.Round(rect, radius))
            using (Pen pen = new Pen(outer, 1f))
                g.DrawPath(pen, p);
            Rectangle inset = new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            using (GraphicsPath p = Theme.Round(inset, Math.Max(2, radius - 1)))
            using (Pen pen = new Pen(inner, 1f))
                g.DrawPath(pen, p);
        }
        // ---------------- 徽标小玻璃（分层窗口专用） ----------------

        /// <summary>
        /// 把已经抓好的一张屏幕图做成“徽标玻璃”：缩小 → 盒式模糊 → 提饱和 → 叠玻璃色。
        /// 徽标只有 28~40 DIP，模糊半径按物理像素自适应，避免糊成一团或一点没糊。
        /// 卡片走 WebView2 的 CSS blur(22px)（GPU，便宜）；徽标是分层窗口，只能自己算。
        /// </summary>
        public static Bitmap FrostedFrom(Bitmap src, bool dark, double opacity, int downscale, int passes, double saturation)
        {
            if (src == null) return null;
            Bitmap blurred = null;
            try
            {
                blurred = Blur(src, downscale, passes);
                if (blurred == null) return null;
                if (Math.Abs(saturation - 1.0) > 0.01) Saturate(blurred, saturation);

                using (Graphics g = Graphics.FromImage(blurred))
                {
                    int a = (int)Math.Max(0, Math.Min(255, opacity * 255));
                    Color tint = dark
                        ? Color.FromArgb(a, 22, 22, 25)
                        : Color.FromArgb(a, 255, 255, 255);
                    using (SolidBrush b = new SolidBrush(tint))
                        g.FillRectangle(b, 0, 0, blurred.Width, blurred.Height);
                }
                return blurred;
            }
            catch
            {
                if (blurred != null) blurred.Dispose();
                return null;
            }
        }

        /// <summary>抓屏 + 小玻璃（徽标用）：失败返回 null，调用方回退纯色玻璃。</summary>
        public static Bitmap FrostedBadge(Rectangle screenRect, bool dark, double opacity)
        {
            Bitmap raw = Capture(screenRect);
            if (raw == null) return null;
            try { return FrostedFrom(raw, dark, opacity, 2, 2, 1.8); }
            finally { raw.Dispose(); }
        }

        /// <summary>提饱和：以亮度为轴放大色差（模拟 CSS saturate()），1.0 = 原样。</summary>
        public static void Saturate(Bitmap bmp, double saturation)
        {
            if (bmp == null || saturation <= 0) return;
            int w = bmp.Width, h = bmp.Height;
            if (w < 2 || h < 2) return;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride;
                    for (int x = 0; x < w; x++, o += 4)
                    {
                        int bb = buf[o], gg = buf[o + 1], rr = buf[o + 2];
                        int gray = (rr * 299 + gg * 587 + bb * 114) / 1000;
                        buf[o]     = Clamp255(gray + (int)((bb - gray) * saturation));
                        buf[o + 1] = Clamp255(gray + (int)((gg - gray) * saturation));
                        buf[o + 2] = Clamp255(gray + (int)((rr - gray) * saturation));
                    }
                }
                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static byte Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return (byte)v;
        }

        /// <summary>圆角裁切 + 描边 + 顶部高光，画出"一块玻璃"。</summary>
        public static void Paint(Graphics g, Rectangle rect, Bitmap frosted, bool dark, int radius)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            using (GraphicsPath clip = Theme.Round(rect, radius))
            {
                GraphicsState state = g.Save();
                g.SetClip(clip, CombineMode.Replace);

                if (frosted != null)
                    g.DrawImageUnscaled(frosted, rect.X, rect.Y);
                else
                    using (SolidBrush b = new SolidBrush(dark ? Color.FromArgb(28, 30, 36) : Color.FromArgb(248, 249, 252)))
                        g.FillRectangle(b, rect);

                // 顶部一道极淡的高光带，模拟玻璃受光
                Rectangle sheen = new Rectangle(rect.X, rect.Y, rect.Width, Math.Max(8, rect.Height / 3));
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    sheen, Color.FromArgb(dark ? 22 : 60, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g.FillRectangle(lg, sheen);

                g.Restore(state);
            }

            // 发丝描边（外亮内暗），玻璃的"厚度"
            Color outer = dark ? Color.FromArgb(46, 255, 255, 255) : Color.FromArgb(180, 255, 255, 255);
            Color inner = dark ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(28, 15, 23, 42);

            using (GraphicsPath p = Theme.Round(rect, radius))
            using (Pen pen = new Pen(outer, 1f))
                g.DrawPath(pen, p);

            Rectangle inset = new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2);
            using (GraphicsPath p = Theme.Round(inset, Math.Max(2, radius - 1)))
            using (Pen pen = new Pen(inner, 1f))
                g.DrawPath(pen, p);
        }
    }
}
