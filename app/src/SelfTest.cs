using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>自检模式：--selftest &lt;输出文件&gt;。用于在无人值守的情况下验证各条链路。</summary>
    internal static class SelfTest
    {
        public static int Run(string outFile)
        {
            StringBuilder sb = new StringBuilder();
            int failures = 0;

            sb.AppendLine("=== 鼠标翻译 自检报告 ===");
            sb.AppendLine("时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("系统：" + Environment.OSVersion.VersionString);
            sb.AppendLine("进程位数：" + (IntPtr.Size == 8 ? "x64" : "x86"));
            sb.AppendLine("屏幕：" + Screen.PrimaryScreen.Bounds.Width + " x " + Screen.PrimaryScreen.Bounds.Height);
            sb.AppendLine();

            // ---------- 1. OCR 管道（用自己渲染的图片，结果可判定） ----------
            sb.AppendLine("--- 1. OCR 管道 ---");
            OcrHost ocr = new OcrHost();
            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                bool ok = ocr.Start();
                sw.Stop();
                sb.AppendLine("启动 OCR 子进程：" + (ok ? "成功" : "失败") + " 用时 " + sw.ElapsedMilliseconds + " ms");
                if (!ok)
                {
                    sb.AppendLine("  原因：" + ocr.LastError);
                    failures++;
                }
                else
                {
                    string img = Path.Combine(Path.GetTempPath(), "mt-selftest-" + Guid.NewGuid().ToString("N") + ".png");
                    RenderSample(img);

                    Stopwatch sw2 = Stopwatch.StartNew();
                    OcrResult r = ocr.RecognizeFile(img);
                    sw2.Stop();
                    sb.AppendLine("识别自带样张：" + (r.Ok ? "成功" : "失败 " + r.Error) + "  用时 " + sw2.ElapsedMilliseconds + " ms");
                    if (r.Ok)
                    {
                        sb.AppendLine("  识别文字：" + r.Text.Replace("\r", " ").Replace("\n", " | "));
                        sb.AppendLine("  单词数：" + CountWords(r));
                        bool hit = r.Text.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0;
                        sb.AppendLine("  关键内容命中 'Selection'：" + (hit ? "是" : "否"));
                        if (!hit) failures++;
                    }
                    else failures++;
                    try { File.Delete(img); } catch { }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("OCR 异常：" + ex.Message);
                failures++;
            }

            // ---------- 2. 屏幕实拍 OCR（仅信息，结果取决于桌面内容） ----------
            sb.AppendLine();
            sb.AppendLine("--- 2. 屏幕实拍 OCR（信息性） ---");
            try
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int x = wa.Left + 80;
                int y = wa.Top + 80;
                Stopwatch sw = Stopwatch.StartNew();
                OcrResult r = ocr.CaptureAndRecognize(x, y, 600, 120);
                sw.Stop();
                sb.AppendLine("区域 " + x + "," + y + " 600x120 → " + (r.Ok ? "成功" : "失败 " + r.Error) +
                              "  用时 " + sw.ElapsedMilliseconds + " ms  字数 " + r.Text.Length);
                if (r.Ok && r.Text.Length > 0)
                {
                    string t = r.Text.Replace("\r", " ").Replace("\n", " | ");
                    if (t.Length > 120) t = t.Substring(0, 120) + "…";
                    sb.AppendLine("  前 120 字：" + t);
                }
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }

            // ---------- 3. UI Automation ----------
            sb.AppendLine();
            sb.AppendLine("--- 3. UI Automation ---");
            try
            {
                AutomationElement el = AutomationElement.FocusedElement;
                if (el == null)
                {
                    sb.AppendLine("FocusedElement：空");
                }
                else
                {
                    object pat;
                    bool hasText = el.TryGetCurrentPattern(TextPattern.Pattern, out pat);
                    sb.AppendLine("FocusedElement 类型：" + el.Current.ControlType.ProgrammaticName);
                    sb.AppendLine("支持 TextPattern（可精确取词）：" + (hasText ? "是" : "否"));
                    sb.AppendLine("名称：" + (el.Current.Name ?? string.Empty));
                }
                sb.AppendLine("结论：UIA 模块可用。");
            }
            catch (Exception ex)
            {
                sb.AppendLine("UIA 异常：" + ex.Message);
                failures++;
            }

            // ---------- 4. 翻译通道 ----------
            sb.AppendLine();
            sb.AppendLine("--- 4. 翻译通道 ---");
            try
            {
                Translator tr = new Translator();
                Stopwatch sw = Stopwatch.StartNew();
                TranslateResult r = tr.TranslateAsync("Hello, world. This is a self test.", "zh-CN").GetAwaiter().GetResult();
                sw.Stop();
                sb.AppendLine("结果：" + (r.Ok ? "成功" : "失败"));
                if (r.Ok)
                {
                    sb.AppendLine("  通道：" + r.Provider + "  用时 " + r.Ms + " ms");
                    sb.AppendLine("  译文：" + r.Text);
                }
                else
                {
                    sb.AppendLine("  错误：" + r.Error.Replace("\n", " / "));
                    failures++;
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("异常：" + ex.Message);
                failures++;
            }

            // ---------- 5. 剪贴板 ----------
            sb.AppendLine();
            sb.AppendLine("--- 5. 剪贴板 ---");
            try
            {
                string has = Clipboard.ContainsText() ? "有文本" : "无文本";
                sb.AppendLine("当前剪贴板：" + has + "（仅信息，不修改）");
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex.Message); }

            // ---------- 收尾 ----------
            sb.AppendLine();
            sb.AppendLine("=== 结论：" + (failures == 0 ? "全部通过" : failures + " 项失败") + " ===");

            try { ocr.Dispose(); } catch { }
            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return failures == 0 ? 0 : 1;
        }

        public static int RunTranslate(string text, string outFile)
        {
            StringBuilder sb = new StringBuilder();
            try
            {
                Translator tr = new Translator();
                TranslateResult r = tr.TranslateAsync(text, Translator.IsChinese(text) ? "en" : "zh-CN").GetAwaiter().GetResult();
                sb.AppendLine("ok=" + r.Ok);
                sb.AppendLine("provider=" + r.Provider);
                sb.AppendLine("ms=" + r.Ms);
                sb.AppendLine("text=" + r.Text);
                if (!r.Ok) sb.AppendLine("error=" + r.Error);
            }
            catch (Exception ex)
            {
                sb.AppendLine("ok=False");
                sb.AppendLine("error=" + ex.Message);
            }
            try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(false)); } catch { }
            return 0;
        }

        private static void RenderSample(string path)
        {
            using (Bitmap bmp = new Bitmap(720, 130))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using (Font f = new Font("Segoe UI", 19f))
                using (SolidBrush b = new SolidBrush(Color.Black))
                {
                    g.DrawString("Selection should feel instant.", f, b, 14, 18);
                    g.DrawString("Hover, click, read.", f, b, 14, 66);
                }
                bmp.Save(path, ImageFormat.Png);
            }
        }

        private static int CountWords(OcrResult r)
        {
            int n = 0;
            foreach (OcrLine l in r.Lines) n += l.Words.Count;
            return n;
        }
    }
}
