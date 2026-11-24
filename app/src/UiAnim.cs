using System;
using System.Drawing;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>轻量入场动效：淡入 + 轻微上浮（ease-out）。</summary>
    internal static class UiAnim
    {
        /// <summary>只做位移的入场（不改 Opacity，避免窗口变分层后子控件不渲染）。</summary>
        public static void SlideIn(Form f, int durationMs, int offsetY)
        {
            try
            {
                int endTop = f.Top;
                int startTop = endTop + offsetY;
                f.Top = startTop;

                int elapsed = 0;
                Timer t = new Timer();
                t.Interval = 15;
                t.Tick += delegate
                {
                    try
                    {
                        elapsed += t.Interval;
                        double p = Math.Min(1.0, elapsed / (double)durationMs);
                        double e = 1.0 - Math.Pow(1.0 - p, 3.0);
                        f.Top = (int)Math.Round(startTop - offsetY * e);
                        if (p >= 1.0) { f.Top = endTop; t.Stop(); t.Dispose(); }
                    }
                    catch { try { t.Stop(); t.Dispose(); } catch { } }
                };
                t.Start();
            }
            catch { }
        }
        public static void FadeIn(Form f, int durationMs, int offsetY)
        {
            try
            {
                int startTop = f.Top + offsetY;
                f.Top = startTop;
                f.Opacity = 0.0;

                int elapsed = 0;
                Timer t = new Timer();
                t.Interval = 15;
                t.Tick += delegate
                {
                    try
                    {
                        elapsed += t.Interval;
                        double p = Math.Min(1.0, elapsed / (double)durationMs);
                        double e = 1.0 - Math.Pow(1.0 - p, 3.0);   // ease-out cubic

                        f.Opacity = e;
                        if (offsetY != 0) f.Top = (int)Math.Round(startTop - offsetY * e);

                        if (p >= 1.0)
                        {
                            f.Opacity = 1.0;
                            t.Stop();
                            t.Dispose();
                        }
                    }
                    catch
                    {
                        try { f.Opacity = 1.0; } catch { }
                        try { t.Stop(); t.Dispose(); } catch { }
                    }
                };
                t.Start();
            }
            catch
            {
                try { f.Opacity = 1.0; } catch { }
            }
        }
    }
}
