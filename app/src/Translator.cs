using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MouseTranslate
{
    internal sealed class TranslateResult
    {
        public bool Ok;
        public string Text = string.Empty;
        public string Provider = string.Empty;
        public long Ms;
        public string Error = string.Empty;
        public string Phonetic = string.Empty;
        public List<string> DictionaryLines = new List<string>();
    }

    /// <summary>一个词性下的一组义项。</summary>
    internal sealed class DictSense
    {
        public string Pos = string.Empty;
        public List<string> Senses = new List<string>();
    }

    /// <summary>单词的完整词条：音标 + 按词性分组的全部义项。</summary>
    internal sealed class DictEntry
    {
        public bool Ok;
        public string Word = string.Empty;
        public string Phonetic = string.Empty;
        public List<DictSense> Groups = new List<DictSense>();
        public List<string> EnglishDefs = new List<string>();
        public string Provider = string.Empty;
        public long Ms;
        public string Error = string.Empty;
    }

    /// <summary>
    /// 翻译通道链：Google 免费接口 → MyMemory。
    /// 两条都不需要 API key、不需要账号；桌面程序不受浏览器 CORS 限制。
    /// </summary>
    internal sealed class Translator : IDisposable
    {
        private static readonly HttpClient Http = CreateClient();
        private readonly Dictionary<string, TranslateResult> _cache = new Dictionary<string, TranslateResult>();
        private readonly Dictionary<string, DictEntry> _dictCache = new Dictionary<string, DictEntry>();
        private readonly LinkedList<string> _dictOrder = new LinkedList<string>();
        private readonly LinkedList<string> _order = new LinkedList<string>();
        private const int CacheLimit = 300;

        private static HttpClient CreateClient()
        {
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
            HttpClient c = new HttpClient();
            c.Timeout = TimeSpan.FromSeconds(9);
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
            return c;
        }

        public static bool IsChinese(string s)
        {
            foreach (char c in s)
            {
                if (c >= '\u4e00' && c <= '\u9fff') return true;
            }
            return false;
        }

        /// <summary>
        /// 翻译方向自动判断：中文→英文，其它（英文等）→中文。
        /// 不再提供"目标语言"开关——选 English 只会得到"英→英"，没有意义。
        /// </summary>
        public static string TargetFor(string text)
        {
            return IsChinese(text) ? "en" : "zh-CN";
        }

        /// <summary>给界面用的一句话方向说明。</summary>
        public static string DirectionLabel(string text)
        {
            return IsChinese(text) ? "中 → 英" : "英 → 中";
        }

        public async Task<TranslateResult> TranslateAsync(string text, string toLang)
        {
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                TranslateResult empty = new TranslateResult();
                empty.Ok = false;
                empty.Error = "没有可翻译的文字";
                return empty;
            }

            string from = IsChinese(text) ? "zh-CN" : "en";
            string key = from + ">" + toLang + "|" + text;
            lock (_cache)
            {
                TranslateResult hit;
                if (_cache.TryGetValue(key, out hit)) return hit;
            }

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            List<string> errors = new List<string>();

            try
            {
                TranslateResult r = await GoogleAsync(text, from, toLang, sw).ConfigureAwait(false);
                if (r.Ok) { Remember(key, r); return r; }
                errors.Add("Google: " + r.Error);
            }
            catch (Exception ex) { errors.Add("Google: " + ex.Message); }

            try
            {
                TranslateResult r = await MyMemoryAsync(text, from, toLang, sw).ConfigureAwait(false);
                if (r.Ok) { Remember(key, r); return r; }
                errors.Add("MyMemory: " + r.Error);
            }
            catch (Exception ex) { errors.Add("MyMemory: " + ex.Message); }

            TranslateResult fail = new TranslateResult();
            fail.Ok = false;
            fail.Ms = sw.ElapsedMilliseconds;
            fail.Error = "所有翻译通道均失败：\n· " + string.Join("\n· ", errors.ToArray());
            return fail;
        }

        private static async Task<TranslateResult> GoogleAsync(string text, string from, string to, System.Diagnostics.Stopwatch sw)
        {
            string url = "https://translate.googleapis.com/translate_a/single?client=gtx" +
                         "&sl=" + Uri.EscapeDataString(from) +
                         "&tl=" + Uri.EscapeDataString(to) +
                         "&dt=t&q=" + Uri.EscapeDataString(text);

            string body = await Http.GetStringAsync(url).ConfigureAwait(false);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            object[] root = ser.DeserializeObject(body) as object[];
            if (root == null || root.Length == 0 || !(root[0] is object[])) throw new Exception("返回格式异常");

            StringBuilder sb = new StringBuilder();
            foreach (object seg in (object[])root[0])
            {
                object[] parts = seg as object[];
                if (parts == null || parts.Length == 0) continue;
                string piece = parts[0] as string;
                if (!string.IsNullOrEmpty(piece)) sb.Append(piece);
            }
            string outText = sb.ToString().Trim();
            if (outText.Length == 0) throw new Exception("空结果");

            TranslateResult r = new TranslateResult();
            r.Ok = true;
            r.Text = outText;
            r.Provider = "Google";
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        /// <summary>
        /// MyMemory 通道。注意它有两个坑（都已处理）：
        ///  ① 文本超过 500 字符会返回 HTTP 200 + "QUERY LENGTH LIMIT EXCEEDED…" 的"伪译文"，
        ///     绝不能当译文展示/写历史 → 超长直接判失败（Google 已先试过，这里不必切块）。
        ///  ② 配额用尽会返回 HTTP 200 + responseStatus=429 / responseDetails 里带 MYMEMORY WARNING →
        ///     用 responseStatus / responseDetails / 明确错误前缀三重判断，任一命中都算失败。
        /// </summary>
        private static async Task<TranslateResult> MyMemoryAsync(string text, string from, string to, System.Diagnostics.Stopwatch sw)
        {
            TranslateResult r = new TranslateResult();
            r.Provider = "MyMemory";
            r.Ms = sw.ElapsedMilliseconds;

            if (text.Length > MyMemoryMaxChars)
            {
                r.Ok = false;
                r.Error = "文本超过 " + MyMemoryMaxChars + " 字符，MyMemory 通道跳过";
                return r;
            }

            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) +
                         "&langpair=" + Uri.EscapeDataString(from + "|" + to);

            string body = await Http.GetStringAsync(url).ConfigureAwait(false);
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 8 * 1024 * 1024;
            Dictionary<string, object> root = ser.DeserializeObject(body) as Dictionary<string, object>;
            if (root == null) throw new Exception("返回格式异常");

            // 状态码：官方协议里 200 才是正常译文
            string details = string.Empty;
            object detailsObj;
            if (root.TryGetValue("responseDetails", out detailsObj) && detailsObj != null)
                details = Convert.ToString(detailsObj);

            object statusObj;
            int status;
            if (root.TryGetValue("responseStatus", out statusObj) && statusObj != null &&
                TryReadInt(statusObj, out status) && status != 200)
            {
                r.Ok = false;
                r.Error = "返回状态 " + status + (details.Length > 0 ? "：" + ShortError(details) : string.Empty);
                r.Ms = sw.ElapsedMilliseconds;
                return r;
            }

            // 配额 / 非法请求等错误有时只在 responseDetails 里
            if (IsErrorText(details))
            {
                r.Ok = false;
                r.Error = "接口报错：" + ShortError(details);
                r.Ms = sw.ElapsedMilliseconds;
                return r;
            }

            object rd;
            if (!root.TryGetValue("responseData", out rd)) throw new Exception("缺少 responseData");
            Dictionary<string, object> data = rd as Dictionary<string, object>;
            if (data == null) throw new Exception("responseData 异常");

            object tt;
            if (!data.TryGetValue("translatedText", out tt)) throw new Exception("缺少 translatedText");
            string outText = Convert.ToString(tt).Trim();
            if (outText.Length == 0) throw new Exception("空结果");

            // 超长 / 配额 / 非法请求会被塞进 translatedText 当"译文"返回
            if (IsErrorText(outText))
            {
                r.Ok = false;
                r.Error = "返回错误文本：" + ShortError(outText);
                r.Ms = sw.ElapsedMilliseconds;
                return r;
            }

            r.Ok = true;
            r.Text = outText;
            r.Ms = sw.ElapsedMilliseconds;
            return r;
        }

        /// <summary>MyMemory 单次请求的硬上限（超过它会返回伪译文）。</summary>
        private const int MyMemoryMaxChars = 500;

        /// <summary>
        /// 错误文本特征：只认 MyMemory 的「明确错误前缀」。
        /// 以前对 LIMIT / QUOTA / INVALID 做宽泛子串匹配，正常译文里出现 "limits"、
        /// "invalid" 之类的词就会被误判失败（P2-4）；现在只匹配协议里一眼能认出是报错的整句前缀。
        /// responseStatus / responseDetails 的判定不在这里，仍然保留在调用方。
        /// </summary>
        private static bool IsErrorText(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.TrimStart();
            if (t.StartsWith("QUERY LENGTH LIMIT", StringComparison.OrdinalIgnoreCase)) return true;
            if (t.StartsWith("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ShortError(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= 120 ? s : s.Substring(0, 120) + "…";
        }

        private static bool TryReadInt(object v, out int n)
        {
            n = 0;
            if (v == null) return false;
            if (v is int) { n = (int)v; return true; }
            if (v is long)
            {
                long l = (long)v;
                if (l > int.MaxValue || l < int.MinValue) return false;
                n = (int)l;
                return true;
            }
            if (v is double) { n = (int)(double)v; return true; }
            return int.TryParse(Convert.ToString(v), out n);
        }

        /// <summary>查英文单词的词典释义（可选增强，失败不影响主流程）。</summary>
        public async Task FillDictionaryAsync(TranslateResult r, string word)
        {
            string w = word.Trim().TrimEnd('.', ',', '\'', '’', ';', ':', '!', '?').ToLowerInvariant();
            if (w.Length == 0 || w.IndexOf(' ') >= 0) return;
            try
            {
                string url = "https://api.dictionaryapi.dev/api/v2/entries/en/" + Uri.EscapeDataString(w);
                string body = await Http.GetStringAsync(url).ConfigureAwait(false);
                JavaScriptSerializer ser = new JavaScriptSerializer();
                ser.MaxJsonLength = 4 * 1024 * 1024;
                object[] root = ser.DeserializeObject(body) as object[];
                if (root == null || root.Length == 0) return;

                Dictionary<string, object> entry = root[0] as Dictionary<string, object>;
                if (entry == null) return;

                object ph;
                if (entry.TryGetValue("phonetic", out ph) && ph != null) r.Phonetic = Convert.ToString(ph);

                object meaningsObj;
                if (!entry.TryGetValue("meanings", out meaningsObj)) return;
                object[] meanings = meaningsObj as object[];
                if (meanings == null) return;

                int n = 0;
                foreach (object mo in meanings)
                {
                    if (n >= 3) break;
                    Dictionary<string, object> m = mo as Dictionary<string, object>;
                    if (m == null) continue;
                    string pos = "";
                    object p;
                    if (m.TryGetValue("partOfSpeech", out p) && p != null) pos = Convert.ToString(p);

                    object defsObj;
                    if (!m.TryGetValue("definitions", out defsObj)) continue;
                    object[] defs = defsObj as object[];
                    if (defs == null || defs.Length == 0) continue;

                    Dictionary<string, object> d0 = defs[0] as Dictionary<string, object>;
                    if (d0 == null) continue;
                    object dt;
                    if (!d0.TryGetValue("definition", out dt) || dt == null) continue;

                    r.DictionaryLines.Add((pos.Length > 0 ? "[" + pos + "] " : "") + Convert.ToString(dt));
                    n++;
                }
            }
            catch { }
        }

        private void Remember(string key, TranslateResult value)
        {
            lock (_cache)
            {
                _cache[key] = value;
                _order.AddLast(key);
                while (_order.Count > CacheLimit)
                {
                    string old = _order.First.Value;
                    _order.RemoveFirst();
                    _cache.Remove(old);
                }
            }
        }

        // ---------------- 词典：多词性 + 全部义项 ----------------

        /// <summary>查一个英文单词：有道 jsonapi（权威多义项）→ 有道 suggest → 失败。</summary>
        public async Task<DictEntry> LookupAsync(string word)
        {
            string w = (word ?? string.Empty).Trim();
            DictEntry entry = new DictEntry();
            entry.Word = w;
            if (w.Length == 0) { entry.Error = "空词"; return entry; }

            string key = "dict|" + w.ToLowerInvariant();
            lock (_cache)
            {
                DictEntry hit;
                if (_dictCache.TryGetValue(key, out hit)) return hit;
            }

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            List<string> errors = new List<string>();

            // 1) 先查离线词库：瞬间返回、断网可用
            try
            {
                string dictPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dict.bin");
                if (OfflineDict.Init(dictPath))
                {
                    DictEntry local = OfflineDict.Lookup(w);
                    if (local != null) { RememberDict(key, local); return local; }
                }
            }
            catch { }

            try
            {
                DictEntry y = await YoudaoJsonAsync(w, sw).ConfigureAwait(false);
                if (y.Ok) { RememberDict(key, y); return y; }
                errors.Add("jsonapi: " + y.Error);
            }
            catch (Exception ex) { errors.Add("jsonapi: " + ex.Message); }

            try
            {
                DictEntry y2 = await YoudaoSuggestAsync(w, sw).ConfigureAwait(false);
                if (y2.Ok) { RememberDict(key, y2); return y2; }
                errors.Add("suggest: " + y2.Error);
            }
            catch (Exception ex) { errors.Add("suggest: " + ex.Message); }

            entry.Ok = false;
            entry.Ms = sw.ElapsedMilliseconds;
            entry.Error = string.Join(" / ", errors.ToArray());
            return entry;
        }

        private static async Task<DictEntry> YoudaoJsonAsync(string word, System.Diagnostics.Stopwatch sw)
        {
            DictEntry e = new DictEntry();
            e.Word = word;

            string url = "https://dict.youdao.com/jsonapi?q=" + Uri.EscapeDataString(word);
            HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Referer", "https://dict.youdao.com/");

            HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 16 * 1024 * 1024;
            Dictionary<string, object> root = ser.DeserializeObject(body) as Dictionary<string, object>;
            if (root == null) throw new Exception("bad json");

            object ecObj;
            if (!root.TryGetValue("ec", out ecObj) || ecObj == null) { e.Error = "无 ec 字段"; return e; }
            Dictionary<string, object> ec = ecObj as Dictionary<string, object>;
            if (ec == null) { e.Error = "ec 结构异常"; return e; }

            Dictionary<string, object> wordObj = FirstDict(ec, "word");
            if (wordObj == null) { e.Error = "无 word 字段"; return e; }

            e.Phonetic = Str(wordObj, "usphone");
            if (e.Phonetic.Length == 0) e.Phonetic = Str(wordObj, "ukphone");

            object trsObj;
            if (!wordObj.TryGetValue("trs", out trsObj) || trsObj == null) { e.Error = "无 trs"; return e; }

            List<string> allLines = new List<string>();

            foreach (object g in AsArray(trsObj))
            {
                Dictionary<string, object> group = g as Dictionary<string, object>;
                if (group == null) continue;

                object trObj;
                if (!group.TryGetValue("tr", out trObj)) continue;
                Dictionary<string, object> tr = FirstDictOf(trObj);
                if (tr == null) continue;

                object lObj;
                if (!tr.TryGetValue("l", out lObj)) continue;
                Dictionary<string, object> l = lObj as Dictionary<string, object>;
                if (l == null) continue;

                object iObj;
                if (!l.TryGetValue("i", out iObj)) continue;

                foreach (object item in AsArray(iObj))
                {
                    string line = item as string;
                    if (string.IsNullOrEmpty(line)) continue;
                    allLines.Add(line);
                }
            }

            ParseDefinitionLines(e, allLines);

            if (e.Groups.Count == 0) { e.Error = "未解析出义项"; return e; }

            e.Ok = true;
            e.Provider = "有道词典";
            e.Ms = sw.ElapsedMilliseconds;
            return e;
        }

        private static async Task<DictEntry> YoudaoSuggestAsync(string word, System.Diagnostics.Stopwatch sw)
        {
            DictEntry e = new DictEntry();
            e.Word = word;

            string url = "https://dict.youdao.com/suggest?num=3&doctype=json&q=" + Uri.EscapeDataString(word);
            string body = await Http.GetStringAsync(url).ConfigureAwait(false);

            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = 4 * 1024 * 1024;
            Dictionary<string, object> root = ser.DeserializeObject(body) as Dictionary<string, object>;
            if (root == null) throw new Exception("bad json");

            object dataObj;
            if (!root.TryGetValue("data", out dataObj)) { e.Error = "无 data"; return e; }
            Dictionary<string, object> data = dataObj as Dictionary<string, object>;
            if (data == null) { e.Error = "data 异常"; return e; }

            object entriesObj;
            if (!data.TryGetValue("entries", out entriesObj)) { e.Error = "无 entries"; return e; }

            foreach (object o in AsArray(entriesObj))
            {
                Dictionary<string, object> item = o as Dictionary<string, object>;
                if (item == null) continue;
                string entryWord = Str(item, "entry");
                string explain = Str(item, "explain");
                if (explain.Length == 0) continue;
                if (entryWord.Length > 0 && !entryWord.Equals(word, StringComparison.OrdinalIgnoreCase)) continue;
                List<string> one = new List<string>();
                one.Add(explain);
                ParseDefinitionLines(e, one);
                break;
            }

            if (e.Groups.Count == 0) { e.Error = "没有匹配词条"; return e; }

            e.Ok = true;
            e.Provider = "有道词典";
            e.Ms = sw.ElapsedMilliseconds;
            return e;
        }

        /// <summary>
        /// 有道返回的是一串「行」：词性行（可能只有 "v．"）开启分组，
        /// 后面的编号行（"1. xxx"）归入当前分组。必须按顺序解析，逐行独立判断会错位。
        /// </summary>
        private static void ParseDefinitionLines(DictEntry e, List<string> rawLines)
        {
            DictSense current = null;

            foreach (string raw in rawLines)
            {
                string line = NormalizePunct(raw);
                if (line.Length == 0) continue;

                // 只有词性，例如 "v." / "n."
                System.Text.RegularExpressions.Match posOnly = System.Text.RegularExpressions.Regex.Match(
                    line, @"^([a-zA-Z]{1,7})\.$");
                if (posOnly.Success)
                {
                    current = NewGroup(e, posOnly.Groups[1].Value.ToLowerInvariant() + ".");
                    continue;
                }

                // 词性 + 内容，例如 "n. 一窝（雏鸟、幼兽等）：指…"
                System.Text.RegularExpressions.Match posFull = System.Text.RegularExpressions.Regex.Match(
                    line, @"^([a-zA-Z]{1,7})\.\s*(.+)$");
                if (posFull.Success)
                {
                    current = NewGroup(e, posFull.Groups[1].Value.ToLowerInvariant() + ".");
                    AddSenses(current, posFull.Groups[2].Value);
                    continue;
                }

                // 编号义项，例如 "1. 孵（蛋）：指…"
                System.Text.RegularExpressions.Match numbered = System.Text.RegularExpressions.Regex.Match(
                    line, @"^[0-9]{1,2}\s*[.、)]\s*(.+)$");
                if (numbered.Success)
                {
                    if (current == null) current = NewGroup(e, string.Empty);
                    AddSenses(current, numbered.Groups[1].Value);
                    continue;
                }

                // 【名】这类中文标签
                System.Text.RegularExpressions.Match zhTag = System.Text.RegularExpressions.Regex.Match(
                    line, @"^【(.{1,4})】\s*(.+)$");
                if (zhTag.Success)
                {
                    current = NewGroup(e, zhTag.Groups[1].Value);
                    AddSenses(current, zhTag.Groups[2].Value);
                    continue;
                }

                if (current == null) current = NewGroup(e, string.Empty);
                AddSenses(current, line);
            }

            // 去掉空分组（有词性没义项的）
            for (int i = e.Groups.Count - 1; i >= 0; i--)
            {
                if (e.Groups[i].Senses.Count == 0) e.Groups.RemoveAt(i);
            }
        }

        private static DictSense NewGroup(DictEntry e, string pos)
        {
            if (e.Groups.Count > 0 && e.Groups[e.Groups.Count - 1].Pos == pos)
                return e.Groups[e.Groups.Count - 1];

            DictSense g = new DictSense();
            g.Pos = pos;
            e.Groups.Add(g);
            return g;
        }

        private static void AddSenses(DictSense group, string text)
        {
            if (text == null) return;
            string[] parts = text.Split(new char[] { '；', ';', '｜', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string raw in parts)
            {
                string s = raw.Trim();
                // 去掉残留的 "1." "2、" 编号
                s = System.Text.RegularExpressions.Regex.Replace(s, @"^[0-9]{1,2}\s*[.、)]\s*", string.Empty);
                s = s.Trim().Trim('。', '，', ',', '.', '：', ':');
                if (s.Length == 0) continue;
                if (group.Senses.Contains(s)) continue;
                if (group.Senses.Count >= 6) break;
                group.Senses.Add(s);
            }
        }

        /// <summary>把全角标点归一，有道常返回 "n．" 这种全角句点。</summary>
        private static string NormalizePunct(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
            {
                char ch = c;
                if (ch == '\uFF0E' || ch == '\u2024' || ch == '\uFE52') ch = '.';   // ．  ․  ﹒
                else if (ch == '\uFF1A') ch = ':';
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }
        private static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return string.Empty;
        }
        private static Dictionary<string, object> FirstDict(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return null;
            Dictionary<string, object> one = v as Dictionary<string, object>;
            if (one != null) return one;
            object[] arr = v as object[];
            if (arr != null && arr.Length > 0) return arr[0] as Dictionary<string, object>;
            return null;
        }

        private static Dictionary<string, object> FirstDictOf(object v)
        {
            if (v == null) return null;
            Dictionary<string, object> one = v as Dictionary<string, object>;
            if (one != null) return one;
            object[] arr = v as object[];
            if (arr != null && arr.Length > 0) return arr[0] as Dictionary<string, object>;
            return null;
        }

        private static object[] AsArray(object v)
        {
            if (v == null) return new object[0];
            object[] arr = v as object[];
            if (arr != null) return arr;
            return new object[] { v };
        }

        private void RememberDict(string key, DictEntry value)
        {
            lock (_cache)
            {
                _dictCache[key] = value;
                _dictOrder.AddLast(key);
                while (_dictOrder.Count > 200)
                {
                    string old = _dictOrder.First.Value;
                    _dictOrder.RemoveFirst();
                    _dictCache.Remove(old);
                }
            }
        }

        public void Dispose()
        {
            // HttpClient 是静态共享的，进程结束自然释放
        }
    }
}
