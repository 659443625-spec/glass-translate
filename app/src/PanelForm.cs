using System;
using System.Collections.Generic;
using System.Drawing;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MouseTranslate
{
    /// <summary>用 WebView2 渲染的控制面板（苹果材质）。对外接口与原 MainForm 一致。</summary>
    internal sealed class PanelForm : Form
    {
        private const int W = 480;
        private const int H3 = 390;   // 悬停关闭时的行数
        private const int H4 = 428;

        private readonly App _app;
        private WebPanel _web;
        private bool _running;
        private bool _shownOnce;

        /// <summary>点「记录」时回调 App 打开记录窗口。</summary>
        public Action RecordsRequested;

        public PanelForm(App app)
        {
            _app = app;
            AutoScaleMode = AutoScaleMode.None;
            Text = "鼠标翻译";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(20, 22, 27);
            ClientSize = new Size(Dpi.S(W), Dpi.S(H3));
            CenterToScreen();
            Theme.ApplyRoundRegion(this, Dpi.S(20));

            _web = new WebPanel();
            _web.Message += OnWebMessage;
            _web.Dock = DockStyle.Fill;
            Controls.Add(_web);
        }

        // ---------------- 对外接口（与 MainForm 保持一致） ----------------

        public void SetRunning(bool running)
        {
            _running = running;
            try { _web.RunScript("window.__setRun && window.__setRun(" + (running ? "true" : "false") + ");"); } catch { }
        }

        public void SetCapabilities(string text) { }
        public void SetChannelStatus(string text) { }

        public void HideToTray()
        {
            Hide();
        }

        // ---------------- 显示与内容 ----------------

        protected override void SetVisibleCore(bool value)
        {
            if (value)
            {
                Rebuild();
                base.SetVisibleCore(true);
                UiAnim.SlideIn(this, 160, 8);
                _shownOnce = true;
                return;
            }
            base.SetVisibleCore(false);
        }

        private void Rebuild()
        {
            Settings s = _app.Settings;
            int h = s.HoverMode ? H4 : H3;
            Height = Dpi.S(h);

            Rectangle r = new Rectangle(Left, Top, Width, Height);
            Bitmap bg = Glass.Capture(r);
            int luma = WebUi.AverageLuma(bg);
            string b64 = WebUi.ToBase64(bg);
            if (bg != null) bg.Dispose();

            // 背景太暗（亮色主题下）/ 太亮（暗色主题下）时自动加厚材质，避免"发灰/刺眼"
            bool solid = Theme.Dark ? luma > 170 : luma < 90;

            string note = OfflineDict.Init(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dict.bin")) && OfflineDict.Available
                ? "离线词库 " + (OfflineDict.Count / 10000.0).ToString("0.0") + " 万词 · 断网也能查"
                : "离线词库未安装";
            _web.Html = WebUi.Panel(Theme.Dark, b64, _running, s.SelectionMode, s.HoverMode, s.ClipboardFallback, s.HoverNeedAlt, solid, note);
            _web.Size = ClientSize;
            if (_shownOnce) _web.Reload();
        }

        // ---------------- 页面消息 ----------------

        private void OnWebMessage(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> msg = ser.DeserializeObject(raw) as Dictionary<string, object>;
                if (msg == null) return;

                string type = msg.ContainsKey("type") ? Convert.ToString(msg["type"]) : string.Empty;
                if (type == "toggleRun") { _app.Toggle(); return; }
                if (type == "quit") { _app.Exit(); return; }
                if (type == "records") { if (RecordsRequested != null) RecordsRequested(); return; }
                if (type == "hide") { Hide(); return; }
                if (type != "toggle") return;

                string key = msg.ContainsKey("key") ? Convert.ToString(msg["key"]) : string.Empty;
                bool on = msg.ContainsKey("on") && msg["on"] is bool && (bool)msg["on"];
                Settings s = _app.Settings;

                switch (key)
                {
                    case "hover":
                        s.HoverMode = on;
                        Height = Dpi.S(on ? H4 : H3);
                        break;
                    case "clip": s.ClipboardFallback = on; break;
                    case "alt": s.HoverNeedAlt = on; break;
                    case "select": s.SelectionMode = on; break;
                }
                s.Save();
                _app.OnSettingsChanged();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
