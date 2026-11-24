using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace MouseTranslate
{
    internal sealed class HistoryItem
    {
        public string T = string.Empty;        // 时间
        public string Mode = "word";           // word / sentence
        public string Text = string.Empty;     // 原文
        public string Detail = string.Empty;   // 译文 / 释义摘要
        public string Provider = string.Empty;
    }

    internal sealed class VocabItem
    {
        public string T = string.Empty;
        public string Word = string.Empty;
        public string Phonetic = string.Empty;
        public string Defs = string.Empty;     // 多行释义
        public string Sentence = string.Empty; // 出处句
        public string Mode = "word";           // word / sentence
    }

    /// <summary>本地记录：最近翻译 + 生词本。放在 %APPDATA%\MouseTranslate\*.json。</summary>
    internal static class Store
    {
        private const int HistoryCap = 200;
        private static readonly object _lock = new object();

        private static List<HistoryItem> _history;
        private static List<VocabItem> _vocab;

        private static string Dir
        {
            get
            {
                string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MouseTranslate");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        private static string HistoryPath { get { return Path.Combine(Dir, "history.json"); } }
        private static string VocabPath { get { return Path.Combine(Dir, "vocab.json"); } }

        // ---------------- 历史 ----------------

        public static List<HistoryItem> History
        {
            get
            {
                lock (_lock)
                {
                    if (_history == null) _history = Load<List<HistoryItem>>(HistoryPath) ?? new List<HistoryItem>();
                    return _history;
                }
            }
        }

        public static void AddHistory(string mode, string text, string detail, string provider)
        {
            if (string.IsNullOrEmpty(text)) return;
            lock (_lock)
            {
                if (_history == null) _history = Load<List<HistoryItem>>(HistoryPath) ?? new List<HistoryItem>();

                // 同一条内容连续重复时不重复记录
                if (_history.Count > 0 && _history[0].Text == text && _history[0].Mode == mode) return;

                HistoryItem it = new HistoryItem();
                it.T = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                it.Mode = mode;
                it.Text = text;
                it.Detail = detail ?? string.Empty;
                it.Provider = provider ?? string.Empty;
                _history.Insert(0, it);
                while (_history.Count > HistoryCap) _history.RemoveAt(_history.Count - 1);
                Save(HistoryPath, _history);
            }
        }

        public static void ClearHistory()
        {
            lock (_lock)
            {
                _history = new List<HistoryItem>();
                Save(HistoryPath, _history);
            }
        }

        public static void RemoveHistory(string text, string time)
        {
            lock (_lock)
            {
                if (_history == null) return;
                for (int i = _history.Count - 1; i >= 0; i--)
                    if (_history[i].Text == text && _history[i].T == time) { _history.RemoveAt(i); break; }
                Save(HistoryPath, _history);
            }
        }

        // ---------------- 生词本 ----------------

        public static List<VocabItem> Vocab
        {
            get
            {
                lock (_lock)
                {
                    if (_vocab == null) _vocab = Load<List<VocabItem>>(VocabPath) ?? new List<VocabItem>();
                    return _vocab;
                }
            }
        }

        public static bool IsStarred(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            lock (_lock)
            {
                if (_vocab == null) _vocab = Load<List<VocabItem>>(VocabPath) ?? new List<VocabItem>();
                foreach (VocabItem v in _vocab)
                    if (string.Equals(v.Word, word, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public static void AddVocab(string word, string phonetic, string defs, string sentence)
        {
            AddVocab(word, phonetic, defs, sentence, "word");
        }

        public static void AddVocab(string word, string phonetic, string defs, string sentence, string mode)
        {
            if (string.IsNullOrEmpty(word)) return;
            lock (_lock)
            {
                if (_vocab == null) _vocab = Load<List<VocabItem>>(VocabPath) ?? new List<VocabItem>();
                foreach (VocabItem v in _vocab)
                {
                    if (string.Equals(v.Word, word, StringComparison.OrdinalIgnoreCase))
                    {
                        v.T = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                        if (!string.IsNullOrEmpty(phonetic)) v.Phonetic = phonetic;
                        if (!string.IsNullOrEmpty(defs)) v.Defs = defs;
                        if (!string.IsNullOrEmpty(sentence)) v.Sentence = sentence;
                        Save(VocabPath, _vocab);
                        return;
                    }
                }
                VocabItem it = new VocabItem();
                it.T = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                it.Word = word;
                it.Phonetic = phonetic ?? string.Empty;
                it.Defs = defs ?? string.Empty;
                it.Sentence = sentence ?? string.Empty;
                it.Mode = string.IsNullOrEmpty(mode) ? "word" : mode;
                _vocab.Insert(0, it);
                Save(VocabPath, _vocab);
            }
        }

        public static void RemoveVocab(string word)
        {
            lock (_lock)
            {
                if (_vocab == null) return;
                for (int i = _vocab.Count - 1; i >= 0; i--)
                    if (string.Equals(_vocab[i].Word, word, StringComparison.OrdinalIgnoreCase)) { _vocab.RemoveAt(i); break; }
                Save(VocabPath, _vocab);
            }
        }

        /// <summary>导出 CSV 到桌面，返回文件路径。</summary>
        public static string ExportCsv(bool vocabOnly)
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    vocabOnly ? "鼠标翻译-生词本.csv" : "鼠标翻译-历史.csv");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(vocabOnly ? "单词,音标,释义,出处句,时间" : "原文,译文或释义,来源,时间");

                if (vocabOnly)
                {
                    foreach (VocabItem v in Vocab)
                        sb.AppendLine(Csv(v.Word) + "," + Csv(v.Phonetic) + "," + Csv(v.Defs) + "," + Csv(v.Sentence) + "," + Csv(v.T));
                }
                else
                {
                    foreach (HistoryItem h in History)
                        sb.AppendLine(Csv(h.Text) + "," + Csv(h.Detail) + "," + Csv(h.Provider) + "," + Csv(h.T));
                }

                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // 带 BOM，Excel 中文不乱码
                return path;
            }
            catch { return string.Empty; }
        }

        private static string Csv(string s)
        {
            if (s == null) s = string.Empty;
            s = s.Replace("\"", "\"\"");
            return "\"" + s + "\"";
        }

        // ---------------- 读写 ----------------

        private static T Load<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                string json = File.ReadAllText(path, Encoding.UTF8);
                if (json.Trim().Length == 0) return null;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;
                return ser.Deserialize<T>(json);
            }
            catch { return null; }
        }

        private static void Save<T>(string path, T data)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 8 * 1024 * 1024;
                File.WriteAllText(path, ser.Serialize(data), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
