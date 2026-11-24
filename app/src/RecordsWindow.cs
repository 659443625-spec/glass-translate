using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>记录窗口：最近翻译 + 生词本，同一张玻璃。 </summary>
    internal sealed class RecordsWindow : Form
    {
        private const int W = 460;
        private const int H = 600;

        private readonly WebPanel _web;
        private bool _ready;

        /// <summary>显示时要切换到的标签页（打开后生效）。</summary>
        public string PendingTab;

        /// <summary>请求重新翻译某段文字（由 App 处理）。</summary>
        public Action<string> TranslateRequested;

        public RecordsWindow()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "翻译记录";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(20, 22, 27);
            ClientSize = new Size(Dpi.S(W), Dpi.S(H));
            MinimumSize = new Size(Dpi.S(W), Dpi.S(H));

            _web = new WebPanel();
            _web.Dock = DockStyle.Fill;
            _web.Message += OnMessage;
            Controls.Add(_web);

            Theme.ApplyRoundRegion(this, Dpi.S(20));
        }

        public void Refresh_()
        {
            if (!_ready) return;
            _web.RunScript("window.__setData && window.__setData(" + BuildData() + ");");
        }

        /// <summary>每次显示都重抓背景，保证玻璃和当前桌面一致。</summary>
        public void Prepare()
        {
            Rectangle r = new Rectangle(Left, Top, Width, Height);
            Bitmap bg = Glass.Capture(r);
            string b64 = WebUi.ToBase64(bg);
            if (bg != null) bg.Dispose();

            string html = WebUi.RecordsShell(Theme.Dark, b64);
            _web.Html = html;
            _ready = true;
            _web.Reload();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Refresh_();
            ApplyPendingTab();
        }

        public void ShowTab(string tab)
        {
            string which = tab == "vocab" ? "vocab" : "history";
            _web.RunScript("document.querySelector('.tab[data-tab=\"" + which + "\"]').click();");
        }

        public void ApplyPendingTab()
        {
            if (string.IsNullOrEmpty(PendingTab)) return;
            string tab = PendingTab;
            PendingTab = null;
            ShowTab(tab);
        }

        private static string ShortTime(string t)
        {
            if (string.IsNullOrEmpty(t)) return string.Empty;
            return t.Length >= 16 ? t.Substring(5, 11) : t;   // MM-dd HH:mm
        }

        private string BuildData()
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;

            List<Dictionary<string, object>> history = new List<Dictionary<string, object>>();
            foreach (HistoryItem h in Store.History)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["text"] = h.Text;
                d["detail"] = h.Detail;
                d["t"] = h.T;
                d["short"] = ShortTime(h.T);
                d["mode"] = h.Mode;
                history.Add(d);
            }

            List<Dictionary<string, object>> vocab = new List<Dictionary<string, object>>();
            foreach (VocabItem v in Store.Vocab)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["word"] = v.Word;
                d["phonetic"] = v.Phonetic;
                d["defs"] = v.Defs;
                d["sentence"] = v.Sentence;
                d["short"] = ShortTime(v.T);
                d["mode"] = v.Mode;
                vocab.Add(d);
            }

            Dictionary<string, object> root = new Dictionary<string, object>();
            root["history"] = history;
            root["vocab"] = vocab;
            return ser.Serialize(root);
        }

        private void OnMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> m = ser.DeserializeObject(raw) as Dictionary<string, object>;
                if (m == null) return;
                string type = m.ContainsKey("type") ? Convert.ToString(m["type"]) : string.Empty;

                switch (type)
                {
                    case "ready": Refresh_(); ApplyPendingTab(); break;
                    case "close": Hide(); break;
                    case "copy":
                        try { string t = GetStr(m,"text"); if (t.Length > 0) Clipboard.SetText(t); } catch { }
                        break;
                    case "translate":
                        {
                            string t = GetStr(m,"text");
                            if (t.Length > 0 && TranslateRequested != null) TranslateRequested(t);
                            break;
                        }
                    case "star":
                        {
                            string word = GetStr(m,"text");
                            string detail = GetStr(m,"detail");
                            Store.AddVocab(word, string.Empty, detail, string.Empty);
                            Toasting("已加入生词本");
                            Refresh_();
                            break;
                        }
                    case "del":
                        {
                            string key = GetStr(m,"key");
                            string tab = GetStr(m,"tab");
                            int sep = key.IndexOf('\u0001');
                            if (tab == "history" && sep > 0)
                                Store.RemoveHistory(key.Substring(0, sep), key.Substring(sep + 1));
                            else if (tab == "vocab")
                                Store.RemoveVocab(key);
                            Refresh_();
                            break;
                        }
                    case "clear":
                        if (GetStr(m,"tab") == "vocab")
                        {
                            foreach (VocabItem v in new List<VocabItem>(Store.Vocab)) Store.RemoveVocab(v.Word);
                        }
                        else Store.ClearHistory();
                        Refresh_();
                        break;
                    case "export":
                        {
                            bool vocabOnly = m.ContainsKey("vocabOnly") && m["vocabOnly"] is bool && (bool)m["vocabOnly"];
                            string path = Store.ExportCsv(vocabOnly);
                            Toasting(path.Length > 0 ? "已导出：" + path : "导出失败");
                            break;
                        }
                }
            }
            catch { }
        }

        private static string GetStr(Dictionary<string, object> m, string k)
        {
            return m.ContainsKey(k) ? Convert.ToString(m[k]) : string.Empty;
        }

        private void Toasting(string msg)
        {
            try
            {
                string js = "window.__toast && window.__toast(" + Q(msg) + ");";
                _web.RunScript(js);
            }
            catch { }
        }

        private static string Q(string s)
        {
            if (s == null) s = string.Empty;
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c >= 32) sb.Append(c);
            }
            sb.Append('"');
            return sb.ToString();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { Hide(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Y < Dpi.S(48)) Native.DragWindow(Handle);
            base.OnMouseDown(e);
        }
    }
}
