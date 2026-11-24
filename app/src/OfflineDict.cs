using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MouseTranslate
{
    /// <summary>
    /// 离线词库（ECDICT 精简版，5.7 万高频词）。文件格式 MTD1：
    /// [magic 4][count 4][wordsOffset 4][entriesOffset 4]
    /// [index: count × (int32 wordOff, int32 entryOff)]
    /// [words: UTF8 + \0]
    /// [entries: (u16 len + bytes) × 4] 依次为 音标 / 释义 / 英文释义 / 标签
    /// </summary>
    internal static class OfflineDict
    {
        private static byte[] _buf;
        private static string[] _words;
        private static int[] _wordOff;
        private static int[] _entryOff;
        private static bool _tried;

        public static int Count { get { return _words == null ? 0 : _words.Length; } }
        public static bool Available { get { return _words != null; } }

        public static bool Init(string path)
        {
            if (_tried) return _words != null;
            _tried = true;
            try
            {
                if (!File.Exists(path)) return false;
                byte[] buf = File.ReadAllBytes(path);
                if (buf.Length < 20) return false;
                if (buf[0] != 'M' || buf[1] != 'T' || buf[2] != 'D' || buf[3] != '1') return false;

                int count = BitConverter.ToInt32(buf, 4);
                int indexOff = 16;
                if (count <= 0 || indexOff + count * 8 > buf.Length) return false;

                int[] wo = new int[count];
                int[] eo = new int[count];
                for (int i = 0; i < count; i++)
                {
                    wo[i] = BitConverter.ToInt32(buf, indexOff + i * 8);
                    eo[i] = BitConverter.ToInt32(buf, indexOff + i * 8 + 4);
                }

                string[] words = new string[count];
                for (int i = 0; i < count; i++)
                {
                    int start = wo[i];
                    int end = start;
                    while (end < buf.Length && buf[end] != 0) end++;
                    words[i] = Encoding.UTF8.GetString(buf, start, end - start);
                }

                _buf = buf;
                _words = words;
                _wordOff = wo;
                _entryOff = eo;
                return true;
            }
            catch { return false; }
        }

        /// <summary>查词；未收录返回 null。</summary>
        public static DictEntry Lookup(string word)
        {
            if (_words == null) return null;
            string key = (word ?? string.Empty).Trim().ToLowerInvariant();
            if (key.Length == 0) return null;

            int lo = 0, hi = _words.Length - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                int cmp = string.CompareOrdinal(_words[mid], key);
                if (cmp == 0) { found = mid; break; }
                if (cmp < 0) lo = mid + 1; else hi = mid - 1;
            }
            if (found < 0) return null;

            try
            {
                int pos = _entryOff[found];
                string phonetic = ReadStr(ref pos);
                string translation = ReadStr(ref pos);
                string definition = ReadStr(ref pos);
                string tag = ReadStr(ref pos);

                DictEntry e = new DictEntry();
                e.Word = _words[found];
                e.Phonetic = phonetic;
                e.Provider = "离线词库";
                e.Ms = 0;
                ParseInto(e, translation, definition);
                if (e.Groups.Count == 0 && e.EnglishDefs.Count == 0)
                {
                    // 只有标签没有释义，仍然算命中
                    DictSense g = new DictSense();
                    g.Pos = tag.Length > 0 ? tag : string.Empty;
                    g.Senses.Add("（离线词库无中文释义）");
                    e.Groups.Add(g);
                }
                e.Ok = true;
                return e;
            }
            catch { return null; }
        }

        private static string ReadStr(ref int pos)
        {
            int len = _buf[pos] | (_buf[pos + 1] << 8);
            pos += 2;
            string s = len == 0 ? string.Empty : Encoding.UTF8.GetString(_buf, pos, len);
            pos += len;
            return s;
        }

        // ---------------- 释义解析 ----------------

        private static readonly string[] PosTokens = new string[]
        {
            "n.", "v.", "vt.", "vi.", "adj.", "adv.", "prep.", "conj.", "pron.", "num.",
            "art.", "int.", "aux.", "abbr.", "pl.", "a.", "ad."
        };

        private static void ParseInto(DictEntry e, string translation, string definition)
        {
            if (!string.IsNullOrEmpty(translation))
            {
                string[] lines = translation.Replace("\r", string.Empty).Split('\n');
                DictSense current = null;
                foreach (string raw in lines)
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0) continue;

                    string pos = string.Empty;
                    string rest = line;
                    foreach (string t in PosTokens)
                    {
                        if (line.StartsWith(t, StringComparison.OrdinalIgnoreCase))
                        {
                            pos = t;
                            rest = line.Substring(t.Length).Trim();
                            break;
                        }
                    }

                    // [网络] [医] [化] 这类标签行
                    if (pos.Length == 0 && line.StartsWith("[", StringComparison.Ordinal))
                    {
                        int close = line.IndexOf(']');
                        if (close > 0 && close < 8)
                        {
                            pos = line.Substring(0, close + 1);
                            rest = line.Substring(close + 1).Trim();
                        }
                    }

                    if (pos.Length > 0)
                    {
                        current = new DictSense();
                        current.Pos = pos;
                        e.Groups.Add(current);
                    }
                    else if (current == null)
                    {
                        current = new DictSense();
                        e.Groups.Add(current);
                    }

                    foreach (string s in SplitSenses(rest))
                        if (s.Length > 0) current.Senses.Add(s);
                }
            }

            if (!string.IsNullOrEmpty(definition))
            {
                foreach (string raw in definition.Replace("\r", string.Empty).Split('\n'))
                {
                    string line = (raw ?? string.Empty).Trim();
                    if (line.Length == 0) continue;
                    line = line.TrimStart('*', '-', ' ');
                    if (line.Length == 0) continue;
                    if (e.EnglishDefs.Count >= 6) break;
                    e.EnglishDefs.Add(line);
                }
            }
        }

        private static List<string> SplitSenses(string s)
        {
            List<string> list = new List<string>();
            if (string.IsNullOrEmpty(s)) return list;
            string[] parts = s.Split(new char[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string t = p.Trim().Trim(',', '，', '。', '.');
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }
    }
}
