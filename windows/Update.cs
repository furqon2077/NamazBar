// Обновление программы: проверка новых релизов на GitHub, «что нового», загрузка и запуск установщика.
// Релиз = страница GitHub Releases; в нём лежит NamazBar-X.Y.Z-windows.exe (полный установщик).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace NamazBar
{
    static class Updater
    {
        const string Repo = "furqon2077/NamazBar";
        public static event Action Changed;
        public static string LatestTag, Notes, PageUrl, AssetUrl, Error;
        public static long AssetSize;
        public static bool Available, Checking;
        static SynchronizationContext sync;
        static System.Windows.Forms.Timer timer;

        static readonly Dictionary<string, string[]> t = new Dictionary<string, string[]>
        {
            { "updVersion",  new[] { "Versiya {0}", "Версия {0}", "Версия {0}", "Version {0}" } },
            { "updAvail",    new[] { "{0} ga yangilash", "{0} га янгилаш", "Обновить до {0}", "Update to {0}" } },
            { "updCheck",    new[] { "Yangilanishni tekshirish", "Янгиланишни текшириш", "Проверить обновления", "Check for updates" } },
            { "updChecking", new[] { "Tekshirilmoqda…", "Текширилмоқда…", "Проверка…", "Checking…" } },
            { "updUpToDate", new[] { "NamazBar {0} - eng yangi versiya", "NamazBar {0} - энг янги версия", "У вас последняя версия: NamazBar {0}", "NamazBar {0} is the latest version" } },
            { "updNoNet",    new[] { "GitHub bilan aloqa yo'q", "GitHub билан алоқа йўқ", "Не удалось проверить обновления", "Could not check for updates" } },
            { "updTitle",    new[] { "Yangi versiya mavjud", "Янги версия мавжуд", "Доступна новая версия", "A new version is available" } },
            { "updHave",     new[] { "Sizda {0}, yangisi {1}", "Сизда {0}, янгиси {1}", "У вас {0}, новая — {1}", "You have {0}, the new one is {1}" } },
            { "updWhats",    new[] { "Nima yangi", "Нима янги", "Что нового", "What's new" } },
            { "updNow",      new[] { "Yangilash", "Янгилаш", "Обновить", "Update now" } },
            { "updLater",    new[] { "Keyinroq", "Кейинроқ", "Позже", "Later" } },
            { "updLoading",  new[] { "Yuklanmoqda… {0}%", "Юкланмоқда… {0}%", "Загрузка… {0}%", "Downloading… {0}%" } },
            { "updStart",    new[] { "O'rnatuvchi ishga tushmoqda…", "Ўрнатувчи ишга тушмоқда…", "Запускаю установщик…", "Starting the installer…" } },
            { "updPending",  new[] { "{0} versiyasi hozir e'lon qilinmoqda - bir necha daqiqadan keyin qayta urinib ko'ring", "{0} версияси ҳозир эълон қилинмоқда - бир неча дақиқадан кейин қайта уриниб кўринг", "Версия {0} сейчас публикуется - попробуйте через пару минут", "Version {0} is being published right now - try again in a few minutes" } },
            { "updLimit",    new[] { "GitHub vaqtincha cheklov qo'ydi - keyinroq urinib ko'ring", "GitHub вақтинча чеклов қўйди - кейинроқ уриниб кўринг", "GitHub временно ограничил запросы - попробуйте позже", "GitHub is temporarily limiting requests - try again later" } },
            { "updFail",     new[] { "Yangilab bo'lmadi: {0}", "Янгилаб бўлмади: {0}", "Не удалось обновить: {0}", "Update failed: {0}" } },
        };
        public static string T(string k) { return t[k][Lang.Cur]; }

        static Version Parse(string s)
        {
            Version v; s = (s ?? "").Trim().TrimStart('v', 'V');
            return Version.TryParse(s, out v) ? v : null;
        }
        public static Version CurrentVer() { return Parse(Build.Version) ?? new Version(0, 0, 0); }
        static void Fire() { Action a = Changed; if (a != null) a(); }
        static void Post(Action a) { sync.Post(delegate { try { a(); } catch (Exception ex) { Store.Log("update: " + ex); } }, null); }

        // Тихая проверка при запуске (через 20 секунд) и затем не чаще раза в 12 часов; вручную - из меню «Версия»
        public static void Start()
        {
            sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += delegate
            {
                timer.Interval = 3600000;
                long last; long.TryParse(Store.Get("updLast", "0"), out last);
                bool due = (DateTime.UtcNow - new DateTime(last, DateTimeKind.Utc)).TotalHours >= 12;   // не чаще двух раз в сутки
                if (due) Check(false);
            };
            timer.Start();
        }

        public static void Check(bool manual)
        {
            if (Checking) return;
            Checking = true; Error = null; Fire();
            Thread th = new Thread(delegate()
            {
                string err = null; Dictionary<string, object> d = null;
                try { try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } } catch { }
                try { d = FetchApi(); }
                catch (Exception ex)
                {
                    err = Describe(ex);
                    // API без токена пускает ~60 запросов в час с одного адреса (в офисе он общий) - тогда берём то же с обычных страниц
                    try { d = FetchPages(); err = d != null ? null : err; }
                    catch (Exception ex2) { if (Describe(ex2) != null && !(ex2 is WebException && ((WebException)ex2).Response == null)) err = Describe(ex2); }
                }
                Post(delegate { Apply(d, err, manual); });
            });
            th.IsBackground = true; th.Name = "update-check"; th.Start();
        }


        static string Describe(Exception ex)
        {
            WebException we = ex as WebException;
            HttpWebResponse hr = we != null ? we.Response as HttpWebResponse : null;
            if (hr != null)
            {
                int c = (int)hr.StatusCode;
                if (c == 403 || c == 429) return T("updLimit");
                if (c == 404) return "no releases found (404)";
                return "HTTP " + c;
            }
            return ex.Message;
        }

        static HttpWebRequest Req(string url, bool redirect)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "NamazBar/" + Build.Version; r.Timeout = 15000; r.AllowAutoRedirect = redirect;
            return r;
        }
        static string Get(string url)
        {
            HttpWebRequest r = Req(url, true);
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
        }
        static Dictionary<string, object> FetchApi()
        {
            HttpWebRequest r = Req("https://api.github.com/repos/" + Repo + "/releases/latest", true);
            r.Accept = "application/vnd.github+json";
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return J.Parse(sr.ReadToEnd());
        }

        // Без API: /releases/latest перенаправляет на тег; файл проверяем по ссылке загрузки, заметки берём из RELEASE_NOTES.md в этом теге
        static Dictionary<string, object> FetchPages()
        {
            string tag;
            HttpWebRequest r = Req("https://github.com/" + Repo + "/releases/latest", false);
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            {
                string loc = resp.Headers["Location"] ?? "";
                int i = loc.LastIndexOf("/tag/", StringComparison.Ordinal);
                if (i < 0) return null;
                tag = Uri.UnescapeDataString(loc.Substring(i + 5));
            }
            Version v = Parse(tag);
            if (v == null) return null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["tag_name"] = tag;
            d["html_url"] = "https://github.com/" + Repo + "/releases/tag/" + tag;
            string notes = "";
            try { notes = Get("https://raw.githubusercontent.com/" + Repo + "/" + tag + "/RELEASE_NOTES.md"); } catch { }
            d["body"] = notes;
            ArrayList assets = new ArrayList();
            string name = "NamazBar-" + v + "-windows.exe", url = "https://github.com/" + Repo + "/releases/download/" + tag + "/" + name;
            try
            {
                HttpWebRequest h = Req(url, false);
                using (HttpWebResponse hr = (HttpWebResponse)h.GetResponse())
                    if ((int)hr.StatusCode >= 200 && (int)hr.StatusCode < 400) assets.Add(new Dictionary<string, object> { { "name", name }, { "browser_download_url", url }, { "size", 0 } });
            }
            catch (WebException) { }   // файла ещё нет: релиз публикуется
            d["assets"] = assets;
            return d;
        }

        static void Apply(Dictionary<string, object> d, string err, bool manual)
        {
            Checking = false;
            Version lv = d != null ? Parse(J.Str(d, "tag_name")) : null;
            if (err == null && lv == null) err = "bad release data";
            if (err != null)
            {
                Error = err;
                // неудачная попытка тоже считается: повтор не раньше чем через час (иначе общий адрес офиса быстро упирается в лимит GitHub)
                Store.Settings["updLast"] = DateTime.UtcNow.AddHours(-11).Ticks.ToString(); Store.Save();
                Fire();
                if (manual) ChatToast.Info(T("updNoNet") + ": " + err, true);
                return;
            }
            Store.Settings["updLast"] = DateTime.UtcNow.Ticks.ToString(); Store.Save();
            LatestTag = lv.ToString(); PageUrl = J.Str(d, "html_url"); Notes = J.Str(d, "body") ?? "";
            AssetUrl = null; AssetSize = 0;
            foreach (Dictionary<string, object> a in J.List(d, "assets"))
            {
                string n = J.Str(a, "name") ?? "";
                if (n.EndsWith("-windows.exe", StringComparison.OrdinalIgnoreCase)) { AssetUrl = J.Str(a, "browser_download_url"); AssetSize = J.Long(a, "size"); }
            }
            bool newer = lv > CurrentVer();
            Available = newer && AssetUrl != null;
            Fire();
            if (newer && AssetUrl == null)   // тег уже есть, а установщик ещё не загружен: идёт выпуск (Release action)
            {
                if (manual) ChatToast.Info(string.Format(T("updPending"), LatestTag), false);
                return;
            }
            if (Available)
            {
                bool seen = Store.Get("updSeen", "") == LatestTag;
                if (manual || (!seen && Build.Version != "dev")) { Store.Settings["updSeen"] = LatestTag; Store.Save(); UpdateForm.ShowFor(); }
            }
            else if (manual) ChatToast.Info(string.Format(T("updUpToDate"), Build.Version), false);
        }

        // Заметки релиза: раздел на языке программы (## ru / ## en), иначе весь текст; служебные строки GitHub убираем
        public static string NotesText()
        {
            string body = (Notes ?? "").Replace("\r", "");
            string[] codes = { "uz", "uz", "ru", "en" };
            string want = codes[Lang.Cur];
            Dictionary<string, StringBuilder> sec = new Dictionary<string, StringBuilder>();
            StringBuilder cur = null, all = new StringBuilder();
            foreach (string line0 in body.Split('\n'))
            {
                string line = line0.TrimEnd();
                Match m = Regex.Match(line, @"^##\s+(ru|en|uz)\s*$", RegexOptions.IgnoreCase);
                if (m.Success) { cur = new StringBuilder(); sec[m.Groups[1].Value.ToLowerInvariant()] = cur; continue; }
                if (line.StartsWith("**Full Changelog**", StringComparison.OrdinalIgnoreCase)) { cur = null; continue; }
                if (Regex.IsMatch(line, @"^##\s+What'?s Changed", RegexOptions.IgnoreCase)) continue;
                if (cur != null) cur.AppendLine(line); else all.AppendLine(line);
            }
            string pick = null;
            StringBuilder sb;
            if (sec.TryGetValue(want, out sb)) pick = sb.ToString();
            else if (sec.TryGetValue("en", out sb)) pick = sb.ToString();
            else pick = all.ToString();
            pick = Regex.Replace(pick, @"^\s*[\*\-]\s+", "• ", RegexOptions.Multiline);
            pick = Regex.Replace(pick, @"\s+by @\S+ in https?://\S+", "");
            pick = Regex.Replace(pick, @"\n{3,}", "\n\n").Trim();
            return pick.Length == 0 ? PageUrl ?? "" : pick;
        }

        // Скачать установщик во временную папку. progress — 0..100, done(ошибка или null, путь к файлу)
        public static void Download(Action<int> progress, Action<string, string> done)
        {
            string url = AssetUrl; long size = AssetSize; string ver = LatestTag;
            Thread th = new Thread(delegate()
            {
                string err = null, path = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
                    string dir = Path.Combine(Path.GetTempPath(), "NamazBar-update");
                    Directory.CreateDirectory(dir);
                    path = Path.Combine(dir, "NamazBar-" + ver + "-windows.exe");
                    HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                    r.UserAgent = "NamazBar/" + Build.Version; r.Timeout = 30000; r.ReadWriteTimeout = 30000;
                    using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream f = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        long total = resp.ContentLength > 0 ? resp.ContentLength : size, got = 0;
                        byte[] buf = new byte[65536]; int n, lastPct = -1;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            f.Write(buf, 0, n); got += n;
                            int pct = total > 0 ? (int)(got * 100 / total) : 0;
                            if (pct != lastPct) { lastPct = pct; int p2 = pct; Post(delegate { progress(p2); }); }
                        }
                        if (size > 0 && got != size) throw new IOException("incomplete download (" + got + " of " + size + " bytes)");
                    }
                }
                catch (Exception ex) { err = ex.Message; try { if (path != null) File.Delete(path); } catch { } }
                string e2 = err, p3 = path;
                Post(delegate { done(e2, p3); });
            });
            th.IsBackground = true; th.Name = "update-download"; th.Start();
        }
    }

    // Окно «Доступна новая версия»: что нового, «Обновить» / «Позже»; встаёт слева над виджетом, как уведомления чата
    class UpdateForm : Form
    {
        static UpdateForm instance;
        public static void ShowFor()
        {
            if (instance != null && !instance.IsDisposed) { instance.Activate(); return; }
            instance = new UpdateForm(); instance.Show();
        }

        Label lblTitle, lblHave, lblStatus;
        TextBox notes;
        UiButton btnNow, btnLater;
        int pct = -1; bool busy;

        int S(float v) { return Ui.S(v); }

        UpdateForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.Body; DoubleBuffered = true;
            int W = S(380), H = S(330);
            Size = new Size(W, H);

            lblTitle = new Label { Text = Updater.T("updTitle"), Font = Ui.Title, ForeColor = Ui.Text, BackColor = Color.Transparent, AutoSize = false };
            lblHave = new Label { Text = string.Format(Updater.T("updHave"), Build.Version, Updater.LatestTag), Font = Ui.Small, ForeColor = Ui.Dim, BackColor = Color.Transparent, AutoSize = false };
            Label what = new Label { Text = Updater.T("updWhats"), Font = Ui.Caps, ForeColor = Ui.Dim, BackColor = Color.Transparent, AutoSize = false };
            notes = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                BackColor = Ui.Raised, ForeColor = Ui.Text, Font = Ui.Body, Text = Updater.NotesText().Replace("\n", "\r\n"), TabStop = false
            };
            lblStatus = new Label { Text = "", Font = Ui.Small, ForeColor = Ui.Dim, BackColor = Color.Transparent, AutoSize = false, AutoEllipsis = true };
            btnNow = new UiButton(Updater.T("updNow"), UiKind.Primary);
            btnLater = new UiButton(Updater.T("updLater"), UiKind.Secondary);
            btnNow.Click += delegate { StartUpdate(); };
            btnLater.Click += delegate { if (!busy) Close(); };

            int pad = S(16);
            lblTitle.SetBounds(pad, S(14), W - 2 * pad, S(24));
            lblHave.SetBounds(pad, S(38), W - 2 * pad, S(18));
            what.SetBounds(pad, S(66), W - 2 * pad, S(16));
            notes.SetBounds(pad, S(86), W - 2 * pad, H - S(86) - S(86));
            lblStatus.SetBounds(pad, H - S(78), W - 2 * pad, S(18));
            btnNow.SetBounds(W - pad - S(120), H - S(48), S(120), S(34));
            btnLater.SetBounds(W - pad - S(120) - S(8) - S(96), H - S(48), S(96), S(34));
            Controls.AddRange(new Control[] { lblTitle, lblHave, what, notes, lblStatus, btnLater, btnNow });

            Rectangle anchor = Rectangle.Empty;
            try { if (ChatToast.Anchor != null) anchor = ChatToast.Anchor(); } catch { }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea; int x, bottom;
            if (anchor.Width > 0 && anchor.Height > 0)
            {
                wa = Screen.FromRectangle(anchor).WorkingArea;
                x = Math.Max(wa.Left + 8, Math.Min(anchor.Left, wa.Right - W - 8)); bottom = Math.Min(anchor.Top, wa.Bottom) - 8;
            }
            else { x = wa.Right - W - 12; bottom = wa.Bottom - 12; }
            Location = new Point(x, Math.Max(wa.Top + 8, bottom - H));
            try { using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, W, H), S(12))) Region = new System.Drawing.Region(p); } catch { }
            KeyPreview = true;
            KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !busy) Close(); };
            FormClosed += delegate { if (instance == this) instance = null; };
        }

        void StartUpdate()
        {
            if (busy) return;
            busy = true; btnNow.Enabled = false; btnLater.Enabled = false;
            lblStatus.ForeColor = Ui.Dim; lblStatus.Text = string.Format(Updater.T("updLoading"), 0);
            Updater.Download(
                delegate(int p) { pct = p; lblStatus.Text = string.Format(Updater.T("updLoading"), p); Invalidate(); },
                delegate(string err, string path)
                {
                    if (IsDisposed) return;
                    if (err != null)
                    {
                        busy = false; btnNow.Enabled = true; btnLater.Enabled = true; pct = -1; Invalidate();
                        lblStatus.ForeColor = Ui.Danger; lblStatus.Text = string.Format(Updater.T("updFail"), err);
                        return;
                    }
                    lblStatus.Text = Updater.T("updStart");
                    try
                    {
                        System.Diagnostics.Process.Start(path);   // это полный установщик: он сам закроет старую копию и поставит новую
                        Application.Exit();
                    }
                    catch (Exception ex)
                    {
                        busy = false; btnNow.Enabled = true; btnLater.Enabled = true;
                        lblStatus.ForeColor = Ui.Danger; lblStatus.Text = string.Format(Updater.T("updFail"), ex.Message);
                    }
                });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (pct >= 0)
            {
                Rectangle bar = new Rectangle(S(16), Height - S(58), Width - S(32), S(4));
                using (SolidBrush b = new SolidBrush(Ui.Raised)) g.FillRectangle(b, bar);
                using (SolidBrush b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, bar.X, bar.Y, bar.Width * Math.Min(100, pct) / 100, bar.Height);
            }
            using (Pen p = new Pen(Ui.Border, 1f)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            base.OnPaint(e);
        }
    }
}
