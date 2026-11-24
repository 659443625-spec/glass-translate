using System;
using System.Drawing;

namespace MouseTranslate
{
    /// <summary>
    /// 统一的高 DPI 缩放。程序是 DPI 感知的（取词坐标与截屏都是物理像素），
    /// 但界面按 96 DPI 设计，所以所有像素坐标都过一遍 S()。
    /// 字号用磅值，系统会自动按 DPI 放大，不需要额外处理。
    /// </summary>
    internal static class Dpi
    {
        private static float _factor = -1f;

        public static float Factor
        {
            get
            {
                if (_factor < 0)
                {
                    try
                    {
                        uint dpi = Native.GetDpiForSystem();
                        if (dpi >= 96) _factor = dpi / 96f;
                    }
                    catch { }
                    if (_factor < 0)
                    {
                        try
                        {
                            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                                _factor = g.DpiX / 96f;
                        }
                        catch { _factor = 1f; }
                    }
                    if (_factor < 1f) _factor = 1f;
                    if (_factor > 4f) _factor = 4f;
                }
                return _factor;
            }
        }

        public static int S(int v)
        {
            return (int)Math.Round(v * Factor);
        }

        public static float Sf(float v)
        {
            return v * Factor;
        }
    }
}
