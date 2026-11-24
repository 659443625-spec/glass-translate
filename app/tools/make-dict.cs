using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
/// 把 ECDICT 的 ecdict.csv 转成鼠标翻译用的精简离线词库。
/// 只保留高频词 + 考试标签词，输出 MTD1 格式（索引 + 词串 + 词条）。
///
/// 用法: make-dict.exe <ecdict.csv> <dict.bin>
/// </summary>
internal static class MakeDict
{
    private static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("用法: make-dict.exe <ecdict.csv> <dict.bin>");
            return 2;
        }

        string src = args[0];
        string dst = args[1];
        if (!File.Exists(src)) { Console.WriteLine("找不到 " + src); return 2; }

        List<Entry> list = new List<Entry>(200000);
        int total = 0;
        int skipped = 0;

        using (StreamReader r = new StreamReader(src, Encoding.UTF8, true))
        {
            string line = r.ReadLine();      // 表头
            while ((line = r.ReadLine()) != null)
            {
                total++;
                string[] f = SplitCsv(line);
                if (f.Length < 11) { skipped++; continue; }

                string word = f[0].Trim().ToLowerInvariant();
                string phonetic = f[1].Trim();
                string definition = Unescape(f[2]);
                string translation = Unescape(f[3]);
                string tag = f[7].Trim();
                int bnc = ParseInt(f[8]);
                int frq = ParseInt(f[9]);

                if (word.Length == 0 || word.Length > 40) { skipped++; continue; }
                if (!LooksLikeWord(word)) { skipped++; continue; }
                if (translation.Length == 0 && definition.Length == 0) { skipped++; continue; }

                bool exam = HasExamTag(tag);
                bool common = (frq >= 1 && frq <= 50000) || (bnc >= 1 && bnc <= 50000);
                if (!exam && !common) { skipped++; continue; }

                Entry e = new Entry();
                e.Word = word;
                e.Phonetic = phonetic;
                e.Translation = translation.Length > 0 ? translation : definition;
                e.Definition = definition;
                e.Tag = tag;
                e.Rank = exam ? -1 : Math.Min(frq > 0 ? frq : 99999, bnc > 0 ? bnc : 99999);
                list.Add(e);

                if (total % 100000 == 0) Console.WriteLine("  已读 " + total + " 行，保留 " + list.Count);
            }
        }

        Console.WriteLine("总行数 " + total + "，保留 " + list.Count + "，跳过 " + skipped);

        list.Sort(delegate (Entry a, Entry b) { return string.Compare(a.Word, b.Word, StringComparison.Ordinal); });
        // 去重（同词取考试标签优先/排名靠前）
        List<Entry> dedup = new List<Entry>(list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            if (dedup.Count > 0 && dedup[dedup.Count - 1].Word == list[i].Word)
            {
                if (list[i].Rank < dedup[dedup.Count - 1].Rank) dedup[dedup.Count - 1] = list[i];
                continue;
            }
            dedup.Add(list[i]);
        }
        list = dedup;
        Console.WriteLine("去重后 " + list.Count + " 条");

        // ---- 写文件 ----
        MemoryStream words = new MemoryStream();
        MemoryStream entries = new MemoryStream();
        int[] wordOff = new int[list.Count];
        int[] entryOff = new int[list.Count];

        for (int i = 0; i < list.Count; i++)
        {
            Entry e = list[i];
            wordOff[i] = (int)words.Position;
            byte[] wb = Encoding.UTF8.GetBytes(e.Word);
            words.Write(wb, 0, wb.Length);
            words.WriteByte(0);

            entryOff[i] = (int)entries.Position;
            WriteStr(entries, e.Phonetic);
            WriteStr(entries, e.Translation);
            WriteStr(entries, e.Definition);
            WriteStr(entries, e.Tag);
        }

        using (FileStream fs = new FileStream(dst, FileMode.Create, FileAccess.Write))
        using (BinaryWriter w = new BinaryWriter(fs))
        {
            w.Write(new byte[] { (byte)'M', (byte)'T', (byte)'D', (byte)'1' });
            w.Write(list.Count);

            int indexOffset = 16;
            int wordsOffset = indexOffset + list.Count * 8;
            int entriesOffset = wordsOffset + (int)words.Length;

            w.Write(wordsOffset);
            w.Write(entriesOffset);

            // 索引
            for (int i = 0; i < list.Count; i++)
            {
                w.Write(wordsOffset + wordOff[i]);
                w.Write(entriesOffset + entryOff[i]);
            }
            // 词串
            w.Write(words.ToArray());
            // 词条
            w.Write(entries.ToArray());

            Console.WriteLine("写出 " + dst + "  " + (fs.Length / 1024.0 / 1024.0).ToString("0.0") + " MB");
        }
        return 0;
    }

    private sealed class Entry
    {
        public string Word;
        public string Phonetic;
        public string Translation;
        public string Definition;
        public string Tag;
        public int Rank;
    }

    private static bool HasExamTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return false;
        return tag.Contains("zk") || tag.Contains("gk") || tag.Contains("cet4") ||
               tag.Contains("cet6") || tag.Contains("ky") || tag.Contains("toefl") || tag.Contains("ielts");
    }

    private static bool LooksLikeWord(string w)
    {
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            bool ok = (c >= 'a' && c <= 'z') || c == '\'' || c == '-' || c == ' ' || c == '.';
            if (!ok) return false;
        }
        return true;
    }

    private static void WriteStr(Stream w, string s)
    {
        byte[] b = Encoding.UTF8.GetBytes(s ?? string.Empty);
        if (b.Length > 60000) { byte[] t = new byte[60000]; Array.Copy(b, t, 60000); b = t; }
        w.WriteByte((byte)(b.Length & 0xFF));
        w.WriteByte((byte)((b.Length >> 8) & 0xFF));
        if (b.Length > 0) w.Write(b, 0, b.Length);
    }

    private static int ParseInt(string s)
    {
        int v;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
    }

    /// <summary>把 CSV 行拆成字段（支持双引号与 "" 转义）。</summary>
    private static string[] SplitCsv(string line)
    {
        List<string> f = new List<string>(16);
        StringBuilder cur = new StringBuilder();
        bool q = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (q)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                    else q = false;
                }
                else cur.Append(c);
            }
            else
            {
                if (c == '"') q = true;
                else if (c == ',') { f.Add(cur.ToString()); cur.Length = 0; }
                else cur.Append(c);
            }
        }
        f.Add(cur.ToString());
        return f.ToArray();
    }

    /// <summary>把字段里的 \n 转义还原成换行。</summary>
    private static string Unescape(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        StringBuilder sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n == 'n') { sb.Append('\n'); i++; continue; }
                if (n == 'r') { i++; continue; }
                if (n == 't') { sb.Append(' '); i++; continue; }
                if (n == '\\') { sb.Append('\\'); i++; continue; }
            }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
