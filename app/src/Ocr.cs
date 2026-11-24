using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MouseTranslate
{
    internal sealed class OcrWord
    {
        public string Text;
        public int X, Y, W, H;
    }

    internal sealed class OcrLine
    {
        public string Text;
        public int X, Y, W, H;
        public List<OcrWord> Words = new List<OcrWord>();
    }

    internal sealed class OcrResult
    {
        public bool Ok;
        public string Text = string.Empty;
        public string Error = string.Empty;
        public List<OcrLine> Lines = new List<OcrLine>();
        public int OriginX;
        public int OriginY;

        /// <summary>把屏幕坐标换算成 OCR 图内坐标后，找命中的单词。</summary>
        public OcrWord WordAtScreenPoint(int screenX, int screenY)
        {
            int rx = screenX - OriginX;
            int ry = screenY - OriginY;

            // ① 精确命中（带一点容差）
            foreach (OcrLine line in Lines)
            {
                foreach (OcrWord w in line.Words)
                {
                    if (rx >= w.X - 5 && rx <= w.X + w.W + 5 && ry >= w.Y - 6 && ry <= w.Y + w.H + 6)
                        return w;
                }
            }

            // ② 光标在同一条文字带的竖直范围内：取横向最近的词
            //    （密集排版里行距很小，这一条能显著提高命中率）
            foreach (OcrLine line in Lines)
            {
                if (ry < line.Y - 7 || ry > line.Y + line.H + 7) continue;
                OcrWord best = null;
                int bestDx = int.MaxValue;
                foreach (OcrWord w in line.Words)
                {
                    int dx = rx < w.X ? w.X - rx : (rx > w.X + w.W ? rx - (w.X + w.W) : 0);
                    if (dx < bestDx) { bestDx = dx; best = w; }
                }
                if (best != null && bestDx <= 40) return best;
            }

            // ③ 兜底：最近的词，但必须够近
            OcrWord nearest = null;
            double nearestD = double.MaxValue;
            foreach (OcrLine line in Lines)
            {
                foreach (OcrWord w in line.Words)
                {
                    double dx = Math.Max(0, Math.Max(w.X - rx, rx - (w.X + w.W)));
                    double dy = Math.Max(0, Math.Max(w.Y - ry, ry - (w.Y + w.H)));
                    double d = dx * dx + dy * dy;
                    if (d < nearestD) { nearestD = d; nearest = w; }
                }
            }
            return nearestD <= 900 ? nearest : null;   // 30px 以内
        }

        /// <summary>找到包含指定单词的那一整行。</summary>
        public OcrLine LineOf(OcrWord word)
        {
            if (word == null) return null;
            foreach (OcrLine line in Lines)
            {
                foreach (OcrWord w in line.Words)
                {
                    if (ReferenceEquals(w, word)) return line;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// 与常驻的 PowerShell OCR 子进程通信。协议：
    ///   发  OCR &lt;base64(图片路径)&gt;
    ///   收  RES &lt;base64(json)&gt;
    /// </summary>
    internal sealed class OcrHost : IDisposable
    {
        private readonly object _lock = new object();
        private Process _proc;
        private bool _disposed;

        public bool Ready { get; private set; }
        public string LastError { get; private set; }

        private static string ScriptPath()
        {
            string dir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            return Path.Combine(dir, "scripts", "ocr-host.ps1");
        }

        public bool Start()
        {
            lock (_lock)
            {
                if (Ready && _proc != null && !_proc.HasExited) return true;

                try
                {
                    string script = ScriptPath();
                    if (!File.Exists(script))
                    {
                        LastError = "缺少 scripts\\ocr-host.ps1";
                        return false;
                    }

                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
                    if (!File.Exists(psi.FileName)) psi.FileName = "powershell.exe";
                    psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -STA -NoLogo -File \"" + script + "\"";
                    psi.EnvironmentVariables["MT_PARENT_PID"] = Process.GetCurrentProcess().Id.ToString();
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    psi.RedirectStandardInput = true;
                    psi.RedirectStandardOutput = true;
                    psi.RedirectStandardError = true;
                    psi.StandardOutputEncoding = Encoding.UTF8;
                    psi.StandardErrorEncoding = Encoding.UTF8;
                    psi.WorkingDirectory = Path.GetDirectoryName(script);

                    _proc = Process.Start(psi);
                    if (_proc == null) { LastError = "无法启动 OCR 子进程"; return false; }

                    Task<string> first = Task.Factory.StartNew(delegate { return _proc.StandardOutput.ReadLine(); });
                    if (!first.Wait(12000))
                    {
                        LastError = "OCR 引擎初始化超时";
                        Kill();
                        return false;
                    }
                    string hello = (first.Result ?? string.Empty).Trim();
                    if (hello != "READY 1")
                    {
                        LastError = "OCR 引擎不可用（系统未安装对应语言包）";
                        Kill();
                        return false;
                    }
                    Ready = true;
                    LastError = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Kill();
                    return false;
                }
            }
        }

        public OcrResult RecognizeFile(string path)
        {
            lock (_lock)
            {
                if (!Ready && !Start()) return Fail(LastError);
                try
                {
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
                    _proc.StandardInput.WriteLine("OCR " + b64);
                    _proc.StandardInput.Flush();

                    Task<string> read = Task.Factory.StartNew(delegate { return _proc.StandardOutput.ReadLine(); });
                    if (!read.Wait(6000))
                    {
                        Kill();
                        return Fail("OCR 响应超时");
                    }
                    string line = read.Result;
                    if (string.IsNullOrEmpty(line)) { Kill(); return Fail("OCR 子进程已退出"); }
                    if (!line.StartsWith("RES ")) return Fail("OCR 返回格式异常");

                    string json = Encoding.UTF8.GetString(Convert.FromBase64String(line.Substring(4)));
                    return Parse(json);
                }
                catch (Exception ex)
                {
                    Kill();
                    return Fail(ex.Message);
                }
            }
        }

        /// <summary>截屏一块区域并识别；返回结果里带 OriginX/Y 方便换算回屏幕坐标。</summary>
        public OcrResult CaptureAndRecognize(int x, int y, int w, int h)
        {
            string tmp = null;
            try
            {
                Bitmap bmp = Native.Capture(x, y, w, h);
                if (bmp == null) return Fail("截屏失败");

                string dir = Path.Combine(Path.GetTempPath(), "MouseTranslate");
                Directory.CreateDirectory(dir);
                tmp = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".png");
                bmp.Save(tmp, ImageFormat.Png);
                bmp.Dispose();

                OcrResult r = RecognizeFile(tmp);
                r.OriginX = x;
                r.OriginY = y;
                return r;
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                if (tmp != null) { try { File.Delete(tmp); } catch { } }
            }
        }

        private static OcrResult Fail(string msg)
        {
            OcrResult r = new OcrResult();
            r.Ok = false;
            r.Error = msg ?? "unknown";
            return r;
        }

        private static OcrResult Parse(string json)
        {
            OcrResult r = new OcrResult();
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;
                Dictionary<string, object> root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) { r.Ok = false; r.Error = "bad json"; return r; }

                object okVal;
                root.TryGetValue("ok", out okVal);
                r.Ok = okVal is bool && (bool)okVal;
                if (!r.Ok)
                {
                    object err;
                    if (root.TryGetValue("error", out err)) r.Error = Convert.ToString(err);
                    return r;
                }

                object text;
                if (root.TryGetValue("text", out text)) r.Text = Convert.ToString(text);

                object linesObj;
                if (root.TryGetValue("lines", out linesObj))
                {
                    object[] arr = linesObj as object[];
                    if (arr != null)
                    {
                        foreach (object lo in arr)
                        {
                            Dictionary<string, object> ld = lo as Dictionary<string, object>;
                            if (ld == null) continue;
                            OcrLine line = new OcrLine();
                            line.Text = Str(ld, "t");
                            line.X = Int(ld, "x");
                            line.Y = Int(ld, "y");
                            line.W = Int(ld, "w");
                            line.H = Int(ld, "h");

                            object wordsObj;
                            if (ld.TryGetValue("words", out wordsObj))
                            {
                                object[] wArr = wordsObj as object[];
                                if (wArr != null)
                                {
                                    foreach (object wo in wArr)
                                    {
                                        Dictionary<string, object> wd = wo as Dictionary<string, object>;
                                        if (wd == null) continue;
                                        OcrWord w = new OcrWord();
                                        w.Text = Str(wd, "t");
                                        w.X = Int(wd, "x");
                                        w.Y = Int(wd, "y");
                                        w.W = Int(wd, "w");
                                        w.H = Int(wd, "h");
                                        line.Words.Add(w);
                                    }
                                }
                            }
                            r.Lines.Add(line);
                        }
                    }
                }
                return r;
            }
            catch (Exception ex)
            {
                r.Ok = false;
                r.Error = "parse: " + ex.Message;
                return r;
            }
        }

        private static string Str(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v);
            return string.Empty;
        }

        private static int Int(Dictionary<string, object> d, string k)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return 0;
        }

        private void Kill()
        {
            Ready = false;
            try
            {
                if (_proc != null && !_proc.HasExited) _proc.Kill();
            }
            catch { }
            try { _proc = null; } catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (_lock)
            {
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); } catch { }
            }
        }
    }
}
