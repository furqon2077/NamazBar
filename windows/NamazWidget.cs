// NamazWidget — виджет Windows (Widgets Board и экран блокировки) с расписанием намаза.
// Отдельный exe внутри MSIX-пакета: COM-сервер, реализующий IWidgetProvider (Windows App SDK).
// Расчёт, города и язык берутся из NamazBar.cs (компилируется вместе), настройки — из
// %AppData%\NamazBar\settings.ini, то есть виджет показывает тот же город и язык, что и панель.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;

namespace NamazBar
{
    // ───────────────────────── COM: фабрика класса ─────────────────────────
    [ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IClassFactory
    {
        [PreserveSig] int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int LockServer(bool fLock);
    }

    [ComVisible(true)]
    class ProviderFactory : IClassFactory
    {
        public int CreateInstance(IntPtr outer, ref Guid riid, out IntPtr ppv)
        {
            ppv = IntPtr.Zero;
            if (outer != IntPtr.Zero) return unchecked((int)0x80040110);   // CLASS_E_NOAGGREGATION
            try
            {
                IntPtr unk = Marshal.GetIUnknownForObject(WidgetProgram.GetProvider());
                try
                {
                    int hr = Marshal.QueryInterface(unk, ref riid, out ppv);
                    if (hr != 0) Store.Log("widget: QueryInterface " + riid + " hr=0x" + hr.ToString("X8"));
                    return hr;
                }
                finally { Marshal.Release(unk); }
            }
            catch (Exception ex) { Store.Log("widget: CreateInstance " + ex); return Marshal.GetHRForException(ex); }
        }
        public int LockServer(bool fLock) { return 0; }
    }

    // ───────────────────────── Провайдер ─────────────────────────
    class WidgetState
    {
        public WidgetSize Size;
        public bool Customizing;
    }

    [ComVisible(true)]
    class Provider : IWidgetProvider, IWidgetProvider2
    {
        readonly object sync = new object();
        readonly Dictionary<string, WidgetState> widgets = new Dictionary<string, WidgetState>();
        static readonly object render = new object();   // Lang.Cur общий — карточки строим по одной
        Timer timer;

        public Provider()
        {
            try
            {
                WidgetInfo[] infos = WidgetManager.GetDefault().GetWidgetInfos();   // null, если виджетов ещё нет
                if (infos != null)
                    foreach (WidgetInfo wi in infos)
                    {
                        WidgetState st = new WidgetState();
                        st.Size = wi.WidgetContext.Size;
                        widgets[wi.WidgetContext.Id] = st;
                    }
            }
            catch (Exception ex) { Store.Log("widget: GetWidgetInfos " + ex.Message); }
            timer = new Timer(delegate { OnTimer(); }, null, Timeout.Infinite, Timeout.Infinite);
            RefreshRegions();
            if (widgets.Count > 0) { UpdateAll(); Schedule(); }
        }

        // Список городов с islom.uz: нужен, если NamazBar на компьютере нет (иначе доступны только 16 встроенных)
        static void RefreshRegions()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    string f = Path.Combine(Store.Dir, "regions.txt");
                    if (File.Exists(f) && (DateTime.Now - File.GetLastWriteTime(f)).TotalDays < 7) return;
                    Store.Download();
                }
                catch (Exception ex) { Store.Log("widget: regions " + ex.Message); }
            });
            t.IsBackground = true; t.Start();
        }

        WidgetState Get(string id, WidgetSize size)
        {
            lock (sync)
            {
                WidgetState st;
                if (!widgets.TryGetValue(id, out st)) widgets[id] = st = new WidgetState();
                st.Size = size;
                return st;
            }
        }

        public void CreateWidget(WidgetContext ctx)
        {
            Get(ctx.Id, ctx.Size);
            Update(ctx.Id);
            Schedule();
        }

        public void DeleteWidget(string widgetId, string customState)
        {
            bool empty;
            lock (sync) { widgets.Remove(widgetId); empty = widgets.Count == 0; }
            if (empty) WidgetProgram.Exit();
        }

        public void OnActionInvoked(WidgetActionInvokedArgs args)
        {
            string id = args.WidgetContext.Id;
            WidgetState st = Get(id, args.WidgetContext.Size);
            if (args.Verb == "open")
            {
                try { System.Diagnostics.Process.Start("https://islom.uz/taqvim"); } catch { }
                return;
            }
            if (args.Verb == "save")
            {
                // настройки общие для всех экземпляров: экран блокировки своего меню «Настроить» может не иметь
                Prefs p = Prefs.Load();
                int rid; if (int.TryParse(Input(args.Data, "city"), out rid)) p.RegionId = rid;
                int lix = Array.IndexOf(Lang.Codes, Input(args.Data, "lang"));
                if (lix >= 0) p.Lang = lix;
                p.Save();
            }
            if (args.Verb == "save" || args.Verb == "cancel")
            {
                lock (sync) st.Customizing = false;
                Update(id);
                UpdateAll();
            }
        }

        public void OnCustomizationRequested(WidgetCustomizationRequestedArgs args)
        {
            WidgetState st = Get(args.WidgetContext.Id, args.WidgetContext.Size);
            lock (sync) st.Customizing = true;
            Update(args.WidgetContext.Id);
        }

        // Значение поля ввода из JSON, который присылает Action.Execute: {"city":"27","lang":"ru"}
        static string Input(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]*)");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }

        public void OnWidgetContextChanged(WidgetContextChangedArgs args)
        {
            Get(args.WidgetContext.Id, args.WidgetContext.Size);
            Update(args.WidgetContext.Id);
        }

        public void Activate(WidgetContext ctx)
        {
            Get(ctx.Id, ctx.Size);
            Update(ctx.Id);
            Schedule();
        }

        public void Deactivate(string widgetId) { }

        // Обновляем в начале каждой минуты — отсчёт «через N мин» остаётся точным
        void Schedule()
        {
            DateTime now = DateTime.Now;
            int ms = (60 - now.Second) * 1000 - now.Millisecond + 300;
            try { timer.Change(ms, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        void OnTimer()
        {
            UpdateAll();
            Schedule();
        }

        void UpdateAll()
        {
            List<string> ids = new List<string>();
            lock (sync)
                foreach (KeyValuePair<string, WidgetState> kv in widgets)
                    if (!kv.Value.Customizing) ids.Add(kv.Key);   // открытую форму настроек не сбрасываем
            foreach (string id in ids) Update(id);
        }

        void Update(string id)
        {
            try
            {
                WidgetSize size; bool customizing;
                lock (sync)
                {
                    WidgetState st;
                    if (!widgets.TryGetValue(id, out st)) return;
                    size = st.Size; customizing = st.Customizing;
                }
                WidgetUpdateRequestOptions o = new WidgetUpdateRequestOptions(id);
                lock (render)
                {
                    Prefs p = Prefs.Load();
                    o.Template = customizing ? Card.Settings(p) : Card.Build((int)size, p);
                }
                o.Data = "{}";
                WidgetManager.GetDefault().UpdateWidget(o);
            }
            catch (Exception ex) { Store.Log("widget: Update " + ex.Message); }
        }
    }

    // ───────────────────────── Настройки виджета ─────────────────────────
    // Приоритет: выбор в форме «Настроить виджет» (widget.ini) → настройки NamazBar (если он установлен)
    // → язык Windows и Ташкент. widget.ini — отдельный файл: запись в settings.ini из пакета
    // создала бы его копию внутри пакета, и виджет перестал бы видеть изменения из NamazBar.
    class Prefs
    {
        public int RegionId, Lang;
        static string File_ { get { return Path.Combine(Store.Dir, "widget.ini"); } }

        public static Prefs Load() { return Parse(null); }

        // custom — настройки строкой "regionId=27;lang=ru" вместо widget.ini (для отладки /card)
        public static Prefs Parse(string custom)
        {
            Store.Load();
            Skin.Load();   // цвета выбранной темы (меню NamazBar → Оформление)
            Prefs p = new Prefs();
            if (!int.TryParse(Store.Get("regionId", "27"), out p.RegionId)) p.RegionId = 27;
            p.Lang = Array.IndexOf(NamazBar.Lang.Codes, Store.Get("lang", SystemLang()));
            if (p.Lang < 0) p.Lang = 2;
            if (custom == null) try { if (File.Exists(File_)) custom = File.ReadAllText(File_, Encoding.UTF8); } catch { }
            foreach (string part in (custom ?? "").Split(';', '\n', '\r'))
            {
                int i = part.IndexOf('=');
                if (i <= 0) continue;
                string k = part.Substring(0, i).Trim(), v = part.Substring(i + 1).Trim();
                int n;
                if (k == "regionId" && int.TryParse(v, out n)) p.RegionId = n;
                else if (k == "lang" && (n = Array.IndexOf(NamazBar.Lang.Codes, v)) >= 0) p.Lang = n;
            }
            return p;
        }

        public void Save()
        {
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(File_, ToString(), Encoding.UTF8); }
            catch (Exception ex) { Store.Log("widget: save prefs " + ex.Message); }
        }

        static string SystemLang()
        {
            CultureInfo c = CultureInfo.CurrentUICulture;
            if (c.TwoLetterISOLanguageName == "uz") return c.Name.IndexOf("Cyrl", StringComparison.OrdinalIgnoreCase) >= 0 ? "uz-cyrl" : "uz-latn";
            if (c.TwoLetterISOLanguageName == "ru") return "ru";
            return "en";
        }

        public override string ToString() { return "regionId=" + RegionId + ";lang=" + NamazBar.Lang.Codes[Lang]; }
    }

    // ───────────────────────── Состояние расписания ─────────────────────────
    class Schedule
    {
        public string City, DateLong;
        public string[] Names, Times;
        public int Cur, Next;              // Cur = -1 до Бомдода (идёт Хуфтон вчерашнего дня)
        public DateTime CurTime, NextTime;
        public string TomorrowFajr, TomorrowSunrise;
        public int MinutesLeft;
        public double Progress;            // доля прошедшего периода 0..1
        public bool Soon { get { return MinutesLeft <= 10; } }

        static readonly string[] cultures = { "uz-Latn-UZ", "uz-Cyrl-UZ", "ru-RU", "en-US" };

        public static Region FindRegion(List<Region> regions, int id)
        {
            foreach (Region r in regions) if (r.Id == id) return r;
            foreach (Region r in regions) if (r.Id == 27) return r;
            return regions[0];
        }

        public static Schedule Now(Prefs p)
        {
            Lang.Cur = p.Lang;
            Region region = FindRegion(Store.LoadRegions(), p.RegionId);

            DateTime now = DateTime.Now;
            DateTime[] today = Astro.Compute(now.Date, region.Lat, region.Lng);
            DateTime[] tomorrow = Astro.Compute(now.Date.AddDays(1), region.Lat, region.Lng);
            Schedule s = new Schedule();
            s.City = Lang.City(region);
            try { s.DateLong = now.ToString("dddd, d MMMM", CultureInfo.GetCultureInfo(cultures[p.Lang])); }
            catch { s.DateLong = now.ToString("dd.MM.yyyy"); }
            if (s.DateLong.Length > 0) s.DateLong = char.ToUpper(s.DateLong[0]) + s.DateLong.Substring(1);
            s.Names = Lang.Names;
            s.Times = new string[6];
            for (int k = 0; k < 6; k++) s.Times[k] = today[k].ToString("HH:mm");
            s.Cur = -1;
            for (int i = 0; i < 6; i++) if (now >= today[i]) s.Cur = i;
            if (s.Cur < 0) { s.CurTime = Astro.Compute(now.Date.AddDays(-1), region.Lat, region.Lng)[5]; s.Next = 0; s.NextTime = today[0]; }
            else if (s.Cur == 5) { s.CurTime = today[5]; s.Next = 0; s.NextTime = tomorrow[0]; }
            else { s.CurTime = today[s.Cur]; s.Next = s.Cur + 1; s.NextTime = today[s.Next]; }
            s.MinutesLeft = (int)Math.Ceiling((s.NextTime - now).TotalMinutes);
            double total = (s.NextTime - s.CurTime).TotalSeconds;
            s.Progress = total > 0 ? Math.Max(0, Math.Min(1, (now - s.CurTime).TotalSeconds / total)) : 0;
            s.TomorrowFajr = tomorrow[0].ToString("HH:mm");
            s.TomorrowSunrise = tomorrow[1].ToString("HH:mm");
            return s;
        }
    }

    // ───────────────────────── Adaptive Card ─────────────────────────
    // Все три размера одной ширины и отличаются высотой: чем больше виджет, тем крупнее время и тем больше подробностей.
    // Время следующего намаза — картинка, нарисованная антиквой (Palatino): текст в Adaptive Cards
    // не бывает крупнее «extraLarge», а картинка растягивается на всю ширину своей колонки.
    static class Card
    {
        static string Q(string s)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static string Text(string text, string extra)
        {
            return "{\"type\":\"TextBlock\",\"text\":" + Q(text) + ",\"wrap\":false" + (extra.Length > 0 ? "," + extra : "") + "}";
        }

        static string Col(string width, string extra, string items)
        {
            return "{\"type\":\"Column\",\"width\":" + Q(width) + (extra.Length > 0 ? "," + extra : "") + ",\"items\":[" + items + "]}";
        }

        static string Cols(string extra, params string[] cols)
        {
            return "{\"type\":\"ColumnSet\"" + (extra.Length > 0 ? "," + extra : "") + ",\"columns\":[" + string.Join(",", cols) + "]}";
        }

        // ── крупное время картинкой: светлый и тёмный вариант, хост показывает подходящий теме ──
        static readonly Dictionary<string, string> images = new Dictionary<string, string>();

        static string TimePng(string text, Color color)
        {
            string key = text + "|" + color.ToArgb();
            string uri;
            if (images.TryGetValue(key, out uri)) return uri;
            if (images.Count > 40) images.Clear();
            Font f = Palette.Serif(100f, FontStyle.Bold);   // антиква, как на панели задач
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddString(text, f.FontFamily, (int)f.Style, 220f, PointF.Empty, StringFormat.GenericTypographic);
                RectangleF b = path.GetBounds();                 // точные границы цифр — без пустых полей шрифта
                int pad = 4;
                using (Bitmap bmp = new Bitmap((int)Math.Ceiling(b.Width) + 2 * pad, (int)Math.Ceiling(b.Height) + 2 * pad, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                using (SolidBrush br = new SolidBrush(color))
                using (MemoryStream ms = new MemoryStream())
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.TranslateTransform(pad - b.X, pad - b.Y);
                    g.FillPath(br, path);
                    bmp.Save(ms, ImageFormat.Png);
                    uri = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                }
            }
            images[key] = uri;
            return uri;
        }

        static string BigTime(Schedule s)
        {
            string t = s.NextTime.ToString("HH:mm");
            // золото на тёмной теме, изумруд на светлой; последние 10 минут — шафран / терракота
            Color dark = s.Soon ? Color.FromArgb(240, 164, 84) : Palette.Gold;
            Color light = s.Soon ? Palette.Terracotta : Palette.Emerald;
            // $host.hostTheme: если хост его не передаёт, остаётся светлый текст (лок-скрин всегда тёмный)
            return "{\"type\":\"Image\",\"url\":" + Q(TimePng(t, dark)) + ",\"size\":\"stretch\",\"altText\":" + Q(t) + ",\"$when\":\"${$host.hostTheme != 'light'}\"}," +
                   "{\"type\":\"Image\",\"url\":" + Q(TimePng(t, light)) + ",\"size\":\"stretch\",\"altText\":" + Q(t) + ",\"$when\":\"${$host.hostTheme == 'light'}\"}";
        }

        static string Countdown(Schedule s, string size)
        {
            return Text(string.Format(Lang.T("left"), Lang.Span(s.MinutesLeft)),
                        "\"size\":" + Q(size) + ",\"wrap\":true,\"spacing\":\"none\"" + (s.Soon ? ",\"color\":\"warning\",\"weight\":\"bolder\"" : ""));
        }

        // Время слева на weight% ширины, справа — какой намаз и сколько осталось
        static string Hero(Schedule s, int weight, string nameSize, string leftSize)
        {
            return Cols("",
                Col(weight.ToString(), "\"verticalContentAlignment\":\"center\"", BigTime(s)),
                Col((100 - weight).ToString(), "\"verticalContentAlignment\":\"center\",\"spacing\":\"large\"",
                    Text(Lang.T("nextWord"), "\"size\":\"small\",\"isSubtle\":true,\"spacing\":\"none\"") + "," +
                    Text(s.Names[s.Next], "\"size\":" + Q(nameSize) + ",\"weight\":\"bolder\",\"spacing\":\"none\",\"wrap\":true") + "," +
                    Countdown(s, leftSize)));
        }

        // Полоска: сколько прошло от текущего намаза до следующего
        static string Bar(Schedule s, bool labels)
        {
            int done = Math.Max(1, Math.Min(99, (int)Math.Round(s.Progress * 100)));
            string bar = Cols("\"spacing\":\"medium\"",
                Col(done.ToString(), "\"style\":" + Q(s.Soon ? "warning" : "good") + ",\"minHeight\":\"6px\"", ""),
                Col((100 - done).ToString(), "\"style\":\"emphasis\",\"minHeight\":\"6px\",\"spacing\":\"none\"", ""));
            if (!labels) return bar;
            string from = (s.Cur < 0 ? s.Names[5] : s.Names[s.Cur]) + " " + s.CurTime.ToString("HH:mm");
            return bar + "," + Cols("\"spacing\":\"small\"",
                Col("stretch", "", Text(from, "\"size\":\"small\",\"isSubtle\":true")),
                Col("auto", "", Text(s.Names[s.Next] + " " + s.NextTime.ToString("HH:mm"), "\"size\":\"small\",\"isSubtle\":true")));
        }

        // текущий — зелёный, следующий — оранжевый, прошедшие — приглушённые
        static string Style(Schedule s, int k)
        {
            if (k == s.Cur) return ",\"color\":\"good\",\"weight\":\"bolder\"";
            if (k == s.Next) return ",\"color\":\"warning\",\"weight\":\"bolder\"";
            if (s.Cur >= 0 && k < s.Cur && s.Next != 0) return ",\"isSubtle\":true";
            return "";
        }

        // Средний: все шесть времён одной строкой
        static string Strip(Schedule s)
        {
            string[] cols = new string[6];
            for (int k = 0; k < 6; k++)
                cols[k] = Col("stretch", "\"spacing\":\"small\"",
                    Text(s.Names[k], "\"size\":\"small\",\"horizontalAlignment\":\"center\",\"spacing\":\"none\"" + (Style(s, k).Length > 0 ? Style(s, k) : ",\"isSubtle\":true")) + "," +
                    Text(s.Times[k], "\"horizontalAlignment\":\"center\",\"spacing\":\"none\"" + Style(s, k)));
            return Cols("\"spacing\":\"large\"", cols);
        }

        // Большой: расписание строками крупным шрифтом
        static string Rows(Schedule s)
        {
            StringBuilder sb = new StringBuilder();
            for (int k = 0; k < 6; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append(Cols("\"spacing\":\"small\"",
                    Col("stretch", "", Text(s.Names[k], "\"size\":\"medium\"" + Style(s, k))),
                    Col("auto", "", Text(s.Times[k], "\"size\":\"medium\"" + Style(s, k)))));
            }
            return sb.ToString();
        }

        // 0 small, 1 medium, 2 large (значения WidgetSize)
        public static string Build(int size, Prefs p)
        {
            Schedule s = Schedule.Now(p);
            string body;
            if (size == 0)
                body = Hero(s, 56, "large", "default");
            else if (size == 1)
                body = Hero(s, 58, "large", "default") + "," + Bar(s, false) + "," + Strip(s);
            else
                body = Text(s.DateLong, "\"isSubtle\":true") + "," +
                       Hero(s, 62, "extraLarge", "medium") + "," +
                       Bar(s, true) + "," +
                       "{\"type\":\"Container\",\"spacing\":\"large\",\"separator\":true,\"items\":[" + Rows(s) + "]}," +
                       Text(W("tomorrow") + " · " + s.Names[0] + " " + s.TomorrowFajr + " · " + s.Names[1] + " " + s.TomorrowSunrise,
                            "\"size\":\"small\",\"isSubtle\":true,\"spacing\":\"large\",\"wrap\":true");
            // заголовок виджета на выбранном языке (иначе Windows показывает DisplayName из манифеста)
            return "{\"type\":\"AdaptiveCard\",\"$schema\":\"http://adaptivecards.io/schemas/adaptive-card.json\",\"version\":\"1.6\"," +
                   "\"header\":" + Q(W("header") + " · " + s.City) + ",\"verticalContentAlignment\":\"center\"," +
                   "\"selectAction\":{\"type\":\"Action.Execute\",\"verb\":\"open\"},\"body\":[" + body + "]}";
        }

        static readonly Dictionary<string, string[]> words = new Dictionary<string, string[]> {
            { "header",   new[] { "Namoz vaqti", "Намоз вақти", "Время намаза", "Prayer times" } },
            { "tomorrow", new[] { "Ertaga", "Эртага", "Завтра", "Tomorrow" } },
            { "title",  new[] { "Vidjet sozlamalari", "Виджет созламалари", "Настройки виджета", "Widget settings" } },
            { "city",   new[] { "Shahar", "Шаҳар", "Город", "City" } },
            { "lang",   new[] { "Til", "Тил", "Язык", "Language" } },
            { "save",   new[] { "Saqlash", "Сақлаш", "Сохранить", "Save" } },
            { "cancel", new[] { "Bekor qilish", "Бекор қилиш", "Отмена", "Cancel" } },
        };
        static string W(string k) { return words[k][Lang.Cur]; }

        // Форма «Настроить виджет»: город и язык
        public static string Settings(Prefs p)
        {
            Lang.Cur = p.Lang;
            List<Region> regions = Store.LoadRegions();
            Region sel = Schedule.FindRegion(regions, p.RegionId);
            List<Region> sorted = new List<Region>(regions);
            sorted.Sort(delegate(Region a, Region b) { return string.Compare(Lang.City(a), Lang.City(b), StringComparison.CurrentCulture); });
            StringBuilder cities = new StringBuilder();
            foreach (Region r in sorted)
            {
                if (cities.Length > 0) cities.Append(',');
                cities.Append("{\"title\":").Append(Q(Lang.City(r))).Append(",\"value\":").Append(Q(r.Id.ToString())).Append('}');
            }
            StringBuilder langs = new StringBuilder();
            for (int i = 0; i < Lang.Titles.Length; i++)
            {
                if (i > 0) langs.Append(',');
                langs.Append("{\"title\":").Append(Q(Lang.Titles[i])).Append(",\"value\":").Append(Q(Lang.Codes[i])).Append('}');
            }
            return "{\"type\":\"AdaptiveCard\",\"$schema\":\"http://adaptivecards.io/schemas/adaptive-card.json\",\"version\":\"1.6\"," +
                   "\"header\":" + Q(W("header")) + ",\"body\":[" +
                   Text(W("title"), "\"weight\":\"bolder\"") + "," +
                   "{\"type\":\"Input.ChoiceSet\",\"id\":\"city\",\"label\":" + Q(W("city")) + ",\"style\":\"compact\",\"value\":" + Q(sel.Id.ToString()) + ",\"choices\":[" + cities + "]}," +
                   "{\"type\":\"Input.ChoiceSet\",\"id\":\"lang\",\"label\":" + Q(W("lang")) + ",\"style\":\"compact\",\"value\":" + Q(Lang.Codes[p.Lang]) + ",\"choices\":[" + langs + "]}," +
                   "{\"type\":\"ActionSet\",\"actions\":[" +
                   "{\"type\":\"Action.Execute\",\"title\":" + Q(W("save")) + ",\"verb\":\"save\",\"style\":\"positive\"}," +
                   "{\"type\":\"Action.Execute\",\"title\":" + Q(W("cancel")) + ",\"verb\":\"cancel\"}]}]}";
        }
    }

    // ───────────────────────── Картинки для пакета (иконки, превью в каталоге виджетов) ─────────────────────────
    static class PackageAssets
    {
        public static void Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            Icon(Path.Combine(dir, "Square44x44Logo.png"), 44);
            Icon(Path.Combine(dir, "Square150x150Logo.png"), 150);
            Icon(Path.Combine(dir, "StoreLogo.png"), 50);
            Icon(Path.Combine(dir, "WidgetIcon.png"), 256);
            Preview(Path.Combine(dir, "Screenshot.png"));
            Ico(Path.Combine(dir, "NamazBar.ico"), new[] { 16, 24, 32, 48, 64, 256 });
        }

        // .ico из PNG-картинок (формат Vista+) — иконка NamazBar.exe
        static void Ico(string path, int[] sizes)
        {
            List<byte[]> pngs = new List<byte[]>();
            string tmp = Path.GetTempFileName();
            foreach (int n in sizes) { Icon(tmp, n); pngs.Add(File.ReadAllBytes(tmp)); }
            File.Delete(tmp);
            using (BinaryWriter w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (byte[] b in pngs) w.Write(b);
            }
        }

        // Изумрудная плитка в золотой рамке: октаграмма (руб-эль-хизб) и полумесяц
        static void Icon(string path, int n)
        {
            using (Bitmap b = new Bitmap(n, n, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                RectangleF r = new RectangleF(n * 0.04f, n * 0.04f, n * 0.92f, n * 0.92f);
                using (GraphicsPath p = Layered.Round(r, n * 0.22f))
                using (LinearGradientBrush lb = new LinearGradientBrush(r, View.Mix(Palette.Emerald, Color.White, 0.12), Palette.EmeraldDark, LinearGradientMode.Vertical))
                {
                    g.FillPath(lb, p);
                    if (n >= 40) using (Pen pen = new Pen(Palette.Gold, Math.Max(1f, n * 0.03f))) g.DrawPath(pen, p);
                    float cx = n / 2f, cy = n / 2f;
                    if (n >= 40)
                        using (GraphicsPath star = new GraphicsPath())
                        {
                            Palette.Star8(star, cx, cy, n * 0.36f);
                            using (Pen pen = new Pen(Color.FromArgb(200, Palette.Gold), Math.Max(1f, n * 0.022f))) g.DrawPath(pen, star);
                        }
                    // полумесяц: золотой круг, поверх — круг цвета фона (со сглаживанием, в отличие от Region)
                    float d = n * 0.42f, x = cx - d * 0.56f, y = cy - d / 2f;
                    using (SolidBrush gb = new SolidBrush(Palette.Gold)) g.FillEllipse(gb, x, y, d, d);
                    g.SetClip(p);
                    g.FillEllipse(lb, x + d * 0.30f, y - d * 0.08f, d * 0.9f, d * 0.9f);
                    g.ResetClip();
                }
                b.Save(path, ImageFormat.Png);
            }
        }

        // Превью для каталога: вид среднего виджета — крупное время, название и отсчёт, полоска и лента из шести времён
        static void Preview(string path)
        {
            int W = 600, H = 300;
            using (Bitmap b = new Bitmap(W, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                RectangleF card = new RectangleF(0, 0, W, H);
                using (GraphicsPath p = Layered.Round(card, 24))
                {
                    using (LinearGradientBrush lb = new LinearGradientBrush(card, Palette.EmeraldDark, Palette.LapisDark, LinearGradientMode.Vertical))
                        g.FillPath(lb, p);
                    g.SetClip(p);
                    Palette.Girih(g, card, 60, Color.FromArgb(18, Palette.Gold), 1.2f);
                    g.ResetClip();
                    using (Pen pen = new Pen(Color.FromArgb(160, Palette.Gold), 2f)) g.DrawPath(pen, p);
                }
                Font big = Palette.Serif(48f, FontStyle.Bold);
                Font name = Fonts.Get("NB Sans Bold", 17f, "Segoe UI", FontStyle.Bold);
                Font small = Fonts.Get("NB Sans", 9.5f, "Segoe UI", FontStyle.Regular);
                Font cell = Fonts.Get("NB Sans Medium", 10.5f, "Segoe UI", FontStyle.Regular);
                string[] names = Lang.Names;
                string[] times = { "04:52", "06:15", "12:28", "16:47", "18:34", "19:52" };
                int cur = 3, next = 4;
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(184, 172, 146)))
                using (SolidBrush ivory = new SolidBrush(Palette.Ivory))
                using (SolidBrush gold = new SolidBrush(Palette.Gold))
                using (SolidBrush jade = new SolidBrush(Palette.Jade))
                using (SolidBrush track = new SolidBrush(Color.FromArgb(60, Palette.Ivory)))
                {
                    g.DrawString(times[next], big, gold, 16, 26);
                    g.DrawString(Lang.T("nextWord"), small, dim, 346, 42);
                    g.DrawString(names[next], name, ivory, 343, 60);
                    g.DrawString(string.Format(Lang.T("left"), Lang.Span(79)), cell, ivory, 346, 96);
                    g.FillRectangle(track, 24, 164, W - 48, 6);
                    g.FillRectangle(jade, 24, 164, (W - 48) * 0.62f, 6);
                    float cw = (W - 48) / 6f;
                    for (int k = 0; k < 6; k++)
                    {
                        Brush br = k == cur ? jade : k == next ? gold : k < cur ? dim : ivory;
                        SizeF ns = g.MeasureString(names[k], small), ts = g.MeasureString(times[k], cell);
                        float cx = 24 + cw * k + cw / 2;
                        g.DrawString(names[k], small, k == cur || k == next ? br : dim, cx - ns.Width / 2, 200);
                        g.DrawString(times[k], cell, br, cx - ts.Width / 2, 222);
                    }
                }
                b.Save(path, ImageFormat.Png);
            }
        }
    }

    // ───────────────────────── Точка входа ─────────────────────────
    static class WidgetProgram
    {
        public static readonly Guid Clsid = new Guid("96405140-8bfb-428c-8e1e-954fff68ae8f");
        static readonly ManualResetEvent exit = new ManualResetEvent(false);
        static readonly object sync = new object();
        static Provider provider;

        [DllImport("ole32.dll")] static extern int CoRegisterClassObject([MarshalAs(UnmanagedType.LPStruct)] Guid clsid,
            [MarshalAs(UnmanagedType.IUnknown)] object unk, uint ctx, uint flags, out uint cookie);
        [DllImport("ole32.dll")] static extern int CoRevokeClassObject(uint cookie);

        public static Provider GetProvider()
        {
            lock (sync) { if (provider == null) provider = new Provider(); return provider; }
        }

        public static void Exit() { exit.Set(); }

        static string CardFor(int size, Prefs p) { return size == 9 ? Card.Settings(p) : Card.Build(size, p); }   // 9 — форма настроек

        [MTAThread]
        static int Main(string[] args)
        {
            int ai = Array.IndexOf(args, "/assets");
            if (ai >= 0 && ai + 1 < args.Length)
            {
                Store.Load();
                int lix = Array.IndexOf(Lang.Codes, Store.Get("lang", "ru"));
                Lang.Cur = lix >= 0 ? lix : 2;
                PackageAssets.Generate(args[ai + 1]);
                return 0;
            }
            int ci = Array.IndexOf(args, "/card");   // отладка: JSON карточки нужного размера в файл
            if (ci >= 0 && ci + 2 < args.Length)
            {
                File.WriteAllText(args[ci + 2], CardFor(int.Parse(args[ci + 1]), Prefs.Parse(args.Length > ci + 3 ? args[ci + 3] : null)), new UTF8Encoding(false));
                return 0;
            }
            if (Array.IndexOf(args, "-RegisterProcessAsComServer") < 0) return 0;   // запуск не от Windows — ничего не делаем

            AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs ea) { Store.Log("widget FATAL " + ea.ExceptionObject); };
            uint cookie;
            int hr = CoRegisterClassObject(Clsid, new ProviderFactory(), 4 /*CLSCTX_LOCAL_SERVER*/, 1 /*REGCLS_MULTIPLEUSE*/, out cookie);
            if (hr != 0) { Store.Log("widget: CoRegisterClassObject hr=0x" + hr.ToString("X8")); return 1; }
            exit.WaitOne();
            CoRevokeClassObject(cookie);
            return 0;
        }
    }
}
