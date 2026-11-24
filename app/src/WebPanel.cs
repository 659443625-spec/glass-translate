using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MouseTranslate
{
    /// <summary>WebView2 承载窗口：圆角无边框 + 透明背景 + 系统投影。</summary>
    internal sealed class WebPanel : UserControl
    {
        private readonly WebView2 _web = new WebView2();
        private bool _initStarted;

        public string Html = "<html><body style='background:transparent'></body></html>";
        public event Action<string> Message;

        public WebPanel()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(20, 22, 27);

            _web.Dock = DockStyle.Fill;
            _web.DefaultBackgroundColor = Color.Transparent;
            Controls.Add(_web);

            _web.CoreWebView2InitializationCompleted += delegate (object s, CoreWebView2InitializationCompletedEventArgs e)
            {
                if (!e.IsSuccess) return;
                try
                {
                    _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                    _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                    _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                    _web.CoreWebView2.Settings.IsZoomControlEnabled = false;
                    _web.CoreWebView2.WebMessageReceived += delegate (object s2, CoreWebView2WebMessageReceivedEventArgs a)
                    {
                        try { if (Message != null) Message(a.TryGetWebMessageAsString()); } catch { }
                    };
                    _web.CoreWebView2.NavigateToString(Html);
                }
                catch { }
            };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!_initStarted) { _initStarted = true; InitAsync(); }
        }

        private async void InitAsync()
        {
            try
            {
                string dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MouseTranslate", "WebView2");
                Directory.CreateDirectory(dataDir);
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, dataDir).ConfigureAwait(true);
                await _web.EnsureCoreWebView2Async(env);
            }
            catch { }
        }

        public void Reload()
        {
            try { if (_web.CoreWebView2 != null) _web.CoreWebView2.NavigateToString(Html); } catch { }
        }

        public void Send(string json)
        {
            try { if (_web.CoreWebView2 != null) _web.CoreWebView2.PostWebMessageAsJson(json); } catch { }
        }

        public void RunScript(string js)
        {
            try { if (_web.CoreWebView2 != null) _web.CoreWebView2.ExecuteScriptAsync(js); } catch { }
        }
    }
    /// <summary>
    /// 抓帧统计：玻璃要“透”但不能牺牲可读性，所以先量背后那张图。
    /// 只用 4 个数字就能判断三件事：是不是亮/暗背景（mean/p5/p95）、
    /// 背景本身是否高对比或密（StdDev）、是否高饱和花哨（MeanSat）。
    /// </summary>
    internal sealed class BackdropStats
    {
        public bool Valid;
        public int Mean = 128;   // 平均亮度（0~255）
        public int P5 = 0;       // 第 5 百分位亮度（偏暗那头的代表）
        public int P95 = 255;    // 第 95 百分位亮度（偏亮那头的代表）
        public int StdDev;       // 亮度标准差：密集文字 / 高对比图片会很大
        public int MeanSat;      // 平均饱和（max-min），花哨图片会很大
        public int Samples;
    }

    /// <summary>面板 HTML：苹果材质靠 CSS 的 blur + saturate + 变量化配色。</summary>
    internal static class WebUi
    {
        public static string ToBase64(Bitmap bmp)
        {
            if (bmp == null) return string.Empty;
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // ---------------- 可读性判据（WCAG 2.x 对比度） ----------------

        // 与 card.html / records.html 里的 CSS 变量一一对应，改这里必须同步改 CSS。
        // 玻璃档位：0 标准 / 1 加厚 / 2 纯色兜底。
        private static readonly double[] LightAlpha = new double[] { 0.72, 0.84, 0.94 };
        private static readonly double[] DarkAlpha = new double[] { 0.76, 0.86, 0.95 };
        private const int LightGlassRgb = 255;
        private const int DarkGlassRgb = 28;
        private const int LightInkRgb = 29;      // #1d1d1f
        private const int DarkInkRgb = 245;      // #f5f5f7
        // 文字色（优先用文字对比度兜住可读性，避免把常见背景也加厚）：
        // 浅色 --fg2 .74 / --fg3 .58；深色 --fg2 .76 / --fg3 .56。
        private const double LightSecondaryA = 0.74;
        private const double DarkSecondaryA = 0.78;
        private const double LightTertiaryA = 0.58;
        private const double DarkTertiaryA = 0.56;

        // 徽标内衬（scrim）：玻璃层与字形之间再加一层，保证细线在任何背景上都有对比。
        private const double LightIconScrim = 0.55;   // 白内衬
        private const double DarkIconScrim = 0.45;    // 黑内衬
        private const int IconInkSwitchLuma = 148;    // 内衬后亮度 >148 → 深色字形，否则白色字形

        private static double Lin(int c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        private static double Luma(int gray)
        {
            return Lin(gray);
        }

        private static double Ratio(double l1, double l2)
        {
            double hi = Math.Max(l1, l2);
            double lo = Math.Min(l1, l2);
            return (hi + 0.05) / (lo + 0.05);
        }

        private static int Mix(int fg, int bg, double alpha)
        {
            int v = (int)Math.Round(fg * alpha + bg * (1.0 - alpha));
            if (v < 0) v = 0;
            if (v > 255) v = 255;
            return v;
        }

        /// <summary>玻璃叠在某个亮度上之后的合成亮度（sRGB 空间近似，够判 4.5:1 用）。</summary>
        public static int CompositeLuma(bool dark, int mode, int backdropLuma)
        {
            double a = dark ? DarkAlpha[mode] : LightAlpha[mode];
            return Mix(dark ? DarkGlassRgb : LightGlassRgb, backdropLuma, a);
        }

        /// <summary>正文（--fg，几乎不透明）在指定背景亮度下的对比度。</summary>
        public static double PrimaryContrast(bool dark, int mode, int backdropLuma)
        {
            int c = CompositeLuma(dark, mode, backdropLuma);
            return Ratio(Luma(c), dark ? Luma(DarkInkRgb) : Luma(LightInkRgb));
        }

        /// <summary>次级文字（--fg2，半透明叠在玻璃上）的对比度 —— 它最先掉到 4.5 以下。</summary>
        public static double SecondaryContrast(bool dark, int mode, int backdropLuma)
        {
            int c = CompositeLuma(dark, mode, backdropLuma);
            int ink = Mix(dark ? DarkInkRgb : LightInkRgb, c, dark ? DarkSecondaryA : LightSecondaryA);
            return Ratio(Luma(c), Luma(ink));
        }

        /// <summary>三级文字（--fg3，仅 meta 小字）的对比度（报告用，不作为 4.5 判据）。</summary>
        public static double TertiaryContrast(bool dark, int mode, int backdropLuma)
        {
            int c = CompositeLuma(dark, mode, backdropLuma);
            int ink = Mix(dark ? DarkInkRgb : LightInkRgb, c, dark ? DarkTertiaryA : LightTertiaryA);
            return Ratio(Luma(c), Luma(ink));
        }

        // ---------------- 徽标内衬（scrim）与字形颜色 ----------------

        /// <summary>徽标合成亮度：抓帧亮度 → 叠玻璃色 → 再叠内衬（浅色白 0.55 / 深色黑 0.45）。</summary>
        public static int IconCompositeLuma(bool dark, double glassAlpha, int backdropLuma)
        {
            int g = Mix(dark ? DarkGlassRgb : LightGlassRgb, backdropLuma, glassAlpha);
            if (dark) return Mix(0, g, DarkIconScrim);
            return Mix(255, g, LightIconScrim);
        }

        /// <summary>按“玻璃 + 内衬”合成后的亮度自动选字形颜色：亮底深字 / 暗底白字。</summary>
        public static bool PickIconInkDark(bool dark, double glassAlpha, int backdropLuma)
        {
            return IconCompositeLuma(dark, glassAlpha, backdropLuma) > IconInkSwitchLuma;
        }

        /// <summary>细线字形最终对比度（报告/自检用）。</summary>
        public static double IconInkContrast(bool dark, double glassAlpha, int backdropLuma)
        {
            bool inkDark = PickIconInkDark(dark, glassAlpha, backdropLuma);
            int c = IconCompositeLuma(dark, glassAlpha, backdropLuma);
            return Ratio(Luma(c), inkDark ? Luma(LightInkRgb) : Luma(DarkInkRgb));
        }

        /// <summary>
        /// 量一张抓帧图。采样步长随尺寸自适应（卡片约 20k 点、徽标约 3k 点），
        /// 用 LockBits 一次拷出来，比 GetPixel 快两个数量级。
        /// </summary>
        public static BackdropStats Analyze(Bitmap bmp)
        {
            BackdropStats st = new BackdropStats();
            if (bmp == null || bmp.Width < 2 || bmp.Height < 2) return st;
            try
            {
                Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
                BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] buf;
                int stride;
                int w, h;
                try
                {
                    stride = data.Stride;
                    w = bmp.Width;
                    h = bmp.Height;
                    buf = new byte[stride * h];
                    Marshal.Copy(data.Scan0, buf, 0, buf.Length);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                int step = (w + h) / 240;
                if (step < 1) step = 1;

                int[] hist = new int[256];
                long sum = 0;
                double sumSq = 0;
                long sumSat = 0;
                int n = 0;
                for (int y = 0; y < h; y += step)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += step)
                    {
                        int o = row + x * 4;
                        int bb = buf[o], gg = buf[o + 1], rr = buf[o + 2];
                        int luma = (rr * 299 + gg * 587 + bb * 114) / 1000;
                        if (luma < 0) luma = 0;
                        if (luma > 255) luma = 255;
                        hist[luma]++;
                        sum += luma;
                        sumSq += (double)luma * luma;
                        int mx = Math.Max(rr, Math.Max(gg, bb));
                        int mn = Math.Min(rr, Math.Min(gg, bb));
                        sumSat += mx - mn;
                        n++;
                    }
                }

                if (n == 0) return st;
                st.Valid = true;
                st.Samples = n;
                st.Mean = (int)(sum / n);

                int p5 = (int)(n * 0.05);
                int p95 = (int)(n * 0.95);
                int acc = 0;
                int low = 0;
                int high = 255;
                bool lowFound = false;
                bool highFound = false;
                for (int i = 0; i < 256; i++)
                {
                    acc += hist[i];
                    if (!lowFound && acc >= p5) { low = i; lowFound = true; }
                    if (acc >= p95) { high = i; highFound = true; break; }
                }
                if (!lowFound) low = high;
                if (!highFound) high = 255;
                st.P5 = low;
                st.P95 = high;

                double mean = sum / (double)n;
                double var = sumSq / n - mean * mean;
                if (var < 0) var = 0;
                st.StdDev = (int)Math.Round(Math.Sqrt(var));
                st.MeanSat = (int)(sumSat / n);
                return st;
            }
            catch
            {
                return st;
            }
        }

        /// <summary>
        /// 选卡片玻璃档位：0 = 标准玻璃（0.72/0.76），1 = 加厚（0.84/0.86），2 = 兜底纯色（0.94/0.95）。
        ///
        /// 统筹评审后的规则（优先调文字色、把加厚留给真正恶劣的背景）：
        ///  · 常见场景（白底网页 / 深底网页）必须留在 mode0 —— 文字色提到 --fg2 .74/.76 后，
        ///    mode0 的次级文字最坏也有 4.5:1（浅 B=0 4.7 / 深 B=255 4.7），不再需要靠加厚玻璃去兜。
        ///  · 只有「真正恶劣」才加厚：高饱和图片（MeanSat ≥ 90）、彩色图文密排（StdDev ≥ 75 且 MeanSat ≥ 55）。
        ///  · 只有「极端亮度」才纯色：纯黑 / 纯白页面。
        ///  · 最后仍保留对比度保底循环（防御性，正常不会触发）。
        /// </summary>
        public static int PickCardMode(bool dark, BackdropStats st)
        {
            if (st == null || !st.Valid) return 2;

            int mode = 0;

            // 1) 真正恶劣的彩色背景 → 加厚（白底/深底纯文字网页 MeanSat≈0~3；带少量配色的常见网页 <90 也不会命中）
            if (st.MeanSat >= 90) mode = 1;                       // 高饱和图片 / 花哨壁纸
            if (st.StdDev >= 75 && st.MeanSat >= 55) mode = 1;    // 彩色图文密排

            // 2) 极端亮度 → 纯色兜底
            if (dark)
            {
                if (st.Mean >= 246 && st.StdDev < 30) mode = 2;   // 纯白网页
                if (st.Mean <= 10) mode = 2;                       // 纯黑网页/桌面
            }
            else
            {
                if (st.Mean <= 14 && st.P5 <= 22) mode = 2;        // 纯黑网页/桌面
                if (st.Mean >= 250 && st.StdDev < 16) mode = 2;    // 纯白网页
            }

            // 3) 对比度保底（新文字色下 mode0 已达标，这里只做防御）
            int worst = dark ? Math.Min(255, st.P95 + 8) : Math.Max(0, st.P5 - 8);
            while (mode < 2 && SecondaryContrast(dark, mode, worst) < 4.5) mode++;

            if (mode < 0) mode = 0;
            if (mode > 2) mode = 2;
            return mode;
        }

        /// <summary>
        /// 徽标玻璃底色不透明度：现在字形对比由「内衬 scrim」兜底，玻璃色只负责一点点氛围，
        /// 所以整体更透、更中性（避免整颗徽标被背景染色）。亮/暗两侧仍按极值微调。
        /// </summary>
        public static double PickIconAlpha(bool dark, BackdropStats st)
        {
            if (st == null || !st.Valid) return 0.96;
            double a = dark ? 0.48 : 0.45;
            if (dark)
            {
                if (st.P95 >= 190 || st.Mean >= 180) a = 0.58;   // 亮底：玻璃稍厚，压住灰感
                if (st.Mean >= 240) a = 0.66;
            }
            else
            {
                if (st.P5 <= 60 || st.Mean <= 32) a = 0.56;      // 暗底：玻璃稍厚
                if (st.Mean <= 12) a = 0.66;
            }
            return a;
        }

        /// <summary>WCAG 对比度（公开给自检/报告用）。</summary>
        public static double ContrastRatio(Color a, Color b)
        {
            double la = 0.2126 * Lin(a.R) + 0.7152 * Lin(a.G) + 0.0722 * Lin(a.B);
            double lb = 0.2126 * Lin(b.R) + 0.7152 * Lin(b.G) + 0.0722 * Lin(b.B);
            return Ratio(la, lb);
        }

        private const string Template = @"<!doctype html><html><head><meta charset='utf-8'><style>
/* ============ 控制面板 · Apple Liquid Glass ============
   作用域：本页全部视觉规则都挂在 .bg/.tint/.frame/.primary/.row/.quit 等面板专属类上，
   不写标签选择器，不和宿主页面共享任何东西（WebView2 本身也是独立文档）。 */
html,body{width:100%;height:100%;background:transparent;overflow:hidden;
  font-family:-apple-system,BlinkMacSystemFont,'SF Pro Text','PingFang SC','Segoe UI Variable Text','Segoe UI','Microsoft YaHei UI',sans-serif;
  -webkit-font-smoothing:antialiased;user-select:none;cursor:default;color-scheme:light dark}
body{--fg:rgba(29,29,31,.94);--fg2:rgba(29,29,31,.74);--fg3:rgba(29,29,31,.58);
  --glass:rgba(255,255,255,.72);--edge:rgba(255,255,255,.65);--inner:rgba(255,255,255,.5);
  --line:rgba(29,29,31,.10);--btn2:rgba(255,255,255,.42);--track:rgba(120,132,150,.30);
  --accent:#007aff;--dot:rgba(140,150,168,.9)}
body.dark{--fg:rgba(245,245,247,.96);--fg2:rgba(245,245,247,.78);--fg3:rgba(245,245,247,.56);
  --glass:rgba(28,28,30,.76);--edge:rgba(255,255,255,.16);--inner:rgba(255,255,255,.5);
  --line:rgba(255,255,255,.12);--btn2:rgba(255,255,255,.08);--track:rgba(255,255,255,.16);
  --accent:#0a84ff;--dot:rgba(150,160,175,.9)}
body.light.solid{--glass:rgba(255,255,255,.88)}
body.dark.solid{--glass:rgba(28,28,30,.88)}
.bg{position:fixed;left:-40px;top:-40px;right:-40px;bottom:-40px;
  background-image:url(data:image/png;base64,{{BG}});background-size:cover;background-position:center;
  filter:blur(22px) saturate(150%) brightness(1.02);transform:scale(1.12)}
.tint{position:fixed;inset:0;background:var(--glass)}
.sheen{position:fixed;inset:0;border-radius:20px;pointer-events:none;
  background:radial-gradient(90% 55% at 13% -20%,rgba(255,255,255,.28),transparent 60%),
             linear-gradient(160deg,rgba(255,255,255,.08),transparent 40%)}
body.dark .sheen{background:radial-gradient(90% 55% at 13% -20%,rgba(255,255,255,.12),transparent 60%),
             linear-gradient(160deg,rgba(255,255,255,.04),transparent 40%)}
.frame{position:fixed;inset:0;border-radius:20px;border:1px solid var(--edge);
  box-shadow:inset 0 1px 0 var(--inner),inset 0 -20px 30px -26px rgba(0,0,0,.16)}
.dots{position:absolute;left:20px;top:16px;display:flex;gap:8px;z-index:2}
.dot{width:12px;height:12px;border-radius:50%;box-shadow:inset 0 0 0 .5px rgba(0,0,0,.16);cursor:pointer;
  transition:transform 180ms cubic-bezier(.22,1,.36,1),filter 180ms cubic-bezier(.22,1,.36,1)}
.dot:hover{transform:scale(1.08);filter:brightness(1.06)}
.dot.r{background:#ff5f57}.dot.y{background:#febc2e}.dot.g{background:#28c840;opacity:.5}
.title{position:absolute;left:0;right:0;top:15px;text-align:center;font-size:13px;
  font-weight:600;color:var(--fg2);letter-spacing:.2px}
.status{position:absolute;left:22px;top:50px;display:flex;align-items:center;gap:8px;
  font-size:12px;font-weight:600;color:var(--fg2);z-index:2}
body.run .status{color:var(--fg)}
.led{width:7px;height:7px;border-radius:50%;background:var(--dot);box-shadow:0 0 0 3px rgba(140,150,168,.14);
  transition:background 180ms cubic-bezier(.22,1,.36,1),box-shadow 180ms cubic-bezier(.22,1,.36,1)}
body.run .led{background:#34d399;box-shadow:0 0 0 3px rgba(52,211,153,.18)}
.primary{position:absolute;left:20px;right:20px;top:86px;height:44px;border:none;border-radius:12px;
  background:linear-gradient(180deg,#0a84ff,#0a6fe0);color:#fff;font-size:13.5px;font-weight:600;
  font-family:inherit;letter-spacing:.2px;cursor:pointer;
  box-shadow:0 1px 2px rgba(16,24,40,.10),0 6px 14px rgba(10,110,220,.16),inset 0 1px 0 rgba(255,255,255,.22);
  transition:transform 180ms cubic-bezier(.22,1,.36,1),box-shadow 180ms cubic-bezier(.22,1,.36,1);
  z-index:2}
.primary:active{transform:translateY(1px) scale(.985);box-shadow:0 1px 2px rgba(16,24,40,.10),0 3px 8px rgba(10,110,220,.14),inset 0 1px 0 rgba(255,255,255,.18)}
.row{position:absolute;left:22px;right:22px;height:38px;display:flex;align-items:center;
  justify-content:space-between;font-size:13px;color:var(--fg2);z-index:2}
.sw{width:46px;height:26px;border-radius:13px;background:var(--track);position:relative;
  cursor:pointer;transition:background 200ms cubic-bezier(.22,1,.36,1);z-index:2}
.sw.on{background:var(--accent)}
.sw i{position:absolute;top:2px;left:2px;width:22px;height:22px;border-radius:50%;background:#fff;
  box-shadow:0 1px 2px rgba(0,0,0,.18),0 3px 8px rgba(0,0,0,.14);
  transition:transform 200ms cubic-bezier(.22,1,.36,1)}
.sw.on i{transform:translateX(20px)}
.sw:active i{width:24px}
.hint{position:absolute;left:22px;top:320px;font-size:11px;color:var(--fg3);z-index:2}
.hint2{position:absolute;left:22px;top:338px;font-size:10.5px;color:var(--fg3);opacity:.85;z-index:2}
.hr{position:absolute;left:22px;right:22px;top:358px;height:1px;background:var(--line);
  -webkit-mask-image:linear-gradient(90deg,transparent,#000 8%,#000 92%,transparent);
  mask-image:linear-gradient(90deg,transparent,#000 8%,#000 92%,transparent);z-index:2}
body.three .row.alt{display:none}
body.three .hint{top:282px}
body.three .hint2{top:300px}
body.three .hr{top:320px}
.quit{position:absolute;right:20px;bottom:16px;height:30px;padding:0 16px;border-radius:9px;
  border:1px solid var(--line);background:var(--btn2);color:var(--fg2);font-size:12px;
  font-family:inherit;cursor:pointer;z-index:2;
  -webkit-backdrop-filter:blur(18px) saturate(180%);backdrop-filter:blur(18px) saturate(180%);
  box-shadow:inset 0 1px 0 var(--inner);
  transition:background 180ms cubic-bezier(.22,1,.36,1),transform 180ms cubic-bezier(.22,1,.36,1)}
.quit:hover{background:var(--glass);color:var(--fg)}
.quit:active{transform:scale(.94)}
/* 兜底：没有 backdrop-filter 时内部小件退回接近纯色，不能变透明 */
@supports not ((backdrop-filter:blur(1px)) or (-webkit-backdrop-filter:blur(1px))){
  .quit{background:rgba(255,255,255,.96);color:rgba(29,29,31,.78)}
  body.dark .quit{background:rgba(36,36,40,.97);color:rgba(245,245,247,.8)}
}
@media (prefers-color-scheme:dark){
  /* 正常路径由 body.dark/light 决定（C# 读系统 AppsUseLightTheme，与 prefers-color-scheme 同源）；
     万一模板没带 class，这里兜一层，避免出现浅色字压在浅色玻璃上。 */
  body:not(.light):not(.dark){--fg:rgba(245,245,247,.96);--fg2:rgba(245,245,247,.78);--fg3:rgba(245,245,247,.56);
    --glass:rgba(28,28,30,.76);--edge:rgba(255,255,255,.16);--inner:rgba(255,255,255,.5);
    --line:rgba(255,255,255,.12);--btn2:rgba(255,255,255,.08);--track:rgba(255,255,255,.16);
    --accent:#0a84ff}
}
@media (prefers-reduced-motion:reduce){
  .primary,.quit,.sw,.sw i,.dot,.led{transition-duration:1ms}
}
</style></head><body class='{{CLS}}'>
<div class='bg'></div><div class='tint'></div><div class='sheen'></div><div class='frame'></div>
<div class='dots'><div class='dot r'></div><div class='dot y'></div><div class='dot g'></div></div>
<div class='title'>鼠标翻译</div>
<div class='status'><span class='led'></span><span>{{STATUS}}</span></div>
<button class='primary' id='go'>{{GO}}</button>
<div class='row' style='top:156px'><span>划词翻译</span><div class='sw{{SELECT}}' data-k='select'><i></i></div></div>
<div class='row' style='top:194px'><span>悬停取词</span><div class='sw{{HOVER}}' data-k='hover'><i></i></div></div>
<div class='row' style='top:232px'><span>剪贴板兜底</span><div class='sw{{CLIP}}' data-k='clip'><i></i></div></div>
<div class='row alt' style='top:270px'><span>悬停需按住 Alt</span><div class='sw{{ALT}}' data-k='alt'><i></i></div></div>
<div class='hint'>自动判断方向：英文 → 中文，中文 → 英文</div>
<div class='hint2'>{{NOTE}}</div>
<div class='hr'></div>
<button class='quit' id='records' style='right:86px'>记录</button>
<button class='quit' id='quit'>退出</button>
<script>
document.querySelectorAll('.sw').forEach(function(el){
  el.addEventListener('click',function(){
    el.classList.toggle('on');
    window.chrome.webview.postMessage(JSON.stringify({type:'toggle',key:el.dataset.k,on:el.classList.contains('on')}));
  });
});
document.getElementById('go').addEventListener('click',function(){window.chrome.webview.postMessage(JSON.stringify({type:'toggleRun'}));});
document.getElementById('quit').addEventListener('click',function(){window.chrome.webview.postMessage(JSON.stringify({type:'quit'}));});
document.getElementById('records').addEventListener('click',function(){window.chrome.webview.postMessage(JSON.stringify({type:'records'}));});
document.querySelector('.dot.r').addEventListener('click',function(){window.chrome.webview.postMessage(JSON.stringify({type:'hide'}));});
document.querySelector('.dot.y').addEventListener('click',function(){window.chrome.webview.postMessage(JSON.stringify({type:'hide'}));});
window.__setRun = function(run){
  document.body.classList.toggle('run', !!run);
  var st = document.querySelector('.status span:last-child');
  if (st) st.textContent = run ? '运行中' : '未启动';
  var go = document.getElementById('go');
  if (go) go.textContent = run ? '停止翻译' : '开始翻译';
};
</script></body></html>";

        /// <summary>粗略采样背景亮度（每 16 像素取一点）。</summary>
        public static int AverageLuma(Bitmap bmp)
        {
            if (bmp == null) return 128;
            try
            {
                long sum = 0; int n = 0;
                for (int y = 0; y < bmp.Height; y += 16)
                {
                    for (int x = 0; x < bmp.Width; x += 16)
                    {
                        Color c = bmp.GetPixel(x, y);
                        sum += (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                        n++;
                    }
                }
                return n == 0 ? 128 : (int)(sum / n);
            }
            catch { return 128; }
        }

        public static string Panel(bool dark, string bgBase64, bool running, bool select, bool hover, bool clip, bool alt, bool solid, string note)
        {
            string cls = (dark ? "dark" : "light") + (running ? " run" : "") + (hover ? "" : " three") + (solid ? " solid" : "");
            return Template
                .Replace("{{BG}}", bgBase64)
                .Replace("{{CLS}}", cls)
                .Replace("{{STATUS}}", running ? "运行中" : "未启动")
                .Replace("{{GO}}", running ? "停止翻译" : "开始翻译")
                .Replace("{{SELECT}}", select ? " on" : "")
                .Replace("{{HOVER}}", hover ? " on" : "")
                .Replace("{{CLIP}}", clip ? " on" : "")
                .Replace("{{NOTE}}", note ?? string.Empty)
                .Replace("{{ALT}}", alt ? " on" : "");
        }

        private static string _cardTemplate;

        private static string LoadCardTemplate()
        {
            if (_cardTemplate == null)
            {
                try
                {
                    using (System.IO.Stream s = typeof(WebUi).Assembly.GetManifestResourceStream("MouseTranslate.card.html"))
                    using (System.IO.StreamReader r = new System.IO.StreamReader(s))
                        _cardTemplate = r.ReadToEnd();
                }
                catch { _cardTemplate = "<html><body style='background:#000'></body></html>"; }
            }
            return _cardTemplate;
        }

        /// <summary>
        /// 卡片模板来自嵌入资源 assets\card.html，避免 C# 字符串转义地狱。
        /// mode：0 标准玻璃 / 1 加厚 / 2 纯色兜底（由 CaptureBackdrop 按抓帧统计算出来）。
        /// 抓帧失败（bg 为空）时加 .nobackdrop，页面自己切纯色，不会露出宿主窗口底色。
        /// </summary>
        public static string CardShell(bool dark, string bgBase64, int mode)
        {
            bool hasBg = bgBase64 != null && bgBase64.Length > 0;
            if (mode < 0) mode = 0;
            if (mode > 2) mode = 2;
            string cls = (dark ? "dark" : "light") + " mode-" + mode.ToString() + (hasBg ? string.Empty : " nobackdrop");
            return LoadCardTemplate()
                .Replace("{{BG}}", hasBg ? bgBase64 : string.Empty)
                .Replace("{{CLS}}", cls);
        }

        /// <summary>旧签名（二值 solid）保留，内部映射到新档位。</summary>
        public static string CardShell(bool dark, string bgBase64, bool solid)
        {
            return CardShell(dark, bgBase64, solid ? 2 : 0);
        }

        // ---------------- 记录窗口 ----------------

        private static string _recordsTemplate;

        private static string LoadRecordsTemplate()
        {
            if (_recordsTemplate == null)
            {
                try
                {
                    using (System.IO.Stream s = typeof(WebUi).Assembly.GetManifestResourceStream("MouseTranslate.records.html"))
                    using (System.IO.StreamReader r = new System.IO.StreamReader(s))
                        _recordsTemplate = r.ReadToEnd();
                }
                catch { _recordsTemplate = "<html><body style='background:#111;color:#fff'>记录加载失败</body></html>"; }
            }
            return _recordsTemplate;
        }

        /// <summary>记录窗口：自己解一遍抓帧 PNG 来判档位（RecordsWindow 不归本轮改，签名保持不变）。</summary>
        public static string RecordsShell(bool dark, string bgBase64)
        {
            bool hasBg = bgBase64 != null && bgBase64.Length > 0;
            int mode = 2;
            if (hasBg)
            {
                try
                {
                    byte[] raw = Convert.FromBase64String(bgBase64);
                    using (MemoryStream ms = new MemoryStream(raw))
                    using (Bitmap b = new Bitmap(ms))
                        mode = PickCardMode(dark, Analyze(b));
                }
                catch { hasBg = false; mode = 2; }
            }
            string cls = (dark ? "dark" : "light") + " mode-" + mode.ToString() + (hasBg ? string.Empty : " nobackdrop");
            return LoadRecordsTemplate()
                .Replace("{{BG}}", hasBg ? bgBase64 : string.Empty)
                .Replace("{{CLS}}", cls);
        }
    }
}
