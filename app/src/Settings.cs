using System;
using System.Globalization;
using System.IO;

namespace MouseTranslate
{
    /// <summary>配置持久化（%APPDATA%\MouseTranslate\settings.ini，键值对，够用且不依赖任何序列化库）。</summary>
    internal sealed class Settings
    {
        public bool HoverMode = false;       // 悬停取词（默认关：鼠标停着不该有反应）
        public bool SelectionMode = true;    // 划词后出图标
        public bool ClipboardFallback = true;  // 剪贴板兜底（UIA 取不到选区的网页/软件靠它）
        public bool HoverNeedAlt = false;    // 悬停需按住 Alt（安静模式）
        public bool DebugLog = false;        // 写调试日志
        public string TargetLang = "zh-CN";  // 目标语言
        public int HoverDelayMs = 320;       // 悬停多久算“停留”
        public bool AutoStart;               // 开机自启（仅记录，执行在设置页）

        private static string FilePath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseTranslate");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.ini");
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                string f = FilePath();
                if (!File.Exists(f)) return s;
                foreach (string raw in File.ReadAllLines(f))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    switch (k)
                    {
                        case "HoverMode": s.HoverMode = Bool(v, false); break;
                        case "SelectionMode": s.SelectionMode = Bool(v, true); break;
                        case "ClipboardFallback": s.ClipboardFallback = Bool(v, true); break;
                        case "HoverNeedAlt": s.HoverNeedAlt = Bool(v, false); break;
                        case "DebugLog": s.DebugLog = Bool(v, false); break;
                        case "TargetLang": if (v.Length > 0) s.TargetLang = v; break;
                        case "HoverDelayMs": s.HoverDelayMs = Int(v, 320); break;
                        case "AutoStart": s.AutoStart = Bool(v, false); break;
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                string[] lines = new string[]
                {
                    "# 鼠标翻译配置",
                    "HoverMode=" + (HoverMode ? "1" : "0"),
                    "SelectionMode=" + (SelectionMode ? "1" : "0"),
                    "ClipboardFallback=" + (ClipboardFallback ? "1" : "0"),
                    "HoverNeedAlt=" + (HoverNeedAlt ? "1" : "0"),
                    "DebugLog=" + (DebugLog ? "1" : "0"),
                    "TargetLang=" + TargetLang,
                    "HoverDelayMs=" + HoverDelayMs.ToString(CultureInfo.InvariantCulture),
                    "AutoStart=" + (AutoStart ? "1" : "0")
                };
                File.WriteAllLines(FilePath(), lines);
            }
            catch { }
        }

        private static bool Bool(string v, bool fallback)
        {
            if (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || v.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        private static int Int(string v, int fallback)
        {
            int n;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return n;
            return fallback;
        }
    }
}
