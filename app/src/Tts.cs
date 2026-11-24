using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MouseTranslate
{
    /// <summary>
    /// 朗读引擎（Windows 桌面程序，WinForms + WebView2；不是浏览器环境）。
    ///
    /// 关键设计：
    /// 1) 所有 SAPI / 播放工作都在**专用 STA 线程**上跑 —— SAPI 的 COM 对象在 MTA 线程会静默失败；
    /// 2) 会话号 _session：Speak 与 Stop 都用 Interlocked 递增；所有异步回调（下载 / 播放 / SAPI 轮询 /
    ///    预取）执行前先校验会话，过期立即放弃并清理，杜绝"停了还在响"；
    /// 3) 停止是毫秒级的：先 stop 再 close 占用的 MCI alias（close 失败重试一次），再取消 SAPI，
    ///    再清掉待播临时文件，最后立刻抛 StateChanged("idle")；
    /// 4) 文本先清洗，再按**句子**切分（中英文句末标点 + 缩写/小数保护），只有单句超过 180 字符才在
    ///    逗号/分号处二次拆，绝不从单词中间硬切；整段上限改为按完整句子累计到 ~2000 字符；
    /// 5) 在线 Google 神经语音优先，失败后熔断 60 秒走系统 SAPI；SAPI 音色按"自然度"打分优选。
    /// </summary>
    internal static class Tts
    {
        // ---------------- 常量 ----------------

        private const int SegmentMax = 180;        // 单段上限：只有单句超过它才允许二次拆分
        private const int TotalMax = 2000;         // 整段上限：按完整句子累计，不切半句
        private const int MaxSegments = 240;       // 安全阀：极端碎片化文本（每句 2~8 字）也不会打爆请求数，正常文本先撞 TotalMax
        private const int HttpTimeout = 2500;
        private const int BlockSeconds = 60;       // 在线失败后的熔断时长（原来是 3 分钟）
        private const int MciPollMs = 60;          // PlayMci 轮询步长
        private const int MciCloseRetry = 1;       // close 失败重试次数
        private const int MciErrInvalidDeviceName = 263;   // 跨线程操作 alias 时 winmm 返回的错误码
        private const long CacheLimitBytes = 20L * 1024 * 1024;   // 磁盘缓存上限 20MB
        private const long CacheTrimBytes = 14L * 1024 * 1024;    // 超限后清到这个大小
        private const int CacheMaxTextChars = 200;                // 只缓存短内容（单词/短语/短句）

        private const string Alias = "mtts";

        // ---------------- 会话 / 状态 ----------------

        private static readonly object _lock = new object();       // 预取槽、状态去重用
        private static readonly object _mciGate = new object();    // MCI alias 操作串行化
        private static readonly object _logLock = new object();    // 日志串行化

        private static int _session;                 // Interlocked 递增的会话号
        private static int _prefetchSeq;             // 预取代次（后发预取覆盖先发）
        private static volatile int _synthCancel;    // 1 = 请求取消当前 SAPI
        private static volatile SpeechSynthesizer _synth;   // 当前 SAPI 实例（供 Stop 取消）
        private static bool _speakingNotified;       // SpeakingChanged 兼容用的上次值
        private static long _blockedUntilTicks;      // 在线熔断截止（UTC ticks；0 = 未熔断）

        // MCI 的 alias 是**线程私有**的：只有打开它的线程能 stop/close（别的线程一律 rc=263）。
        // 所以 alias 归属用线程静态标记跟踪，Stop 时再用一个自动事件把播放线程立刻叫醒去关它。
        [ThreadStatic]
        private static bool _mciOpenHere;
        private static volatile int _mciOwnerTid;    // 当前持有 alias 的线程 id（0 = 没人持有，供诊断）
        private static readonly AutoResetEvent _mciWake = new AutoResetEvent(false);

        private static string _prefetchFile;
        private static string _prefetchKey;

        private static volatile bool _netReady;

        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MouseTranslate", "tts-cache");

        private static readonly string WorkDir = Path.Combine(Path.GetTempPath(), "MouseTranslateTts");

        public static volatile string LastStatus = "idle";
        public static volatile string LastState = "idle";
        public static volatile int FirstAudioMs = -1;
        public static volatile bool LastTruncated;

        /// <summary>
        /// 朗读状态变化："loading"（开始 / 等首段音频）、"playing"（首段开始出声）、
        /// "idle"（结束 / 停止 / 失败兜底）。可能在任意后台线程触发，UI 侧必须切回 UI 线程。
        /// </summary>
        public static event Action<string> StateChanged;

        /// <summary>旧接口兼容（true = 正在出声）。CardWindow 已改用 StateChanged。</summary>
        public static event Action<bool> SpeakingChanged;

        private static readonly string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        private static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr hwnd);

        // ---------------- 日志 ----------------

        private static string LogPath
        {
            get { return Path.Combine(Path.GetTempPath(), "MouseTranslate-tts.log"); }
        }

        private static void Log(string msg)
        {
            LastStatus = msg;
            try
            {
                lock (_logLock)
                {
                    FileInfo fi = new FileInfo(LogPath);
                    if (fi.Exists && fi.Length > 200000) fi.Delete();
                    File.AppendAllText(LogPath,
                        DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        private static string Short(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length > 60 ? s.Substring(0, 60) : s;
        }

        private static string Head(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max);
        }

        // ---------------- 会话与状态 ----------------

        private static int CurrentSession()
        {
            return Interlocked.CompareExchange(ref _session, 0, 0);
        }

        private static bool Alive(int session)
        {
            return session == Interlocked.CompareExchange(ref _session, 0, 0);
        }

        private static void SetState(string state)
        {
            LastState = state;
            Log("state " + state);
            try
            {
                Action<string> handler = StateChanged;
                if (handler != null) handler(state);
            }
            catch { }

            bool speaking = state == "playing";
            bool changed = false;
            lock (_lock)
            {
                if (_speakingNotified != speaking) { _speakingNotified = speaking; changed = true; }
            }
            if (changed)
            {
                try
                {
                    Action<bool> handler = SpeakingChanged;
                    if (handler != null) handler(speaking);
                }
                catch { }
            }
        }

        // ---------------- 对外 ----------------

        public static void Speak(string text, bool chinese)
        {
            Speak(text);
        }

        /// <summary>开始朗读。会先让上一个会话闭嘴，再开新会话。</summary>
        public static void Speak(string text)
        {
            if (string.IsNullOrEmpty(text)) { SetState("idle"); return; }

            string clean = Clean(text);
            if (clean.Length == 0)
            {
                Log("speak ignored: text empty after clean");
                SetState("idle");
                return;
            }

            bool truncated;
            List<string> segments = BuildSegments(clean, out truncated);
            if (segments.Count == 0) { SetState("idle"); return; }
            bool zh = Translator.IsChinese(clean);

            if (Environment.GetEnvironmentVariable("MT_TTS_LOCAL") == "1")
                Interlocked.Exchange(ref _blockedUntilTicks, DateTime.MaxValue.Ticks);   // 强制系统音色（测试/离线）

            int session = Interlocked.Increment(ref _session);
            _mciWake.Set();   // 让还在播的上一个会话的播放线程立刻醒来（不用等 60ms 轮询）

            // 上一个会话可能还在播：先 stop + close，再取消 SAPI，避免两段声音叠在一起
            StopAndCloseAlias("speak");
            RequestSynthCancel();

            FirstAudioMs = -1;
            LastTruncated = truncated;
            TrimCache();

            Log("speak start session=" + session + " segments=" + segments.Count
                + " chars=" + clean.Length + " zh=" + zh + (truncated ? " truncated" : ""));
            for (int i = 0; i < segments.Count; i++)
            {
                Log("seg " + (i + 1) + "/" + segments.Count + " len=" + segments[i].Length
                    + " \"" + Head(segments[i], 30) + "\"");
            }
            if (truncated)
                Log("text truncated: whole sentences up to ~" + TotalMax + " chars (max " + MaxSegments + " segments)");

            SetState("loading");

            Thread th = new Thread(delegate () { Run(session, segments, zh); });
            th.IsBackground = true;
            try { th.SetApartmentState(ApartmentState.STA); } catch { }
            th.Start();
        }

        /// <summary>
        /// 毫秒级停止：会话号递增 → 唤醒播放线程（它持有 MCI alias 并会立刻 stop+close）→
        /// 本线程也 stop+close 试一次（跨线程会失败，由播放线程兜底）→ 取消 SAPI → 清待播文件 → 立刻 idle。
        /// </summary>
        public static void Stop()
        {
            int session = Interlocked.Increment(ref _session);
            _mciWake.Set();   // 关键：播放线程在 60ms 轮询里 WaitOne，收到信号立刻校验会话并关掉 alias
            Stopwatch sw = Stopwatch.StartNew();

            bool closed = StopAndCloseAlias("stop");
            RequestSynthCancel();
            int files = ClearPendingFiles();
            SetState("idle");

            Log("stop session=" + session + " mciClosed=" + closed
                + (closed ? "" : " (alias owned by playback thread -> closed in poll)")
                + " pendingFiles=" + files + " elapsed=" + sw.ElapsedMilliseconds + "ms");
        }

        /// <summary>诊断用：是否还有线程持有 MCI alias（open 状态）。</summary>
        internal static bool MciActive
        {
            get { return _mciOwnerTid != 0 || _mciOpenHere; }
        }

        /// <summary>诊断用：当前会话号。</summary>
        internal static int Session
        {
            get { return CurrentSession(); }
        }

        // ---------------- 主流程（STA 线程） ----------------

        private static void Run(int session, List<string> segments, bool zh)
        {
            try { Directory.CreateDirectory(WorkDir); } catch { }

            Stopwatch sw = Stopwatch.StartNew();
            int played = 0;
            bool usedSapi = false;

            try
            {
                string first = Fetch(session, segments[0], 0);
                if (!Alive(session)) return;

                if (first == null)
                {
                    usedSapi = true;
                    Log("online unavailable -> sapi (" + segments.Count + " segs)");
                    SpeakLocal(session, JoinSegments(segments, 0), zh, (int)sw.ElapsedMilliseconds);
                    return;
                }

                Task<string> pending = null;
                if (segments.Count > 1)
                {
                    int idx = 1;
                    pending = Task.Factory.StartNew<string>(delegate () { return Fetch(session, segments[idx], idx); });
                }

                string file = first;
                for (int i = 0; i < segments.Count; i++)
                {
                    if (!Alive(session)) return;

                    if (file == null)
                    {
                        // 当前段下载失败：从 i 开始的剩余文本按顺序交给 SAPI，不重排、不跳读
                        string rest = JoinSegments(segments, i);
                        usedSapi = true;
                        Log("online fail at " + (i + 1) + "/" + segments.Count + " -> sapi rest chars=" + rest.Length);
                        SpeakLocal(session, rest, zh, (int)sw.ElapsedMilliseconds);
                        return;
                    }

                    if (FirstAudioMs < 0) FirstAudioMs = (int)sw.ElapsedMilliseconds;
                    Log("playing " + (i + 1) + "/" + segments.Count + " firstAudio=" + FirstAudioMs + "ms");
                    SetState("playing");
                    PlayMci(file, session);
                    TryDelete(file);
                    played++;
                    if (!Alive(session)) return;

                    if (pending == null) break;

                    file = null;
                    try { file = pending.Result; } catch { file = null; }
                    pending = null;

                    int next = i + 2;
                    if (next < segments.Count && Alive(session))
                    {
                        int ni = next;
                        pending = Task.Factory.StartNew<string>(delegate () { return Fetch(session, segments[ni], ni); });
                    }
                }

                if (!Alive(session)) return;
                Log("done " + played + "/" + segments.Count + (LastTruncated ? " (truncated)" : ""));
            }
            catch (Exception ex)
            {
                Log("error: " + ex.Message);
                if (Alive(session))
                {
                    usedSapi = true;
                    try { SpeakLocal(session, JoinSegments(segments, 0), zh, (int)sw.ElapsedMilliseconds); }
                    catch { }
                }
            }
            finally
            {
                if (Alive(session))
                {
                    SetState("idle");
                    Log("run end session=" + session + " played=" + played + "/" + segments.Count
                        + " sapi=" + usedSapi);
                }
                CleanupSessionFiles(session);
            }
        }

        private static string JoinSegments(List<string> segments, int from)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = from; i < segments.Count; i++)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(segments[i]);
            }
            return sb.ToString();
        }

        // ---------------- 在线语音 ----------------

        private static string Fetch(int session, string text, int index)
        {
            if (!Alive(session)) return null;

            bool cacheable = text.Length <= CacheMaxTextChars;
            string key = KeyOf(text);

            // 1) 持久缓存命中（只对短内容）
            if (cacheable)
            {
                string cached = Path.Combine(CacheDir, key + ".mp3");
                try
                {
                    if (File.Exists(cached) && new FileInfo(cached).Length > 2000)
                    {
                        try { File.SetLastAccessTimeUtc(cached, DateTime.UtcNow); } catch { }
                        Log("cache hit " + index);
                        return cached;
                    }
                }
                catch { }
            }

            // 2) 预取槽命中（卡片出现时已下好）
            string slot = Path.Combine(WorkDir, SlotName(session, index));
            try
            {
                bool hit = false;
                lock (_lock)
                {
                    if (_prefetchFile != null && _prefetchKey == key && File.Exists(_prefetchFile))
                    {
                        if (!Alive(session)) return null;
                        try
                        {
                            File.Copy(_prefetchFile, slot, true);
                            TryDelete(_prefetchFile);
                            _prefetchFile = null;
                            _prefetchKey = null;
                            hit = true;
                        }
                        catch { }
                    }
                }
                if (hit)
                {
                    Log("prefetch hit " + index);
                    return slot;
                }
            }
            catch { }

            // 熔断只拦"联网下载"：本地已有持久缓存或预取音频时，即使熔断中也照常用，
            // 避免网络抖一次就整整 60 秒退化成机械 SAPI 音。
            if (OnlineBlocked()) return null;
            EnsureNet();

            // 3) 联网下载
            if (!Download(text, slot, session)) return null;
            if (!Alive(session)) { TryDelete(slot); return null; }

            // 只把短内容写进持久缓存，并立刻做有界清理
            if (cacheable)
            {
                try
                {
                    Directory.CreateDirectory(CacheDir);
                    File.Copy(slot, Path.Combine(CacheDir, key + ".mp3"), true);
                    TrimCache();
                }
                catch { }
            }
            return slot;
        }

        private static string SlotName(int session, int index)
        {
            return "mt_" + session + "_" + index + ".mp3";
        }

        private static bool Download(string text, string outFile, int session)
        {
            EnsureNet();
            string lang = Translator.IsChinese(text) ? "zh-CN" : "en";
            string q = "&q=" + Uri.EscapeDataString(text);

            string[] hosts = new string[]
            {
                "https://translate.google.com/translate_tts?ie=UTF-8&client=tw-ob&tl=",
                "https://translate.googleapis.com/translate_tts?ie=UTF-8&client=gtx&tl="
            };

            for (int h = 0; h < hosts.Length; h++)
            {
                if (!Alive(session))
                {
                    TryDelete(outFile);
                    Log("download abort: session changed");
                    return false;
                }

                bool aborted = false;
                try
                {
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(hosts[h] + lang + q);
                    req.UserAgent = UserAgent;
                    req.Referer = "https://translate.google.com/";
                    req.Timeout = HttpTimeout;
                    req.ReadWriteTimeout = HttpTimeout;
                    req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;

                    using (WebResponse resp = req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream fs = File.Create(outFile))
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            fs.Write(buf, 0, n);
                            if (!Alive(session)) { aborted = true; break; }
                        }
                    }

                    if (aborted)
                    {
                        TryDelete(outFile);
                        Log("download abort midstream: session changed");
                        return false;
                    }
                    if (new FileInfo(outFile).Length > 2000) return true;
                }
                catch (Exception ex)
                {
                    Log("download fail " + hosts[h].Substring(8, 12) + "..: " + Short(ex.Message));
                }
                try { if (File.Exists(outFile)) File.Delete(outFile); } catch { }
            }

            if (Alive(session))
            {
                Interlocked.Exchange(ref _blockedUntilTicks, DateTime.UtcNow.AddSeconds(BlockSeconds).Ticks);
                Log("online blocked for " + BlockSeconds + "s");
            }
            return false;
        }

        private static bool OnlineBlocked()
        {
            long ticks = Interlocked.Read(ref _blockedUntilTicks);
            return ticks != 0 && DateTime.UtcNow.Ticks < ticks;
        }

        private static void EnsureNet()
        {
            if (_netReady) return;
            _netReady = true;
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 12;
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
            try { Directory.CreateDirectory(CacheDir); } catch { }
        }

        // ---------------- 预取 / 缓存 ----------------

        /// <summary>预取：卡片一出现就先把第一段音频下载到缓存，点朗读时几乎零等待。</summary>
        public static void Prefetch(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string clean = Clean(text);
            if (clean.Length == 0) return;

            bool truncated;
            List<string> segments = BuildSegments(clean, out truncated);
            if (segments.Count == 0) return;

            string first = segments[0];
            string key = KeyOf(first);
            int session = CurrentSession();
            int seq = Interlocked.Increment(ref _prefetchSeq);

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    TrimPrefetchFiles(null);
                    if (!Alive(session))
                    {
                        Log("prefetch discard: session changed before start");
                        return;
                    }
                    if (OnlineBlocked()) return;

                    try { Directory.CreateDirectory(WorkDir); } catch { }
                    string slot = Path.Combine(WorkDir, "prefetch_" + seq + ".mp3");

                    if (!Download(first, slot, session) || !Alive(session))
                    {
                        TryDelete(slot);
                        Log("prefetch discard: session changed or download failed");
                        return;
                    }
                    if (seq != Interlocked.CompareExchange(ref _prefetchSeq, 0, 0))
                    {
                        TryDelete(slot);   // 已经有更新的预取，这次结果作废
                        return;
                    }

                    lock (_lock)
                    {
                        if (_prefetchFile != null) TryDelete(_prefetchFile);
                        _prefetchFile = slot;
                        _prefetchKey = key;
                    }
                    Log("prefetch ready " + (new FileInfo(slot).Length / 1024) + " KB");
                }
                catch { }
            });
        }

        private static void TrimPrefetchFiles(string keep)
        {
            try
            {
                if (!Directory.Exists(WorkDir)) return;
                DateTime expire = DateTime.UtcNow.AddHours(-1);
                string[] files = Directory.GetFiles(WorkDir, "prefetch_*");
                for (int i = 0; i < files.Length; i++)
                {
                    if (keep != null && string.Equals(files[i], keep, StringComparison.OrdinalIgnoreCase)) continue;
                    try { if (new FileInfo(files[i]).LastWriteTimeUtc < expire) File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        /// <summary>当前磁盘缓存大小（字节）。</summary>
        public static long CacheSizeBytes()
        {
            try
            {
                long total = 0;
                DirectoryInfo di = new DirectoryInfo(CacheDir);
                if (di.Exists) foreach (FileInfo f in di.GetFiles("*.mp3")) total += f.Length;
                return total;
            }
            catch { return 0; }
        }

        /// <summary>清空磁盘缓存，返回释放的字节数。</summary>
        public static long ClearCache()
        {
            long before = CacheSizeBytes();
            try
            {
                DirectoryInfo di = new DirectoryInfo(CacheDir);
                if (di.Exists) foreach (FileInfo f in di.GetFiles()) { try { f.Delete(); } catch { } }
            }
            catch { }
            return before;
        }

        private static string KeyOf(string text)
        {
            try
            {
                using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
                {
                    byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes((Translator.IsChinese(text) ? "zh|" : "en|") + text));
                    StringBuilder sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return text; }
        }

        private static void TrimCache()
        {
            try
            {
                DirectoryInfo di = new DirectoryInfo(CacheDir);
                if (!di.Exists) return;
                FileInfo[] files = di.GetFiles("*.mp3");
                long total = 0;

                // 先删掉 7 天没用过的（自动过期，不长期占用磁盘）
                DateTime expire = DateTime.UtcNow.AddDays(-7);
                foreach (FileInfo f in files)
                {
                    try
                    {
                        if (f.LastAccessTimeUtc < expire) { f.Delete(); continue; }
                    }
                    catch { }
                    total += f.Length;
                }

                files = di.GetFiles("*.mp3");
                if (total < CacheLimitBytes) return;

                Array.Sort(files, delegate (FileInfo a, FileInfo b) { return a.LastAccessTimeUtc.CompareTo(b.LastAccessTimeUtc); });
                foreach (FileInfo f in files)
                {
                    if (total < CacheTrimBytes) break;
                    try { total -= f.Length; f.Delete(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>停止时清掉当前待播的临时音频（mt_*）。</summary>
        private static int ClearPendingFiles()
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(WorkDir)) return 0;
                string[] files = Directory.GetFiles(WorkDir, "mt_*");
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); n++; } catch { }
                }
            }
            catch { }
            return n;
        }

        /// <summary>只清本会话的临时音频，避免误删新会话正在下载的文件。</summary>
        private static void CleanupSessionFiles(int session)
        {
            try
            {
                if (!Directory.Exists(WorkDir)) return;
                string[] files = Directory.GetFiles(WorkDir, "mt_" + session + "_*");
                for (int i = 0; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
            }
            catch { }
        }

        private static void TryDelete(string f)
        {
            try
            {
                if (f == null || !File.Exists(f)) return;
                if (f.StartsWith(CacheDir, StringComparison.OrdinalIgnoreCase)) return;   // 缓存留着复用
                File.Delete(f);
            }
            catch { }
        }

        // ---------------- 系统音色（离线兜底） ----------------

        private static string[] _bonus100 = new string[] { "natural", "neural", "online" };
        private static string[] _bonus90 = new string[] { "aria", "jenny", "guy", "xiaoxiao", "yunxi", "yunyang" };
        private static string[] _bonus80 = new string[] { "premium", "enhanced" };
        private static string[] _bonus70 = new string[] { "google" };
        private static string[] _bonus60 = new string[] { "siri", "samantha", "daniel", "tingting" };
        private static string[] _bonus40 = new string[] { "zira", "david", "huihui", "kangkang", "yaoyao" };

        /// <summary>SAPI 音色打分：名字里带自然/神经/知名音色关键词的加分；"Desktop"/"legacy" 不加分。</summary>
        private static int VoiceScore(VoiceInfo vi)
        {
            if (vi == null) return -1000;
            string n = (vi.Name != null ? vi.Name : string.Empty).ToLowerInvariant();
            int score = 0;
            if (ContainsAny(n, _bonus100)) score += 100;
            if (ContainsAny(n, _bonus90)) score += 90;
            if (ContainsAny(n, _bonus80)) score += 80;
            if (ContainsAny(n, _bonus70)) score += 70;
            if (ContainsAny(n, _bonus60)) score += 60;
            if (ContainsAny(n, _bonus40)) score += 40;
            return score;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            for (int i = 0; i < needles.Length; i++)
            {
                if (haystack.IndexOf(needles[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        /// <summary>选音色：语言必须匹配（中文 zh-*、英文 en-*），同类里取自然度得分最高者。</summary>
        private static VoiceInfo PickVoice(SpeechSynthesizer synth, bool zh)
        {
            string want = zh ? "zh" : "en";
            VoiceInfo best = null;
            int bestScore = int.MinValue;
            InstalledVoice[] voices = new InstalledVoice[synth.GetInstalledVoices().Count];
            synth.GetInstalledVoices().CopyTo(voices, 0);

            for (int i = 0; i < voices.Length; i++)
            {
                InstalledVoice v = voices[i];
                VoiceInfo vi = v.VoiceInfo;
                if (vi == null) continue;
                string culture = vi.Culture != null ? vi.Culture.Name : string.Empty;
                if (!culture.StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;

                int score = VoiceScore(vi);
                Log("sapi candidate \"" + vi.Name + "\" culture=" + culture
                    + " score=" + score + (v.Enabled ? "" : " (disabled)"));
                if (!v.Enabled) continue;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = vi;
                }
            }
            return best;
        }

        /// <summary>离线兜底：SAPI。失败只记日志，状态由调用方兜底回 idle。</summary>
        private static void SpeakLocal(int session, string text, bool zh, int startedMs)
        {
            if (!Alive(session)) return;
            if (string.IsNullOrEmpty(text)) return;
            _synthCancel = 0;

            SpeechSynthesizer synth = null;
            ManualResetEvent finished = null;
            try
            {
                synth = new SpeechSynthesizer();
                synth.Rate = 0;   // 正常语速（0 = 系统默认；-10~10），不擅自变速
                VoiceInfo picked = PickVoice(synth, zh);
                if (picked == null)
                {
                    // 别静默：明确提示缺语音包，方便用户去系统里装
                    Log("sapi: 缺语音包 —— 本机没有 " + (zh ? "zh-*（中文）" : "en-*（英文）")
                        + " 语音，已放弃离线朗读；可在 Windows 设置 → 时间和语言 → 语音 安装对应语音包");
                    return;
                }
                try { synth.SelectVoice(picked.Name); } catch { }
                Log("sapi voice=\"" + picked.Name + "\" culture="
                    + (picked.Culture != null ? picked.Culture.Name : "?")
                    + " score=" + VoiceScore(picked) + " zh=" + zh + " chars=" + text.Length);
                if (!Alive(session)) return;

                _synth = synth;
                finished = new ManualResetEvent(false);
                synth.SpeakCompleted += delegate { try { finished.Set(); } catch { } };
                synth.SpeakAsync(text);
                if (!Alive(session)) return;

                if (FirstAudioMs < 0) FirstAudioMs = startedMs;
                SetState("playing");
                Log("sapi start after " + startedMs + "ms");

                int waited = 0;
                bool stoppedEarly = false;
                while (!finished.WaitOne(20))
                {
                    if (!Alive(session) || _synthCancel != 0)
                    {
                        stoppedEarly = true;
                        try { synth.SpeakAsyncCancelAll(); } catch { }
                        Log("sapi cancelled session=" + session + " waited=" + waited + "ms");
                        break;
                    }
                    waited += 20;
                    if (waited > 180000)
                    {
                        stoppedEarly = true;
                        try { synth.SpeakAsyncCancelAll(); } catch { }
                        Log("sapi timeout");
                        break;
                    }
                }

                // Stop 时 RequestSynthCancel 会先在后台线程取消，SpeakCompleted 可能先于本循环的会话校验触发：
                // 这种情况同样算"被取消"，补一条日志，别让日志看起来像正常读完。
                if (!stoppedEarly && (!Alive(session) || _synthCancel != 0))
                {
                    stoppedEarly = true;
                    Log("sapi cancelled (async) session=" + session + " waited=" + waited + "ms");
                }
                Log("sapi done waited=" + waited + "ms cancelled=" + stoppedEarly);
            }
            catch (Exception ex)
            {
                Log("sapi error: " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(_synth, synth)) _synth = null;
                if (finished != null) { try { finished.Dispose(); } catch { } }
                try { if (synth != null) synth.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// 取消当前 SAPI。在后台线程上调用：SAPI 的 STA 线程没有消息泵，
        /// 直接跨套间调用可能把调用方（UI 线程）卡住，所以只发取消请求 + 兜底异步调用，
        /// SAPI 自己的轮询循环会在 20ms 内看到会话号变化并自行 SpeakAsyncCancelAll。
        /// </summary>
        private static void RequestSynthCancel()
        {
            _synthCancel = 1;
            SpeechSynthesizer synth = _synth;
            if (synth == null) return;
            Thread th = new Thread(delegate ()
            {
                try { synth.SpeakAsyncCancelAll(); } catch { }
            });
            th.IsBackground = true;
            th.Start();
        }

        // ---------------- 播放 ----------------

        private static void PlayMci(string file, int session)
        {
            if (!Alive(session)) return;

            int len = 0;
            lock (_mciGate)
            {
                if (!Alive(session)) return;
                CloseAliasLocked("play-open");

                int rc = mciSendString("open \"" + file + "\" type mpegvideo alias " + Alias, null, 0, IntPtr.Zero);
                if (rc != 0) { Log("mci open fail " + rc); return; }
                _mciOpenHere = true;
                _mciOwnerTid = Thread.CurrentThread.ManagedThreadId;

                // open 之后、play 之前再校验一次：Stop 可能刚好发生在 open 和 play 之间，避免"停完还响一下"
                if (!Alive(session))
                {
                    CloseAliasLocked("play-stale");
                    return;
                }

                StringBuilder sb = new StringBuilder(256);
                if (mciSendString("status " + Alias + " length", sb, sb.Capacity, IntPtr.Zero) == 0)
                    int.TryParse(sb.ToString(), out len);
                if (len <= 0) len = 4000;

                rc = mciSendString("play " + Alias, null, 0, IntPtr.Zero);   // 非阻塞 play
                if (rc != 0)
                {
                    Log("mci play fail " + rc);
                    CloseAliasLocked("play-fail");
                    return;
                }
            }

            // 非阻塞播放 + 60ms 轮询：每帧校验会话；Stop/Speak 会 Set(_mciWake) 把这里立刻叫醒，
            // 所以停止延迟通常只有几毫秒，最坏也就是一个 60ms 轮询周期。
            int waited = 0;
            bool interrupted = false;
            while (waited < len + 400)
            {
                if (!Alive(session)) { interrupted = true; break; }
                bool signaled = _mciWake.WaitOne(MciPollMs);
                if (!signaled) waited += MciPollMs;
            }
            if (!Alive(session)) interrupted = true;
            if (interrupted)
                Log("play interrupted: session=" + session + " waited=" + waited + "ms (superseded by new session)");

            // alias 是线程私有的：只有本播放线程能真正关掉它
            lock (_mciGate) { CloseAliasLocked(interrupted ? "play-interrupt" : "play-end"); }
        }

        private static bool StopAndCloseAlias(string reason)
        {
            lock (_mciGate) { return CloseAliasLocked(reason); }
        }

        /// <summary>
        /// 先 stop 再 close，close 失败重试一次。调用方必须已持有 _mciGate。
        /// 注意：MCI alias 线程私有，非打开线程调用会得到 263（MCIERR_INVALID_DEVICE_NAME），
        /// 这种确定性失败不再空转重试，直接交给播放线程在轮询里 close。
        /// </summary>
        private static bool CloseAliasLocked(string reason)
        {
            bool ownedHere = _mciOpenHere;
            int ownerTid = _mciOwnerTid;
            if (!ownedHere && ownerTid == 0) return true;   // 没有任何线程持有 alias

            try
            {
                mciSendString("stop " + Alias, null, 0, IntPtr.Zero);
                for (int attempt = 0; attempt <= MciCloseRetry; attempt++)
                {
                    int rc = mciSendString("close " + Alias, null, 0, IntPtr.Zero);
                    if (rc == 0)
                    {
                        if (ownedHere)
                        {
                            _mciOpenHere = false;
                            if (_mciOwnerTid == Thread.CurrentThread.ManagedThreadId) _mciOwnerTid = 0;
                        }
                        return true;
                    }

                    if (rc == MciErrInvalidDeviceName && !ownedHere)
                    {
                        // 跨线程：alias 属于播放线程，由它 60ms 轮询里的会话校验负责真正 close
                        Log("mci alias owned by thread " + ownerTid + " (rc=263, " + reason + ") -> deferred to poll");
                        return false;
                    }

                    Log("mci close fail rc=" + rc + " attempt=" + (attempt + 1) + " reason=" + reason);
                    if (attempt >= MciCloseRetry) break;
                    Thread.Sleep(10);
                    mciSendString("stop " + Alias, null, 0, IntPtr.Zero);
                }
            }
            catch (Exception ex)
            {
                Log("mci close error: " + ex.Message);
            }
            return false;
        }

        // ---------------- 文本清洗 ----------------

        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            StringBuilder raw = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // 换行 / 制表 / 其它控制字符 → 合并成单个空格
                if (c == '\r' || c == '\n' || c == '\t' || c == '\f' || c == '\v')
                {
                    raw.Append(' ');
                    continue;
                }
                // 零宽字符 / 软连字符：直接删掉（不产生空格，避免把词拆开）
                if (IsInvisible(c)) continue;
                // 其它控制字符：直接去掉（不引入假空格）
                if (char.IsControl(c)) continue;
                // 旧行为保留：项目符号/星号当空白
                if (c == '·' || c == '•' || c == '*') { raw.Append(' '); continue; }

                raw.Append(c);
            }
            return CollapseWhitespace(raw);
        }

        private static bool IsInvisible(char c)
        {
            return c == '\u00AD'     // SOFT HYPHEN
                || c == '\u200B'     // ZERO WIDTH SPACE
                || c == '\u200C'     // ZERO WIDTH NON-JOINER
                || c == '\u200D'     // ZERO WIDTH JOINER
                || c == '\u200E'     // LEFT-TO-RIGHT MARK
                || c == '\u200F'     // RIGHT-TO-LEFT MARK
                || c == '\u2060'     // WORD JOINER
                || c == '\u180E'     // MONGOLIAN VOWEL SEPARATOR
                || c == '\uFEFF';    // BOM / ZERO WIDTH NO-BREAK SPACE
        }

        /// <summary>连续空白折叠成单个空格，并去掉首尾空白。</summary>
        private static string CollapseWhitespace(StringBuilder raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length);
            bool pendingSpace = false;
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = true;
                    continue;
                }
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // ---------------- 语义断句 ----------------

        // 缩写保护表（比较时大小写不敏感）。英文句点落在这些词尾时不当句末。
        private static readonly HashSet<string> Abbrev = BuildAbbrev();

        private static HashSet<string> BuildAbbrev()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] words = new string[]
            {
                // 头衔 / 称谓
                "mr", "mrs", "ms", "dr", "prof", "st", "jr", "sr", "vs", "etc",
                "e.g", "i.e", "u.s", "u.k", "u.s.a", "fig", "no", "inc", "ltd",
                "a.m", "p.m", "approx",
                // 论文引用 / 版式（et al. / Vol. / pp. / Eq. / Ref. / Sec. / Ch.）
                "al", "vol", "pp", "eq", "ref", "sec", "ch", "cf", "viz",
                // 机构 / 地址
                "dept", "univ", "ave", "rd", "mt", "ft",
                // 月份（May 是常用词，故意不收）
                "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec"
            };
            for (int i = 0; i < words.Length; i++) set.Add(words[i]);
            return set;
        }

        // 常作"独立短句"的句末词：即使后面紧跟小写/数字（聊天体常见），仍按句末切分。
        private static readonly HashSet<string> SentenceEndWords = BuildSentenceEndWords();

        private static HashSet<string> BuildSentenceEndWords()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] words = new string[]
            {
                "yes", "no", "ok", "okay", "sure", "done", "fine", "good", "great",
                "thanks", "please", "sorry", "right", "correct", "wrong", "true", "false"
            };
            for (int i = 0; i < words.Length; i++) set.Add(words[i]);
            return set;
        }

        /// <summary>
        /// 英文句点是否句末：排除小数（3.14）、缩写（Dr./e.g./U.S./et al./Jan.…）、
        /// 单字母人名首字母（John F. Kennedy）；再兜底排除"后接小写/数字"的句中缩写。
        /// </summary>
        private static bool IsSentenceEndDot(string text, int i)
        {
            int len = text.Length;

            // 点号后面紧跟字母/数字：小数、域名、缩写内部点 → 不是句末
            if (i + 1 < len && char.IsLetterOrDigit(text[i + 1])) return false;

            // 找到后面第一个非空白字符
            int k = i + 1;
            while (k < len && char.IsWhiteSpace(text[k])) k++;
            if (k >= len) return true;   // 文本结尾

            // 取点号前面紧邻的词（允许中间带点，如 e.g / U.S）
            int start = i - 1;
            while (start >= 0)
            {
                char pc = text[start];
                if (char.IsLetterOrDigit(pc) || pc == '.' || pc == '\'') start--;
                else break;
            }
            string token = text.Substring(start + 1, i - start - 1);
            if (token.Length == 0) return true;
            if (Abbrev.Contains(token)) return false;

            // 单字母大写（人名首字母）不切
            if (token.Length == 1 && char.IsLetter(token[0]) && char.IsUpper(token[0])) return false;

            // 启发式兜底：句点后是空白，且下一个非空白字符是小写字母或数字 —— 通常是句中缩写/编号
            // （"Smith et al. showed"、"In Jan. 2024"、"Vol. 3"、"cf. the"），不是句子结束。
            // 例外：token 是常作独立短句的句末词（ok/yes/done…）时仍然切。
            char next = text[k];
            if ((char.IsLower(next) || char.IsDigit(next)) && !SentenceEndWords.Contains(token))
                return false;

            return true;
        }

        private static bool IsTerminalPunct(char c)
        {
            return c == '。' || c == '！' || c == '？' || c == '!'
                || c == '?' || c == '；' || c == ';' || c == '…' || c == '．';
        }

        private static bool IsClosingMark(char c)
        {
            return c == '"' || c == '\'' || c == '”' || c == '’'
                || c == '」' || c == '』' || c == '）' || c == ')'
                || c == ']' || c == '】' || c == '》' || c == '〉'
                || c == '〕' || c == '｝' || c == '}';
        }

        /// <summary>按句子切分：中英文句末标点（英文点号做缩写/小数保护），句末标点后的右引号一起收进本句。</summary>
        private static List<string> SplitSentences(string text)
        {
            List<string> list = new List<string>();
            int len = text.Length;
            int start = 0;

            for (int i = 0; i < len; i++)
            {
                char c = text[i];
                bool boundary = IsTerminalPunct(c);
                if (!boundary && c == '.') boundary = IsSentenceEndDot(text, i);
                if (!boundary) continue;

                // 吸收连续句末标点 / 省略号 / 右引号右括号
                int end = i + 1;
                while (end < len)
                {
                    char e = text[end];
                    if (e == '.' || IsTerminalPunct(e) || IsClosingMark(e)) { end++; continue; }
                    break;
                }

                string sentence = text.Substring(start, end - start).Trim();
                if (sentence.Length > 0) list.Add(sentence);
                start = end;
                i = end - 1;
            }

            if (start < len)
            {
                string tail = text.Substring(start).Trim();
                if (tail.Length > 0) list.Add(tail);
            }
            return list;
        }

        /// <summary>在逗号/分号/冒号处把超长句拆成子句（保留原字符，便于后面拼接）。</summary>
        private static List<string> SplitClauses(string s)
        {
            List<string> list = new List<string>();
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '，' || c == ',' || c == '；' || c == ';' || c == '：' || c == ':')
                {
                    list.Add(s.Substring(start, i - start + 1));
                    start = i + 1;
                }
            }
            if (start < s.Length) list.Add(s.Substring(start));
            return list;
        }

        /// <summary>
        /// 单句超过 SegmentMax 才允许二次拆分：只在逗号/分号/冒号处拆；
        /// 没有标点可用时退到**空白处**（绝不从单词中间断），纯中日文长串才按字符边界兜底。
        /// </summary>
        private static List<string> SplitLongSentence(string sentence, int max)
        {
            List<string> result = new List<string>();
            List<string> clauses = SplitClauses(sentence);
            StringBuilder cur = new StringBuilder();

            for (int i = 0; i < clauses.Count; i++)
            {
                string clause = clauses[i];
                if (clause.Length > max)
                {
                    if (cur.Length > 0)
                    {
                        result.Add(cur.ToString().Trim());
                        cur.Length = 0;
                    }
                    List<string> pieces = BreakClause(clause, max);
                    for (int j = 0; j < pieces.Count; j++) result.Add(pieces[j]);
                    continue;
                }

                if (cur.Length > 0 && cur.Length + clause.Length > max)
                {
                    result.Add(cur.ToString().Trim());
                    cur.Length = 0;
                }
                cur.Append(clause);
            }

            if (cur.Length > 0) result.Add(cur.ToString().Trim());
            return result;
        }

        private static List<string> BreakClause(string clause, int max)
        {
            List<string> result = new List<string>();
            string rest = clause.Trim();

            while (rest.Length > max)
            {
                int limit = max;
                if (limit > rest.Length - 1) limit = rest.Length - 1;

                int cut = -1;
                for (int i = limit; i > 0; i--)
                {
                    if (rest[i] == ' ') { cut = i; break; }
                }

                if (cut <= 0)
                {
                    // 没有空白可断：中文/日文没有词间空格，按字符边界拆是安全的；
                    // 纯 ASCII 长串才会出现"词中间断"，记日志留痕（正常情况下走不到）。
                    char boundaryChar = rest[limit];
                    if (boundaryChar < 128 && char.IsLetterOrDigit(boundaryChar))
                        Log("split fallback: no word boundary, char cut at " + limit);
                    cut = limit;
                }

                string piece = rest.Substring(0, cut).Trim();
                if (piece.Length > 0) result.Add(piece);
                rest = rest.Substring(cut).TrimStart();
            }

            if (rest.Length > 0) result.Add(rest);
            return result;
        }

        /// <summary>
        /// 生成播放分段：一句一段（保留句间自然停顿）；单句 &gt; SegmentMax 才二次拆；
        /// 整篇按完整句子累计，到 ~TotalMax 字符为止，绝不切半句，并记录是否截断。
        /// </summary>
        private static List<string> BuildSegments(string clean, out bool truncated)
        {
            truncated = false;
            List<string> result = new List<string>();
            List<string> sentences = SplitSentences(clean);
            int total = 0;
            bool stop = false;

            for (int i = 0; i < sentences.Count && !stop; i++)
            {
                string s = sentences[i];
                List<string> pieces;
                if (s.Length > SegmentMax) pieces = SplitLongSentence(s, SegmentMax);
                else
                {
                    pieces = new List<string>();
                    pieces.Add(s);
                }

                for (int j = 0; j < pieces.Count; j++)
                {
                    string p = pieces[j].Trim();
                    if (p.Length == 0) continue;

                    if (result.Count >= MaxSegments)
                    {
                        truncated = true;
                        stop = true;
                        break;
                    }
                    int add = p.Length + (result.Count > 0 ? 1 : 0);
                    if (total + add > TotalMax)
                    {
                        truncated = true;
                        stop = true;
                        break;
                    }
                    result.Add(p);
                    total += add;
                }
            }

            // 兜底：理论上 SplitSentences 至少返回一段；这里保证不会空手而归
            if (result.Count == 0 && clean.Length > 0)
            {
                string only = clean.Length > TotalMax ? clean.Substring(0, TotalMax) : clean;
                truncated = clean.Length > TotalMax;
                result.Add(only);
            }
            return result;
        }
    }
}
