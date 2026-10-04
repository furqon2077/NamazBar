// NamazBar — мини-утилита: время намаза в левом углу панели задач Windows.
// Данные: координаты городов с islom.uz (new.islom.uz/api/v1/regions),
// расчёт — тем же алгоритмом и параметрами, что и на сайте islom.uz
// (adhan: угол Фаджр/Иша 15.5°, мазхаб Ханафи, Шом = закат + 4 мин).
// Совместимо с C# 5 / .NET Framework 4.x (компилируется встроенным csc.exe).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("NamazBar")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace NamazBar
{
    // ───────────────────────── Астрономия (порт adhan-js 4.4) ─────────────────────────
    static class Astro
    {
        static double D2R(double d) { return d * Math.PI / 180.0; }
        static double R2D(double r) { return r * 180.0 / Math.PI; }
        static double Norm(double n, double max) { return n - max * Math.Floor(n / max); }
        static double Unwind(double a) { return Norm(a, 360.0); }
        static double QuadShift(double a)
        {
            if (a >= -180 && a <= 180) return a;
            return a - 360 * Math.Round(a / 360, MidpointRounding.AwayFromZero);
        }

        class SolarCoords
        {
            public double Decl, RA, Sidereal;
            public SolarCoords(double jd)
            {
                double T = (jd - 2451545.0) / 36525;
                double L0 = Unwind(280.4664567 + 36000.76983 * T + 0.0003032 * T * T);
                double Lp = Unwind(218.3165 + 481267.8813 * T);
                double Om = Unwind(125.04452 - 1934.136261 * T + 0.0020708 * T * T + T * T * T / 450000);
                double M = Unwind(357.52911 + 35999.05029 * T - 0.0001537 * T * T);
                double Mr = D2R(M);
                double C = (1.914602 - 0.004817 * T - 0.000014 * T * T) * Math.Sin(Mr)
                         + (0.019993 - 0.000101 * T) * Math.Sin(2 * Mr) + 0.000289 * Math.Sin(3 * Mr);
                double O2 = 125.04 - 1934.136 * T;
                double Lambda = D2R(Unwind(L0 + C - 0.00569 - 0.00478 * Math.Sin(D2R(O2))));
                double JD = T * 36525 + 2451545.0;
                double Theta0 = Unwind(280.46061837 + 360.98564736629 * (JD - 2451545)
                                + 0.000387933 * T * T - T * T * T / 38710000);
                double dPsi = -17.2 / 3600 * Math.Sin(D2R(Om)) - 1.32 / 3600 * Math.Sin(2 * D2R(L0))
                            - 0.23 / 3600 * Math.Sin(2 * D2R(Lp)) + 0.21 / 3600 * Math.Sin(2 * D2R(Om));
                double dEps = 9.2 / 3600 * Math.Cos(D2R(Om)) + 0.57 / 3600 * Math.Cos(2 * D2R(L0))
                            + 0.1 / 3600 * Math.Cos(2 * D2R(Lp)) - 0.09 / 3600 * Math.Cos(2 * D2R(Om));
                double Eps0 = 23.439291 - 0.013004167 * T - 0.0000001639 * T * T + 0.0000005036 * T * T * T;
                double EpsApp = D2R(Eps0 + 0.00256 * Math.Cos(D2R(O2)));
                Decl = R2D(Math.Asin(Math.Sin(EpsApp) * Math.Sin(Lambda)));
                RA = Unwind(R2D(Math.Atan2(Math.Cos(EpsApp) * Math.Sin(Lambda), Math.Cos(Lambda))));
                Sidereal = Theta0 + dPsi * 3600 * Math.Cos(D2R(Eps0 + dEps)) / 3600;
            }
        }

        static double Interp(double y2, double y1, double y3, double n)
        {
            double a = y2 - y1, b = y3 - y2, c = b - a;
            return y2 + n / 2 * (a + b + n * c);
        }
        static double InterpAngles(double y2, double y1, double y3, double n)
        {
            double a = Unwind(y2 - y1), b = Unwind(y3 - y2), c = b - a;
            return y2 + n / 2 * (a + b + n * c);
        }
        static double Altitude(double phi, double delta, double H)
        {
            return R2D(Math.Asin(Math.Sin(D2R(phi)) * Math.Sin(D2R(delta))
                + Math.Cos(D2R(phi)) * Math.Cos(D2R(delta)) * Math.Cos(D2R(H))));
        }
        static double JulianDay(int year, int month, int day)
        {
            int Y = month > 2 ? year : year - 1;
            int M = month > 2 ? month : month + 12;
            int A = Y / 100;
            int B = 2 - A + A / 4;
            int i0 = (int)Math.Floor(365.25 * (Y + 4716));
            int i1 = (int)Math.Floor(30.6001 * (M + 1));
            return i0 + i1 + day + B - 1524.5;
        }

        class SolarTime
        {
            SolarCoords s, p, n;
            double lat, lng, m0;
            public double Transit, Sunrise, Sunset;
            public SolarTime(DateTime date, double lat, double lng)
            {
                this.lat = lat; this.lng = lng;
                double jd = JulianDay(date.Year, date.Month, date.Day);
                s = new SolarCoords(jd); p = new SolarCoords(jd - 1); n = new SolarCoords(jd + 1);
                double Lw = -lng;
                m0 = Norm((s.RA + Lw - s.Sidereal) / 360, 1);
                double expected = Norm((12.0 - lng / 15.0) / 24.0, 1);
                if (m0 - expected > 0.5) m0 -= 1.0; else if (expected - m0 > 0.5) m0 += 1.0;
                double Theta = Unwind(s.Sidereal + 360.985647 * m0);
                double a = Unwind(InterpAngles(s.RA, p.RA, n.RA, m0));
                double H = QuadShift(Theta - Lw - a);
                Transit = (m0 + H / -360) * 24;
                Sunrise = HourAngle(-50.0 / 60.0, false);
                Sunset = HourAngle(-50.0 / 60.0, true);
            }
            public double HourAngle(double h0, bool after)
            {
                double Lw = -lng;
                double t1 = Math.Sin(D2R(h0)) - Math.Sin(D2R(lat)) * Math.Sin(D2R(s.Decl));
                double t2 = Math.Cos(D2R(lat)) * Math.Cos(D2R(s.Decl));
                double H0 = R2D(Math.Acos(t1 / t2));
                double m = after ? m0 + H0 / 360 : m0 - H0 / 360;
                double Theta = Unwind(s.Sidereal + 360.985647 * m);
                double a = Unwind(InterpAngles(s.RA, p.RA, n.RA, m));
                double delta = Interp(s.Decl, p.Decl, n.Decl, m);
                double H = Theta - Lw - a;
                double h = Altitude(lat, delta, H);
                double dm = (h - h0) / (360 * Math.Cos(D2R(delta)) * Math.Cos(D2R(lat)) * Math.Sin(D2R(H)));
                return (m + dm) * 24;
            }
            public double Afternoon(double shadow)
            {
                double tangent = Math.Abs(lat - s.Decl);
                double inverse = shadow + Math.Tan(D2R(tangent));
                return HourAngle(R2D(Math.Atan(1.0 / inverse)), true);
            }
        }

        // TimeComponents.utcDate: часы (UTC, дробные) -> DateTime UTC на указанную дату
        static DateTime Utc(DateTime date, double hours)
        {
            if (double.IsNaN(hours)) return DateTime.MinValue;
            int h = (int)Math.Floor(hours);
            int m = (int)Math.Floor((hours - h) * 60);
            int s = (int)Math.Floor((hours - (h + m / 60.0)) * 3600);
            return new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc)
                .AddHours(h).AddMinutes(m).AddSeconds(s);
        }
        static DateTime RoundMin(DateTime d)
        {
            int sec = d.Second;
            DateTime t = new DateTime(d.Year, d.Month, d.Day, d.Hour, d.Minute, d.Second, DateTimeKind.Utc);
            return sec >= 30 ? t.AddSeconds(60 - sec) : t.AddSeconds(-sec);
        }

        // Возвращает локальное время: [0]Бомдод [1]Қуёш [2]Пешин [3]Аср [4]Шом [5]Хуфтон
        public static DateTime[] Compute(DateTime localDate, double lat, double lng)
        {
            const double fajrAngle = 15.5, ishaAngle = 15.5, shadow = 2; // Hanafi
            DateTime date = localDate.Date;
            SolarTime st = new SolarTime(date, lat, lng);
            DateTime tomorrow = date.AddDays(1);
            SolarTime tst = new SolarTime(tomorrow, lat, lng);

            DateTime dhuhr = Utc(date, st.Transit);
            DateTime sunrise = Utc(date, st.Sunrise);
            DateTime sunset = Utc(date, st.Sunset);
            DateTime asr = Utc(date, st.Afternoon(shadow));
            DateTime tSunrise = Utc(tomorrow, tst.Sunrise);
            double night = (tSunrise - sunset).TotalSeconds;

            DateTime fajr = Utc(date, st.HourAngle(-fajrAngle, false));
            DateTime safeFajr = sunrise.AddSeconds(-0.5 * night);
            if (fajr == DateTime.MinValue || safeFajr > fajr) fajr = safeFajr;

            DateTime isha = Utc(date, st.HourAngle(-ishaAngle, true));
            DateTime safeIsha = sunset.AddSeconds(0.5 * night);
            if (isha == DateTime.MinValue || safeIsha < isha) isha = safeIsha;

            DateTime maghrib = RoundMin(sunset);
            return new DateTime[] {
                RoundMin(fajr).ToLocalTime(),
                RoundMin(sunrise).ToLocalTime(),
                RoundMin(dhuhr).ToLocalTime(),
                RoundMin(asr).ToLocalTime(),
                maghrib.AddMinutes(4).ToLocalTime(),   // Шом на islom.uz = закат + 4 мин
                RoundMin(isha).ToLocalTime()
            };
        }
    }

    // ───────────────────────── Данные / настройки ─────────────────────────
    class Region
    {
        public int Id; public string Name, NameRu; public double Lat, Lng; public int Order;
        public override string ToString() { return Name; }
    }

    // Номер версии подставляет CI при релизе (Actions -> Release); в обычной сборке — dev
    static class Build { public const string Version = "dev"; /* @VERSION@ */ }

    static class Store
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NamazBar");
        static string SettingsFile { get { return Path.Combine(Dir, "settings.ini"); } }
        static string RegionsFile { get { return Path.Combine(Dir, "regions.txt"); } }
        public const string RegionsUrl = "https://new.islom.uz/api/v1/regions";

        public static void Log(string msg)
        {
            try { Directory.CreateDirectory(Dir); File.AppendAllText(Path.Combine(Dir, "NamazBar.log"), DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n"); }
            catch { }
        }

        public static Dictionary<string, string> Settings = new Dictionary<string, string>();

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsFile)) return;
                foreach (string line in File.ReadAllLines(SettingsFile, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) Settings[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
        }
        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in Settings) sb.AppendLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(SettingsFile, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
        public static string Get(string k, string def)
        {
            string v; return Settings.TryGetValue(k, out v) ? v : def;
        }

        // Встроенный запасной список (снят с islom.uz), если сети нет при первом запуске
        const string Builtin =
            "27|Тошкент|1|41.300872|69.241813|Ташкент\n1|Андижон|2|40.786215|72.328205\n4|Бухоро|3|39.772300|64.423324\n" +
            "5|Гулистон|4|40.492550|68.777611|Гулистан\n18|Самарқанд|5|39.650650|66.976460\n15|Наманган|6|41.005175|71.643583\n" +
            "14|Навоий|7|40.102403|65.367579|Навои\n9|Жиззах|8|40.122196|67.873275\n16|Нукус|9|42.471325|59.616820\n" +
            "25|Қарши|10|38.830248|65.778764|Карши\n26|Қўқон|11|40.535509|70.937948\n21|Хива|12|41.389141|60.350174\n" +
            "13|Марғилон|13|40.47111|71.72472|Маргилан\n37|Фарғона|99|40.376952|71.793129\n74|Термиз|99|37.258528|67.308346\n" +
            "78|Урганч|99|41.553823|60.620611|Ургенч";

        static List<Region> ParseLines(string text)
        {
            List<Region> list = new List<Region>();
            foreach (string line in text.Split('\n'))
            {
                string[] p = line.Trim().Split('|');
                if (p.Length < 5) continue;
                Region r = new Region();
                if (!int.TryParse(p[0], out r.Id)) continue;
                r.Name = p[1];
                if (p.Length > 5) r.NameRu = p[5];
                int.TryParse(p[2], out r.Order);
                if (!double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out r.Lat)) continue;
                if (!double.TryParse(p[4], NumberStyles.Float, CultureInfo.InvariantCulture, out r.Lng)) continue;
                list.Add(r);
            }
            return list;
        }

        public static List<Region> LoadRegions()
        {
            try { if (File.Exists(RegionsFile)) { List<Region> l = ParseLines(File.ReadAllText(RegionsFile, Encoding.UTF8)); if (l.Count > 0) return l; } }
            catch { }
            return ParseLines(Builtin);
        }

        static string Field(string obj, string key)
        {
            Match m = Regex.Match(obj, "\"" + key + "\"\\s*:\\s*(\"((?:[^\"\\\\]|\\\\.)*)\"|[-+0-9.]+|null)");
            if (!m.Success) return null;
            return m.Groups[2].Success && m.Groups[1].Value.StartsWith("\"") ? Regex.Unescape(m.Groups[2].Value) : m.Groups[1].Value;
        }

        // Скачивает список городов с islom.uz. Бросает исключение при ошибке.
        public static List<Region> Download()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            string json;
            using (WebClient wc = new WebClient())
            {
                wc.Encoding = Encoding.UTF8;
                wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36";
                wc.Headers[HttpRequestHeader.Accept] = "application/json";
                wc.Headers["Origin"] = "https://islom.uz";
                wc.Headers[HttpRequestHeader.Referer] = "https://islom.uz/";
                json = wc.DownloadString(RegionsUrl);
            }
            List<Region> list = new List<Region>();
            foreach (Match m in Regex.Matches(json, "\\{[^{}]*\\}"))
            {
                string o = m.Value;
                string st = Field(o, "status");
                if (st != null && st != "1") continue;
                Region r = new Region();
                if (!int.TryParse(Field(o, "id"), out r.Id)) continue;
                r.Name = (Field(o, "name") ?? "").Trim();
                r.NameRu = (Field(o, "name_ru") ?? "").Trim();
                string ord = Field(o, "order");
                if (!int.TryParse(ord, out r.Order)) r.Order = 999;
                if (!double.TryParse(Field(o, "latitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.Lat)) continue;
                if (!double.TryParse(Field(o, "longitude"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.Lng)) continue;
                if (r.Name.Length == 0) continue;
                list.Add(r);
            }
            if (list.Count == 0) throw new Exception("empty response");
            // главные города (order) вперёд, остальные по алфавиту — как на сайте
            list.Sort(delegate(Region a, Region b)
            {
                int c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture);
            });
            StringBuilder sb = new StringBuilder();
            foreach (Region r in list)
                sb.Append(r.Id).Append('|').Append(r.Name).Append('|').Append(r.Order).Append('|')
                  .Append(r.Lat.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(r.Lng.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append((r.NameRu ?? "").Replace("|", " ")).Append('\n');
            Directory.CreateDirectory(Dir);
            File.WriteAllText(RegionsFile, sb.ToString(), Encoding.UTF8);
            return list;
        }
    }

    // ───────────────────────── Языки ─────────────────────────
    static class Lang
    {
        // 0 = O'zbekcha (lotin), 1 = Ўзбекча (кирилл), 2 = Русский, 3 = English
        public static int Cur = 2;
        public static readonly string[] Titles = { "O'zbekcha (lotin)", "Ўзбекча (кирилл)", "Русский", "English" };
        public static readonly string[] Codes = { "uz-latn", "uz-cyrl", "ru", "en" };

        static readonly string[][] names = {
            new[] { "Bomdod", "Quyosh", "Peshin", "Asr", "Shom", "Xufton" },
            new[] { "Бомдод", "Қуёш", "Пешин", "Аср", "Шом", "Хуфтон" },
            new[] { "Фаджр", "Восход", "Зухр", "Аср", "Магриб", "Иша" },
            new[] { "Fajr", "Sunrise", "Dhuhr", "Asr", "Maghrib", "Isha" } };
        public static string[] Names { get { return names[Cur]; } }

        static readonly Dictionary<string, string[]> t = new Dictionary<string, string[]> {
            { "justNow",  new[] { "hozirgina", "ҳозиргина", "только что", "just now" } },
            { "passed",   new[] { "{0} o'tdi", "{0} ўтди", "прошло {0}", "{0} ago" } },
            { "next",     new[] { "{0} {1} da", "{0} {1} да", "{0} в {1}", "{0} at {1}" } },
            { "min",      new[] { "daq", "дақ", "мин", "min" } },
            { "hour",     new[] { "soat", "соат", "ч", "h" } },
            { "city",     new[] { "Shahar", "Шаҳар", "Город", "City" } },
            { "look",     new[] { "Ko'rinish", "Кўриниш", "Оформление", "Appearance" } },
            { "skin",     new[] { "Rang mavzusi", "Ранг мавзуси", "Цветовая тема", "Color theme" } },
            { "alerts",   new[] { "Bildirishnomalar", "Билдиришномалар", "Уведомления", "Notifications" } },
            { "system",   new[] { "Sozlamalar", "Созламалар", "Настройки", "Settings" } },
            { "other",    new[] { "Boshqa shaharlar", "Бошқа шаҳарлар", "Другие города", "Other cities" } },
            { "lang",     new[] { "Til / Язык / Language", "Тил / Язык / Language", "Язык / Til / Language", "Language / Til / Язык" } },
            { "sync",     new[] { "islom.uz dan yangilash", "islom.uz дан янгилаш", "Обновить с islom.uz", "Update from islom.uz" } },
            { "open",     new[] { "islom.uz ni ochish", "islom.uz ни очиш", "Открыть islom.uz", "Open islom.uz" } },
            { "sound",    new[] { "Namoz vaqti kirganda ovoz", "Намоз вақти кирганда овоз", "Звук при наступлении намаза", "Sound when prayer time begins" } },
            { "test",     new[] { "Yoritishni ko'rsatish (sinov)", "Ёритишни кўрсатиш (синов)", "Показать подсветку (тест)", "Show highlight (test)" } },
            { "autorun",  new[] { "Windows bilan birga ishga tushirish", "Windows билан бирга ишга тушириш", "Запускать вместе с Windows", "Start with Windows" } },
            { "reset",    new[] { "Joylashuvni tiklash", "Жойлашувни тиклаш", "Сбросить позицию", "Reset position" } },
            { "left",     new[] { "yana {0}", "яна {0}", "через {0}", "after {0}" } },
            { "embed",    new[] { "Panelga joylashtirish (tavsiya)", "Панелга жойлаштириш (тавсия)", "Встроить в панель задач (рекомендуется)", "Embed into taskbar (recommended)" } },
            { "ticker",   new[] { "Yuguruvchi satr", "Югурувчи сатр", "Бегущая строка", "Ticker" } },
            { "tEvery",   new[] { "Har ... da ishga tushirish", "Ҳар ... да ишга тушириш", "Запускать каждые", "Run every" } },
            { "tBefore",  new[] { "Jadvaldan oldin pauza", "Жадвалдан олдин пауза", "Пауза перед расписанием", "Pause before schedule" } },
            { "tAfter",   new[] { "Jadvaldan keyin pauza", "Жадвалдан кейин пауза", "Пауза после расписания", "Pause after schedule" } },
            { "tSpeed",   new[] { "Tezlik", "Тезлик", "Скорость", "Speed" } },
            { "tShow",    new[] { "Tabloni ko'rsatish", "Таблони кўрсатиш", "Показывать табло", "Show ticker" } },
            { "off",      new[] { "O'chirilgan", "Ўчирилган", "Выключено", "Off" } },
            { "slow",     new[] { "Sekin", "Секин", "Медленно", "Slow" } },
            { "normal",   new[] { "O'rtacha", "Ўртача", "Обычно", "Normal" } },
            { "fast",     new[] { "Tez", "Тез", "Быстро", "Fast" } },
            { "sec",      new[] { "s", "с", "с", "s" } },
            { "nextWord", new[] { "Keyingi", "Кейинги", "Далее", "Next" } },
            { "showCur",  new[] { "Joriy namozni ko'rsatish", "Жорий намозни кўрсатиш", "Показывать текущий намаз", "Show current prayer for" } },
            { "today",    new[] { "Bugun:", "Бугун:", "Сегодня:", "Today:" } },
            { "exit",     new[] { "Chiqish", "Чиқиш", "Выход", "Exit" } },
            { "synced",   new[] { "islom.uz bilan sinxronlash: ", "islom.uz билан синхронлаш: ", "Синхронизация с islom.uz: ", "Synced with islom.uz: " } },
            { "never",    new[] { "hech qachon", "ҳеч қачон", "никогда", "never" } },
            { "syncFail", new[] { "Yangilab bo'lmadi: ", "Янгилаб бўлмади: ", "Не удалось обновить: ", "Update failed: " } },
        };
        public static string T(string key) { return t[key][Cur]; }

        public static string Span(int mins)
        {
            if (mins < 60) return mins + " " + T("min");
            return (mins / 60) + " " + T("hour") + " " + (mins % 60).ToString("00") + " " + T("min");
        }

        // Названия городов на islom.uz — кириллицей; для латиницы/английского транслитерируем
        public static string City(Region r)
        {
            if (Cur == 1) return r.Name;
            if (Cur == 2) return string.IsNullOrEmpty(r.NameRu) ? r.Name : r.NameRu;
            return ToLatin(r.Name);
        }

        static readonly Dictionary<char, string> map = new Dictionary<char, string> {
            {'а',"a"},{'б',"b"},{'в',"v"},{'г',"g"},{'д',"d"},{'е',"e"},{'ё',"yo"},{'ж',"j"},{'з',"z"},{'и',"i"},
            {'й',"y"},{'к',"k"},{'л',"l"},{'м',"m"},{'н',"n"},{'о',"o"},{'п',"p"},{'р',"r"},{'с',"s"},{'т',"t"},
            {'у',"u"},{'ф',"f"},{'х',"x"},{'ц',"ts"},{'ч',"ch"},{'ш',"sh"},{'щ',"sh"},{'ъ',"'"},{'ь',""},{'ы',"i"},
            {'э',"e"},{'ю',"yu"},{'я',"ya"},{'ў',"o'"},{'қ',"q"},{'ғ',"g'"},{'ҳ',"h"} };
        public static string ToLatin(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i], lc = char.ToLowerInvariant(c);
                string v;
                if (lc == 'е' && (i == 0 || "аеёиоуэюяўъь ".IndexOf(char.ToLowerInvariant(s[i - 1])) >= 0)) v = "ye";
                else if (!map.TryGetValue(lc, out v)) { sb.Append(c); continue; }
                if (c != lc && v.Length > 0) v = char.ToUpperInvariant(v[0]) + v.Substring(1);
                sb.Append(v);
            }
            return sb.ToString();
        }
    }

    // ───────────────────────── Шрифты (Google Sans, вшиты в exe) ─────────────────────────
    static class Fonts
    {
        static System.Drawing.Text.PrivateFontCollection pfc;
        static readonly List<IntPtr> mem = new List<IntPtr>();   // память шрифтов должна жить всё время
        static bool loaded;

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                pfc = new System.Drawing.Text.PrivateFontCollection();
                System.Reflection.Assembly asm = typeof(Fonts).Assembly;
                foreach (string rn in asm.GetManifestResourceNames())
                {
                    if (!rn.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                    using (Stream st = asm.GetManifestResourceStream(rn))
                    {
                        byte[] data = new byte[st.Length];
                        st.Read(data, 0, data.Length);
                        IntPtr p = Marshal.AllocCoTaskMem(data.Length);
                        Marshal.Copy(data, 0, p, data.Length);
                        pfc.AddMemoryFont(p, data.Length);
                        mem.Add(p);
                    }
                }
            }
            catch (Exception ex) { Store.Log("fonts: " + ex.Message); }
        }

        // family: "NB Sans", "NB Sans Medium", "NB Sans Bold", "NB Sans Black"; fallback — Segoe UI
        public static Font Get(string family, float size, string fallback, FontStyle fbStyle)
        {
            Load();
            if (pfc != null)
                foreach (FontFamily f in pfc.Families)
                    if (f.Name == family && f.IsStyleAvailable(FontStyle.Regular))
                        return new Font(f, size, FontStyle.Regular, GraphicsUnit.Point);
            return new Font(fallback, size, fbStyle);
        }
    }

    // ───────────────────────── Палитра и орнамент ─────────────────────────
    // Цвета исламского искусства: изумруд, лазурит, золото, слоновая кость; шафран и терракота — для «скоро».
    static class Palette
    {
        public static Color Emerald = Color.FromArgb(18, 94, 68);
        public static Color EmeraldDark = Color.FromArgb(10, 52, 40);
        public static Color Jade = Color.FromArgb(70, 184, 140);
        public static Color Lapis = Color.FromArgb(30, 56, 110);
        public static Color LapisHi = Color.FromArgb(84, 118, 184);   // светлый отсвет циферблата «до следующего намаза» (зависит от скина)
        public static Color LapisDark = Color.FromArgb(14, 26, 54);
        public static Color Gold = Color.FromArgb(222, 186, 98);
        public static Color GoldDeep = Color.FromArgb(160, 118, 34);
        public static Color Ivory = Color.FromArgb(246, 238, 218);
        public static Color Ink = Color.FromArgb(40, 32, 20);
        public static readonly Color Saffron = Color.FromArgb(214, 132, 38);
        public static readonly Color Terracotta = Color.FromArgb(196, 88, 50);

        // антиква для цифр и заголовков; Palatino есть в любой Windows, запасной — Georgia
        public static Font Serif(float size, FontStyle style)
        {
            foreach (string n in new[] { "Palatino Linotype", "Georgia" })
            {
                Font f = new Font(n, size, style);
                if (f.Name == n) return f;
                f.Dispose();
            }
            return new Font(FontFamily.GenericSerif, size, style);
        }

        // Восьмиконечная звезда (руб-эль-хизб): два квадрата, повёрнутых на 45°
        public static void Star8(GraphicsPath p, float cx, float cy, float r)
        {
            for (int s = 0; s < 2; s++)
            {
                PointF[] pts = new PointF[4];
                for (int k = 0; k < 4; k++)
                {
                    double a = Math.PI / 4 * s + Math.PI / 2 * k;
                    pts[k] = new PointF(cx + (float)(r * Math.Cos(a)), cy + (float)(r * Math.Sin(a)));
                }
                p.AddPolygon(pts);
            }
        }

        // Звёздная решётка (гирих) по прямоугольнику — едва заметный фон
        public static void Girih(Graphics g, RectangleF r, float step, Color c, float width)
        {
            using (GraphicsPath p = new GraphicsPath())
            {
                for (float y = r.Top + step / 2; y < r.Bottom + step / 2; y += step)
                    for (float x = r.Left + step / 2; x < r.Right + step / 2; x += step)
                        Star8(p, x, y, step * 0.34f);
                using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
            }
        }

        // Золотой разделитель: линия со звездой посередине
        public static void Divider(Graphics g, float x1, float x2, float y, float r, Color c)
        {
            float cx = (x1 + x2) / 2;
            using (Pen pen = new Pen(Color.FromArgb(150, c), Math.Max(1f, r / 5)))
            {
                g.DrawLine(pen, x1, y, cx - r * 1.8f, y);
                g.DrawLine(pen, cx + r * 1.8f, y, x2, y);
            }
            using (GraphicsPath p = new GraphicsPath())
            {
                p.FillMode = FillMode.Winding;
                Star8(p, cx, y, r);
                using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            }
        }
    }

    // ───────────────────────── Отрисовка виджета ─────────────────────────
    // Отдельный класс, чтобы вид можно было отрисовать и в картинку (превью).
    class View
    {
        public Font TimeFont, NameFont, SubFont, ChipFont, IconFont;
        public Color Bg, Fg, FgDim, Green, GreenHi, Amber, Orange;
        public bool Light, Transparent;
        public float Dpi = 1f;

        // состояние
        public string Time = "--:--", Name = "", Sub = "", Elapsed = "";
        // Бегущая строка: расписание на сегодня
        public string TodayLabel = "";
        public string[] SchedNames = new string[0], SchedTimes = new string[0];
        public int SchedCur = -1, SchedNext = -1;
        public bool TickerScroll;
        public bool Countdown;         // режим «следующий намаз»: плашка = время следующего, без табло
        public bool ShowTicker = true; // показывать оранжевое табло
        // Ступени упрощения, когда на панели мало места (см. BarForm.PlaceOnTaskbar):
        // 0 всё; 1 без табло; 2 без «Next»/«прошло»; 3 без названия; 4 без кнопки ⟳; 5 узкая плашка; 6 — скрыт
        public const int MaxCompact = 6;
        public int Compact;
        bool TickerOn { get { return ShowTicker && Compact < 1; } }
        bool ExtraOn { get { return Compact < 2; } }
        bool NameOn { get { return Compact < 3; } }
        bool SyncOn { get { return Compact < 4; } }
        bool SlimPill { get { return Compact >= 5; } }
        public string Prefix = "";     // «Next» перед названием в режиме отсчёта
        public float StaticDY;         // сдвиг статичного текста по вертикали (анимация «вниз/сверху»)
        public float StaticAlpha = 1f;
        public float TickerX;          // сдвиг начала расписания от левого края рамки (px)
        public float ScheduleW;        // ширина расписания (px), считается в Measure
        public RectangleF TickerRect;
        public bool Sunrise;          // период «Восход» — жёлтая плашка
        public bool WarnSoon;         // < 10 мин до следующего — жёлтый отсчёт
        public double Progress;       // 0..1 доля прошедшего периода
        public bool Alert;            // только что наступило время намаза
        public double AlertT;         // секунд с начала подсветки
        public bool Hover, HoverSync, Syncing, SyncErr;
        public float SyncAngle;
        // Песочные часы: до следующего намаза меньше HourglassMin минут
        public const double HourglassMin = 15;
        public bool Hourglass;
        public double SandLeft;        // 1 — только начали (15 мин), 0 — время наступило
        public double SandT;           // секунды анимации (падающие песчинки)
        bool HourglassOn { get { return Hourglass && Compact < 5; } }

        public Rectangle SyncRect, PillRect, HourglassRect;

        public View(bool light)
        {
            Light = light;
            // Циферблат — классическая антиква (Palatino), остальное — Google Sans
            TimeFont = Palette.Serif(15f, FontStyle.Bold);
            NameFont = Fonts.Get("NB Sans Bold", 14f, "Segoe UI", FontStyle.Bold);
            SubFont = Fonts.Get("NB Sans", 8.5f, "Segoe UI", FontStyle.Regular);
            ChipFont = Fonts.Get("NB Sans Medium", 9f, "Segoe UI Semibold", FontStyle.Regular);
            IconFont = new Font("Segoe UI Symbol", 10f);
            Fg = light ? Palette.Ink : Palette.Ivory;
            FgDim = light ? Color.FromArgb(112, 98, 76) : Color.FromArgb(184, 172, 146);
            Green = Palette.Emerald;
            GreenHi = Palette.Jade;
            Amber = Palette.Saffron;
            Orange = light ? Palette.GoldDeep : Palette.Gold;   // золотая рамка табло и «следующий»
        }

        // Вёрстка по базовой линии: межстрочные поля шрифта не тратят высоту панели
        static float Em(Graphics g, Font f) { return f.SizeInPoints * g.DpiY / 72f; }
        // Реальные границы буквы «H» (не зависят от метрик шрифта): Asc — от точки вывода до базовой линии
        static readonly Dictionary<string, RectangleF> inkCache = new Dictionary<string, RectangleF>();
        static RectangleF Ink(Graphics g, Font f)
        {
            string key = f.Name + "|" + f.SizeInPoints + "|" + g.DpiY;
            RectangleF r;
            if (inkCache.TryGetValue(key, out r)) return r;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddString("H", f.FontFamily, (int)f.Style, Em(g, f), PointF.Empty, StringFormat.GenericTypographic);
                r = p.GetBounds();
            }
            if (r.Height <= 0) r = new RectangleF(0, Em(g, f) * 0.2f, 1, Em(g, f) * 0.72f);
            inkCache[key] = r;
            return r;
        }
        static float Asc(Graphics g, Font f) { return Ink(g, f).Bottom; }
        static float Cap(Graphics g, Font f) { return Ink(g, f).Height; }
        static float Desc(Graphics g, Font f) { return Em(g, f) * 0.22f; }
        float ChipH(Graphics g) { return Cap(g, ChipFont) + Desc(g, ChipFont) + 2 * ChipPadY; }
        float BlockH(Graphics g) { return Cap(g, NameFont) + S(4) + ChipH(g); }

        float ChipPadX { get { return S(6); } }
        float ChipPadY { get { return S(2.5f); } }

        int S(float v) { return (int)Math.Round(v * Dpi); }
        static SizeF M(Graphics g, string s, Font f) { return g.MeasureString(s, f, 1000, StringFormat.GenericTypographic); }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        // Пульс подсветки: первые 60 с — быстрый и яркий, затем мягкое «дыхание»
        public double Pulse
        {
            get
            {
                if (!Alert) return 0;
                double period = AlertT < 60 ? 0.9 : 2.4;
                double amp = AlertT < 60 ? 1.0 : 0.55;
                return amp * (0.5 - 0.5 * Math.Cos(2 * Math.PI * AlertT / period));
            }
        }

        // maxHeight — доступная высота внутри панели (без её рамки).
        // Одна строка: [ВРЕМЯ] Название  когда наступил  [ бегущая строка ]  ⟳
        public Size Measure(Graphics g, int maxHeight)
        {
            int h = maxHeight;
            SizeF t = M(g, "00:00", TimeFont);
            SizeF i = M(g, "\u27F3", IconFont);
            int pillH = h - S(2);
            int pillW = (int)Math.Ceiling(t.Width) + (SlimPill ? S(10) : S(22));
            PillRect = new Rectangle(SlimPill ? S(1) : S(4), (h - pillH) / 2, pillW, pillH);
            if (HourglassOn)
            {
                int hgH = pillH - S(2), hgW = (int)Math.Round(hgH * 0.58);
                HourglassRect = new Rectangle(PillRect.Right + S(7), (h - hgH) / 2, hgW, hgH);
            }
            else HourglassRect = Rectangle.Empty;
            int left = HourglassOn ? HourglassRect.Right : PillRect.Right;

            float nameW = !NameOn ? -S(10) : M(g, Name, NameFont).Width
                        + (ExtraOn ? S(6) + M(g, Elapsed, ElapsedFont).Width + (Prefix.Length > 0 ? M(g, Prefix, SubFont).Width + S(5) : 0) : 0);
            float x = left + S(10) + nameW + S(12);

            float th = TickerH(g);
            float tw = Math.Max(S(200), M(g, Sub, ChipFont).Width + 2 * ChipPadX);
            TickerRect = new RectangleF(x, (h - th) / 2f, tw, th);
            ScheduleW = ScheduleWidth(g);

            int iw = (int)i.Width + S(6), ih = (int)i.Height + S(4);
            float after = TickerOn ? TickerRect.Right + S(6) : x - S(6);
            if (!SyncOn)
            {
                SyncRect = Rectangle.Empty;
                return new Size((int)Math.Ceiling(Math.Max(left, after - S(6))) + (SlimPill ? S(1) : S(3)), h);
            }
            SyncRect = new Rectangle((int)Math.Ceiling(after), (h - ih) / 2, iw, ih);
            return new Size(SyncRect.Right + S(3), h);
        }

        // в режиме отсчёта «after 1 h 27 min» — главная информация, поэтому крупнее
        Font ElapsedFont { get { return Countdown ? ChipFont : SubFont; } }

        float TickerH(Graphics g) { return Cap(g, ChipFont) + Desc(g, ChipFont) + 2 * S(4.5f); }

        float ScheduleWidth(Graphics g)
        {
            // пробелы в конце строк GDI+ не учитывает — отступы задаём явно
            float w = M(g, TodayLabel, ChipFont).Width + S(8);
            for (int k = 0; k < SchedNames.Length; k++)
                w += M(g, SchedNames[k], ChipFont).Width + S(4) + M(g, SchedTimes[k], NameFontSmall(g)).Width
                   + (k < SchedNames.Length - 1 ? SepW(g) : 0);
            return w;
        }
        float SepW(Graphics g) { return S(9) * 2 + M(g, "·", ChipFont).Width; }
        Font timeSmall;
        Font NameFontSmall(Graphics g)
        {
            if (timeSmall == null) timeSmall = Fonts.Get("NB Sans Bold", ChipFont.SizeInPoints, "Segoe UI", FontStyle.Bold);
            return timeSmall;
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) return p;   // окно ещё не получило размер (OnHandleCreated) — AddArc бросил бы исключение
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Draw(Graphics g, Size size)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (Transparent)
            {
                // прозрачный фон (альфа 1 — чтобы окно ловило мышь), текст — сглаживание без ClearType
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
            }
            else g.Clear(Bg);
            if (PillRect.Width <= 0 || PillRect.Height <= 0) return;   // Measure ещё не вызывался (окно только создаётся)
            double pulse = Pulse;
            StringFormat sf = StringFormat.GenericTypographic;

            // 1. Подложка всего виджета: hover как у кнопок панели, при наступлении — зелёное свечение
            RectangleF all = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
            if (Alert)
            {
                using (GraphicsPath p = Round(all, S(6)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(28 + 60 * pulse), GreenHi)))
                    g.FillPath(b, p);
                using (GraphicsPath p = Round(all, S(6)))
                using (Pen pen = new Pen(Color.FromArgb((int)(60 + 150 * pulse), GreenHi), Math.Max(1f, S(1.2f))))
                    g.DrawPath(pen, p);
            }
            else if (Hover)
            {
                using (GraphicsPath p = Round(all, S(6)))
                using (SolidBrush b = new SolidBrush(Light ? Color.FromArgb(18, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255)))
                    g.FillPath(b, p);
            }

            // 2. Ореол вокруг циферблата. Изумруд — идёт намаз, лазурит — отсчёт до следующего,
            //    шафран — восход или меньше 10 минут до следующего
            Color baseC = Countdown ? (WarnSoon ? Amber : Palette.Lapis) : (Sunrise ? Amber : Green);
            Color hiC = Countdown ? (WarnSoon ? Color.FromArgb(240, 178, 84) : Palette.LapisHi)
                                  : (Sunrise ? Color.FromArgb(240, 178, 84) : GreenHi);
            if (Alert)
            {
                for (int k = 3; k >= 1; k--)
                {
                    RectangleF hr = PillRect; hr.Inflate(k * S(1f), k * S(1f));
                    int a = (int)((40 + 70 * pulse) / k);
                    using (GraphicsPath p = Round(hr, S(6) + k))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, hiC)))
                        g.FillPath(b, p);
                }
            }

            // 3. Циферблат: насыщенная заливка, едва заметная звёздная решётка, двойная золотая рамка, ромбы по краям
            Color top = Mix(Mix(baseC, Color.White, 0.10), hiC, pulse);
            Color bot = Mix(Mix(baseC, Color.Black, 0.28), hiC, pulse * 0.7);
            float rad = S(5);
            using (GraphicsPath p = Round(PillRect, rad))
            {
                using (LinearGradientBrush lb = new LinearGradientBrush(PillRect, top, bot, LinearGradientMode.Vertical))
                    g.FillPath(lb, p);
                if (!SlimPill)
                {
                    GraphicsState gs0 = g.Save();
                    g.SetClip(p);
                    Palette.Girih(g, PillRect, PillRect.Height * 0.62f, Color.FromArgb(34, Palette.Gold), Math.Max(1f, S(0.7f)));
                    g.Restore(gs0);
                }
                using (Pen pen = new Pen(Palette.Gold, Math.Max(1f, S(1.3f)))) g.DrawPath(pen, p);
            }
            if (!SlimPill)
            {
                RectangleF inner = PillRect; inner.Inflate(-S(2.6f), -S(2.6f));
                using (GraphicsPath p = Round(inner, rad - S(2)))
                using (Pen pen = new Pen(Color.FromArgb(120, Palette.Gold), Math.Max(1f, S(0.8f)))) g.DrawPath(pen, p);
                float my = PillRect.Y + PillRect.Height / 2f, d = S(2.6f);
                foreach (float mx in new[] { PillRect.X + S(2.6f), PillRect.Right - S(2.6f) })
                    using (SolidBrush b = new SolidBrush(Palette.Gold))
                        g.FillPolygon(b, new[] { new PointF(mx, my - d * 1.6f), new PointF(mx + d, my), new PointF(mx, my + d * 1.6f), new PointF(mx - d, my) });
            }

            SizeF ts = g.MeasureString(Time, TimeFont, 1000, sf);
            float tx = PillRect.X + (PillRect.Width - ts.Width) / 2f;
            float ty = PillRect.Y + (PillRect.Height - S(3) + Cap(g, TimeFont)) / 2f - Asc(g, TimeFont);
            using (SolidBrush sh = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.DrawString(Time, TimeFont, sh, tx, ty + S(1), sf);
            using (SolidBrush tb = new SolidBrush(Color.FromArgb(250, 238, 204))) g.DrawString(Time, TimeFont, tb, tx, ty, sf);

            // 3b. Песочные часы: сколько осталось до следующего намаза
            if (HourglassOn) DrawHourglass(g, HourglassRect);

            // 4. Название намаза крупно + когда наступил (одна строка, общая базовая линия)
            float colX = (HourglassOn ? HourglassRect.Right : PillRect.Right) + S(10);
            float nameBase = (size.Height + Cap(g, NameFont)) / 2f;
            Color nameC = Alert ? Mix(Fg, Light ? Green : GreenHi, 0.5 + 0.5 * pulse) : Fg;
            if (NameOn && ExtraOn && Prefix.Length > 0)
            {
                using (SolidBrush b = new SolidBrush(FgDim)) g.DrawString(Prefix, SubFont, b, colX, nameBase - Asc(g, SubFont), sf);
                colX += g.MeasureString(Prefix, SubFont, 1000, sf).Width + S(5);
            }
            if (NameOn)
                using (SolidBrush b = new SolidBrush(nameC)) g.DrawString(Name, NameFont, b, colX, nameBase - Asc(g, NameFont), sf);
            float nw = g.MeasureString(Name, NameFont, 1000, sf).Width;
            Color ec = Countdown ? (WarnSoon ? Orange : (Light ? Color.FromArgb(50, 50, 50) : Color.FromArgb(220, 224, 230)))
                                 : (Alert ? Mix(FgDim, Light ? Green : GreenHi, 0.6) : FgDim);
            if (NameOn && ExtraOn)
                using (SolidBrush b = new SolidBrush(ec))
                    g.DrawString(Elapsed, ElapsedFont, b, colX + nw + S(6), nameBase - Asc(g, ElapsedFont), sf);

            // 4b. Бегущая строка в светло-оранжевой рамке
            if (TickerOn)
            {
                RectangleF box = TickerRect;
                using (GraphicsPath p = Round(box, box.Height / 2f))
                {
                    if (WarnSoon)
                        using (SolidBrush fb = new SolidBrush(Color.FromArgb(70, Orange))) g.FillPath(fb, p);
                    using (Pen pen = new Pen(Color.FromArgb(WarnSoon ? 255 : 210, Orange), Math.Max(1f, S(1.2f))))
                        g.DrawPath(pen, p);
                }
                Color tc = WarnSoon ? (Light ? Color.FromArgb(150, 70, 0) : Color.FromArgb(255, 214, 170))
                                    : (Light ? Color.FromArgb(60, 60, 60) : Color.FromArgb(232, 235, 238));
                float baseY = box.Y + (box.Height + Cap(g, ChipFont)) / 2f;
                GraphicsState st = g.Save();
                RectangleF clip = new RectangleF(box.X + ChipPadX * 0.6f, box.Y + S(1.5f), box.Width - ChipPadX * 1.2f, box.Height - S(3));
                g.SetClip(clip);
                if (!TickerScroll)
                {
                    int a = (int)(255 * Math.Max(0f, Math.Min(1f, StaticAlpha)));
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(a, tc)))
                        g.DrawString(Sub, ChipFont, b, box.X + ChipPadX, baseY - Asc(g, ChipFont) + StaticDY, sf);
                }
                else
                {
                    float x = box.X + ChipPadX + TickerX;
                    Font tf = NameFontSmall(g);
                    using (SolidBrush dim = new SolidBrush(FgDim))
                    using (SolidBrush norm = new SolidBrush(tc))
                    using (SolidBrush cur = new SolidBrush(Light ? Green : GreenHi))
                    using (SolidBrush nxt = new SolidBrush(Orange))
                    {
                        g.DrawString(TodayLabel, ChipFont, dim, x, baseY - Asc(g, ChipFont), sf);
                        x += g.MeasureString(TodayLabel, ChipFont, 1000, sf).Width + S(8);
                        for (int k = 0; k < SchedNames.Length; k++)
                        {
                            SolidBrush b = k == SchedCur ? cur : (k == SchedNext ? nxt : (SchedCur >= 0 && k < SchedCur && SchedNext != 0 ? dim : norm));
                            string nm = SchedNames[k];
                            g.DrawString(nm, ChipFont, b, x, baseY - Asc(g, ChipFont), sf);
                            x += g.MeasureString(nm, ChipFont, 1000, sf).Width + S(4);
                            g.DrawString(SchedTimes[k], tf, b, x, baseY - Asc(g, tf), sf);
                            x += g.MeasureString(SchedTimes[k], tf, 1000, sf).Width;
                            if (k < SchedNames.Length - 1)
                            {
                                g.DrawString("·", ChipFont, dim, x + S(9), baseY - Asc(g, ChipFont), sf);
                                x += SepW(g);
                            }
                        }
                    }
                }
                g.Restore(st);
            }

            // 5. Прогресс периода — тонкая полоска внутри плашки, у нижнего края
            {
                float bh = Math.Max(2f, S(1.4f));
                float bx = PillRect.X + S(8), bw = PillRect.Width - S(16), by = PillRect.Bottom - bh - S(4);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(50, Palette.Ivory))) g.FillRectangle(b, bx, by, bw, bh);
                float fw = (float)(bw * Math.Max(0, Math.Min(1, Progress)));
                using (SolidBrush b = new SolidBrush(WarnSoon ? Color.FromArgb(250, 200, 120) : Palette.Gold))
                    g.FillRectangle(b, bx, by, fw, bh);
            }

            // 6. Кнопка синхронизации (крутится во время обновления); на узких ступенях её нет
            if (SyncRect.IsEmpty) return;
            if (HoverSync && !Syncing)
                using (GraphicsPath p = Round(SyncRect, S(5)))
                using (SolidBrush b = new SolidBrush(Light ? Color.FromArgb(25, 0, 0, 0) : Color.FromArgb(35, 255, 255, 255)))
                    g.FillPath(b, p);
            Color ic = Syncing ? GreenHi : (SyncErr ? Amber : (HoverSync ? Fg : FgDim));
            SizeF isz = g.MeasureString("⟳", IconFont, 1000, sf);
            GraphicsState gs = g.Save();
            g.TranslateTransform(SyncRect.X + SyncRect.Width / 2f, SyncRect.Y + SyncRect.Height / 2f);
            if (Syncing) g.RotateTransform(SyncAngle);
            using (SolidBrush b = new SolidBrush(ic)) g.DrawString("⟳", IconFont, b, -isz.Width / 2f, -isz.Height / 2f, sf);
            g.Restore(gs);
        }

        // Песочные часы в золотой оправе. Песок сверху убывает вместе с оставшимся временем, струйка течёт;
        // меньше 5 минут — песок краснеет (терракота), меньше 3 — часы мягко светятся.
        void DrawHourglass(Graphics g, RectangleF r)
        {
            double left = Math.Max(0, Math.Min(1, SandLeft));
            double mins = left * HourglassMin;
            Color sand = mins < 5 ? Mix(Palette.Terracotta, Palette.Saffron, mins / 5) : Mix(Palette.Saffron, Palette.Gold, (mins - 5) / 10);
            float capH = Math.Max(2f, r.Height * 0.09f);
            float gx = r.X + r.Width * 0.14f, gw = r.Width * 0.72f;              // стекло между стойками
            float gTop = r.Y + capH, gBot = r.Bottom - capH, cx = r.X + r.Width / 2f, cy = (gTop + gBot) / 2f;
            float neck = Math.Max(1f, r.Width * 0.05f);

            if (mins < 3)   // свечение: чем ближе, тем чаще «дыхание»
            {
                double beat = 0.5 - 0.5 * Math.Cos(2 * Math.PI * SandT / (0.8 + mins * 0.4));
                RectangleF glow = r; glow.Inflate(S(2), S(1));
                using (GraphicsPath p = Round(glow, S(4)))
                using (SolidBrush b = new SolidBrush(Color.FromArgb((int)(30 + 70 * beat), sand))) g.FillPath(b, p);
            }

            using (GraphicsPath glass = new GraphicsPath())
            {
                glass.AddBezier(gx, gTop, gx, cy - (cy - gTop) * 0.35f, cx - neck * 2, cy - neck * 2, cx - neck, cy);
                glass.AddBezier(cx - neck, cy, cx - neck * 2, cy + neck * 2, gx, cy + (gBot - cy) * 0.35f, gx, gBot);
                glass.AddLine(gx, gBot, gx + gw, gBot);
                glass.AddBezier(gx + gw, gBot, gx + gw, cy + (gBot - cy) * 0.35f, cx + neck * 2, cy + neck * 2, cx + neck, cy);
                glass.AddBezier(cx + neck, cy, cx + neck * 2, cy - neck * 2, gx + gw, cy - (cy - gTop) * 0.35f, gx + gw, gTop);
                glass.CloseFigure();
                using (SolidBrush b = new SolidBrush(Color.FromArgb(Light ? 40 : 34, Palette.Ivory))) g.FillPath(b, glass);

                GraphicsState st = g.Save();
                g.SetClip(glass);
                using (SolidBrush sb = new SolidBrush(sand))
                {
                    // верхняя колба: песок лежит у горлышка, уровень опускается
                    float topH = (float)((cy - gTop) * 0.82 * left);
                    if (topH > 0.5f) g.FillRectangle(sb, gx, cy - topH, gw, topH);
                    // нижняя колба: горка растёт
                    float botH = (float)((gBot - cy) * 0.82 * (1 - left)) + S(1);
                    float sy = gBot - botH;
                    using (GraphicsPath heap = new GraphicsPath())
                    {
                        heap.AddLine(gx, gBot, gx, sy + botH * 0.35f);
                        heap.AddBezier(gx, sy + botH * 0.35f, cx - gw * 0.2f, sy, cx + gw * 0.2f, sy, gx + gw, sy + botH * 0.35f);
                        heap.AddLine(gx + gw, sy + botH * 0.35f, gx + gw, gBot);
                        heap.CloseFigure();
                        g.FillPath(sb, heap);
                    }
                    // струйка и падающие песчинки
                    if (left > 0.002)
                    {
                        using (Pen stream = new Pen(Color.FromArgb(200, sand), Math.Max(1f, S(0.9f)))) g.DrawLine(stream, cx, cy, cx, sy + S(1));
                        float fall = sy - cy;
                        for (int k = 0; k < 3; k++)
                        {
                            double ph = (SandT * 1.6 + k / 3.0) % 1.0;
                            float py = cy + (float)(fall * ph);
                            float gr = Math.Max(1f, S(0.9f));
                            g.FillEllipse(sb, cx - gr, py - gr, gr * 2, gr * 2);
                        }
                    }
                }
                g.Restore(st);
                using (Pen pen = new Pen(Color.FromArgb(200, Palette.Gold), Math.Max(1f, S(0.9f)))) g.DrawPath(pen, glass);
            }
            // оправа: крышки и две точёные стойки
            using (SolidBrush b = new SolidBrush(Palette.Gold))
            {
                g.FillRectangle(b, r.X, r.Y, r.Width, capH);
                g.FillRectangle(b, r.X, r.Bottom - capH, r.Width, capH);
            }
            using (Pen post = new Pen(Palette.GoldDeep, Math.Max(1f, S(1f))))
            {
                g.DrawLine(post, r.X + S(1), r.Y + capH, r.X + S(1), r.Bottom - capH);
                g.DrawLine(post, r.Right - S(1), r.Y + capH, r.Right - S(1), r.Bottom - capH);
            }
        }
    }

    // ───────────────────────── Где начинаются значки панели задач (UI Automation) ─────────────────────────
    // Ищем кнопку «Пуск» Windows 11, чтобы виджет не наезжал на значки, когда их становится много.
    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] class CUIAutomation { }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomationElement
    {
        void SetFocus();
        void GetRuntimeId();
        IUIAutomationElement FindFirst(int scope, IUIAutomationCondition condition);
        void FindAll();
        void FindFirstBuildCache();
        void FindAllBuildCache();
        void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object GetCurrentPropertyValue(int propertyId);
    }

    [ComImport, Guid("30cbe57d-d406-4e22-a58b-d8a0e2df34d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IUIAutomation
    {
        void CompareElements();
        void CompareRuntimeIds();
        void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint();
        void GetFocusedElement();
        void GetRootElementBuildCache();
        void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache();
        void GetFocusedElementBuildCache();
        void CreateTreeWalker();
        void get_ControlViewWalker();
        void get_ContentViewWalker();
        void get_RawViewWalker();
        void get_RawViewCondition();
        void get_ControlViewCondition();
        void get_ContentViewCondition();
        void CreateCacheRequest();
        void CreateTrueCondition();
        void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    static class TaskbarProbe
    {
        const int UIA_BoundingRectangle = 30001, UIA_AutomationId = 30011, TreeScope_Descendants = 4;
        static IUIAutomation uia;
        static IUIAutomationElement startBtn;
        static IntPtr cachedFor = IntPtr.Zero;
        public static volatile int StartLeft = -1;      // экранная X кнопки «Пуск», -1 — неизвестно
        public static event EventHandler Changed;

        public static void Run()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    int left = -1;
                    try { left = Probe(); } catch { startBtn = null; }
                    if (left != StartLeft)
                    {
                        StartLeft = left;
                        EventHandler h = Changed; if (h != null) h(null, EventArgs.Empty);
                    }
                    Thread.Sleep(400);   // «Пуск» сдвигается при каждом открытии программы — следим часто
                }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);

        static int Probe()
        {
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero) return -1;
            if (uia == null) uia = (IUIAutomation)new CUIAutomation();
            if (startBtn == null || cachedFor != tb)
            {
                IUIAutomationElement root = uia.ElementFromHandle(tb);
                IUIAutomationCondition c = uia.CreatePropertyCondition(UIA_AutomationId, "StartButton");
                startBtn = root.FindFirst(TreeScope_Descendants, c);
                cachedFor = tb;
                if (startBtn == null) return -1;
            }
            double[] r = startBtn.GetCurrentPropertyValue(UIA_BoundingRectangle) as double[];
            if (r == null || r.Length < 4 || r[2] <= 0) { startBtn = null; return -1; }
            return (int)r[0];
        }
    }

    // ───────────────────────── Тексты для карточки / перерыва ─────────────────────────
    static class Words
    {
        static readonly Dictionary<string, string[]> t = new Dictionary<string, string[]> {
            { "title",   new[] { "{0} namozi vaqti kirdi", "{0} намози вақти кирди", "Наступило время намаза {0}", "It's time for {0}" } },
            { "ayah",    new[] { "«Albatta, namoz mo'minlarga vaqtlari belgilangan farz bo'lgandir»",
                                 "«Албатта, намоз мўминларга вақтлари белгиланган фарз бўлгандир»",
                                 "«Воистину, намаз предписан верующим в строго определённое время»",
                                 "“Indeed, prayer has been decreed upon the believers at specified times”" } },
            { "ayahRef", new[] { "Niso surasi, 103-oyat", "Нисо сураси, 103-оят", "Сура ан-Ниса, аят 103", "Surah An-Nisa, 4:103" } },
            { "hadith",  new[] { "«Allohga eng suyukli amal — o'z vaqtida o'qilgan namoz»",
                                 "«Аллоҳга энг суюкли амал — ўз вақтида ўқилган намоз»",
                                 "«Самое любимое Аллахом деяние — намаз, совершённый в своё время»",
                                 "“The deed most beloved to Allah is prayer offered at its proper time”" } },
            { "hadRef",  new[] { "Buxoriy, Muslim", "Бухорий, Муслим", "аль-Бухари, Муслим", "Bukhari, Muslim" } },
            { "breakIn", new[] { "{1} daqiqalik tanaffus {0} s dan keyin", "{1} дақиқалик танаффус {0} с дан кейин", "Перерыв {1} мин через {0} с", "{1}-min break in {0} s" } },
            { "in5",     new[] { "5 daqiqadan keyin", "5 дақиқадан кейин", "Через 5 мин", "In 5 min" } },
            { "in10",    new[] { "10 daqiqadan keyin", "10 дақиқадан кейин", "Через 10 мин", "In 10 min" } },
            { "holdSkip",new[] { "Chiqish uchun bosib turing", "Чиқиш учун босиб туринг", "Удерживайте, чтобы выйти", "Hold to exit" } },
            { "breakT",  new[] { "Namoz vaqti", "Намоз вақти", "Время намаза", "Prayer time" } },
            { "breakM",  new[] { "Namoz tanaffusi", "Намоз танаффуси", "Перерыв на намаз", "Prayer break" } },
            { "screen",  new[] { "{0} daqiqa", "{0} дақиқа", "{0} мин", "{0} min" } },
            { "card",    new[] { "Namoz vaqti kirganda xabar", "Намоз вақти кирганда хабар", "Карточка при наступлении намаза", "Card when prayer time begins" } },
        };
        public static string T(string k) { return t[k][Lang.Cur]; }
    }

    // Общие помощники для окон с попиксельной прозрачностью
    static class Layered
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst, ref SZ psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public static void Push(IntPtr hwnd, Bitmap bmp, Point screenPos, byte alpha)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            PT dst; dst.X = screenPos.X; dst.Y = screenPos.Y;
            SZ sz; sz.cx = bmp.Width; sz.cy = bmp.Height;
            PT src; src.X = 0; src.Y = 0;
            BLEND bf; bf.Op = 0; bf.Flags = 0; bf.Alpha = alpha; bf.Format = 1;
            UpdateLayeredWindow(hwnd, screen, ref dst, ref sz, mem, ref src, 0, ref bf, 2);
            SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) return p;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ───────────────────────── Карточка при наступлении намаза ─────────────────────────
    // Изумрудно-лазуритовая карточка в золотой рамке с аятом и хадисом. Если назначен перерыв —
    // отсчёт 60 с и кнопки «Через 5 мин» / «Через 10 мин»; отменить перерыв нельзя.
    class PrayerCard : Form
    {
        readonly string title, time;
        readonly float dpi;
        readonly Rectangle anchor;          // экранный прямоугольник виджета
        readonly bool offerBreak;
        readonly int breakMinutes;
        DateTime shown = DateTime.Now, breakAt;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        Font fTitle, fTime, fAyah, fRef, fBtn;
        Rectangle rClose, rIn5, rIn10;
        bool closing; DateTime closeStart;
        public event EventHandler BreakNow;
        public event Action<int> Postpone;  // перенос перерыва на N минут

        public PrayerCard(string prayerName, string time, Rectangle anchor, float dpi, bool offerBreak, int breakMinutes, int breakDelaySec)
        {
            this.title = string.Format(Words.T("title"), prayerName);
            this.time = time; this.anchor = anchor; this.dpi = dpi; this.offerBreak = offerBreak; this.breakMinutes = breakMinutes;
            breakAt = DateTime.Now.AddSeconds(breakDelaySec);
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            TopMost = true;
            fTitle = Palette.Serif(12f, FontStyle.Bold);
            fTime = Palette.Serif(12f, FontStyle.Bold);
            fAyah = Palette.Serif(10.5f, FontStyle.Italic);
            fRef = Fonts.Get("NB Sans", 8f, "Segoe UI", FontStyle.Regular);
            fBtn = Fonts.Get("NB Sans Medium", 9f, "Segoe UI Semibold", FontStyle.Regular);
            timer.Interval = 30;
            timer.Tick += delegate { Render(); };
        }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80000 /*LAYERED*/ | 0x80 /*TOOL*/ | 0x8 /*TOPMOST*/ | 0x08000000 /*NOACTIVATE*/; return cp; }
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        int S(float v) { return (int)Math.Round(v * dpi); }

        protected override void OnShown(EventArgs e) { base.OnShown(e); shown = DateTime.Now; timer.Start(); Render(); }

        // Держим карточку поверх всех окон (в т.ч. других «поверх всех», появившихся позже)
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        DateTime lastTop = DateTime.MinValue;
        void KeepTop()
        {
            if ((DateTime.Now - lastTop).TotalMilliseconds < 300) return;
            lastTop = DateTime.Now;
            // GW_HWNDPREV = 3: если над нами есть окно — поднимаемся
            if (GetWindow(Handle, 3) != IntPtr.Zero || (DateTime.Now - shown).TotalSeconds < 2)
                SetWindowPos(Handle, new IntPtr(-1) /*TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0040 /*NOSIZE|NOMOVE|NOACTIVATE|SHOW*/);
        }

        public void Dismiss() { if (!closing) { closing = true; closeStart = DateTime.Now; } }

        Size Layout(Graphics g, out float[] ys)
        {
            int pad = S(18);
            float head = g.MeasureString(time, fTime).Width + S(16) + S(12) + g.MeasureString(title, fTitle).Width + (offerBreak ? 0 : S(30));
            int w = Math.Max(Math.Max(anchor.Width, S(400)), (int)Math.Ceiling(head) + 2 * pad);
            int inner = w - 2 * pad;
            ys = new float[7];
            float y = pad;
            ys[0] = y; y += S(34);                                            // заголовок
            ys[6] = y + S(10); y += S(22);                                    // разделитель со звездой
            ys[1] = y; y += g.MeasureString(Words.T("ayah"), fAyah, inner).Height + S(2);
            ys[2] = y; y += g.MeasureString(Words.T("ayahRef"), fRef, inner).Height + S(10);
            ys[3] = y; y += g.MeasureString(Words.T("hadith"), fAyah, inner).Height + S(2);
            ys[4] = y; y += g.MeasureString(Words.T("hadRef"), fRef, inner).Height;
            if (offerBreak) { y += S(14); ys[5] = y; y += S(26) + S(32); }   // строка «Перерыв N мин через M с», под ней кнопки
            y += pad;
            return new Size(w, (int)Math.Ceiling(y));
        }

        void Render()
        {
            double age = (DateTime.Now - shown).TotalSeconds;
            if (!closing && age > 180 && !offerBreak) Dismiss();          // сама закрывается через 3 минуты
            if (offerBreak && !closing && DateTime.Now >= breakAt)
            {
                Dismiss();
                EventHandler h = BreakNow; if (h != null) h(this, EventArgs.Empty);
            }
            double t = closing ? 1 - (DateTime.Now - closeStart).TotalSeconds / 0.25 : Math.Min(1, age / 0.3);
            timer.Interval = (closing || age < 0.4) ? 30 : 250;   // после появления — экономный режим
            if (closing && t <= 0) { timer.Stop(); Close(); return; }
            float ease = (float)(1 - Math.Pow(1 - Math.Max(0, t), 3));
            int shadow;
            using (Bitmap bmp = RenderBitmap(out shadow))
            {
                Point pos = new Point(anchor.Left - shadow, anchor.Top - S(8) - (bmp.Height - 2 * shadow) - shadow + (int)((1 - ease) * S(16)));
                Size = bmp.Size;
                Layered.Push(Handle, bmp, pos, (byte)(255 * Math.Max(0, Math.Min(1, ease))));
                KeepTop();
            }
        }

        // Карточка целиком (с тенью) — и для окна, и для /preview
        public Bitmap RenderBitmap(out int shadow)
        {
            float[] ys; Size sz;
            using (Bitmap probe = new Bitmap(1, 1)) using (Graphics pg = Graphics.FromImage(probe)) sz = Layout(pg, out ys);
            shadow = S(10);
            Bitmap bmp = new Bitmap(sz.Width + 2 * shadow, sz.Height + 2 * shadow, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                // мягкая тень
                for (int i = shadow; i > 0; i -= 2)
                {
                    RectangleF sr = new RectangleF(shadow - i / 2f, shadow - i / 3f, sz.Width + i, sz.Height + i);
                    using (GraphicsPath p = Layered.Round(sr, S(14) + i))
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(10, 0, 0, 0))) g.FillPath(b, p);
                }
                g.TranslateTransform(shadow, shadow);
                RectangleF card = new RectangleF(0, 0, sz.Width, sz.Height);
                using (GraphicsPath p = Layered.Round(card, S(14)))
                {
                    using (LinearGradientBrush lb = new LinearGradientBrush(card, Color.FromArgb(250, Palette.EmeraldDark), Color.FromArgb(250, Palette.LapisDark), LinearGradientMode.Vertical))
                        g.FillPath(lb, p);
                    GraphicsState st = g.Save();
                    g.SetClip(p);
                    Palette.Girih(g, card, S(46), Color.FromArgb(16, Palette.Gold), Math.Max(1f, S(0.8f)));
                    g.Restore(st);
                    using (Pen pen = new Pen(Color.FromArgb(220, Palette.Gold), Math.Max(1f, S(1.4f)))) g.DrawPath(pen, p);
                }
                RectangleF inner = card; inner.Inflate(-S(4), -S(4));
                using (GraphicsPath p = Layered.Round(inner, S(11)))
                using (Pen pen = new Pen(Color.FromArgb(90, Palette.Gold), Math.Max(1f, S(0.8f)))) g.DrawPath(pen, p);

                int pad = S(18), w = sz.Width - 2 * pad;
                // заголовок: изумрудный «циферблат» в золотой рамке + текст
                SizeF ts = g.MeasureString(time, fTime);
                RectangleF pill = new RectangleF(pad, ys[0], ts.Width + S(16), S(34));
                using (GraphicsPath p = Layered.Round(pill, S(6)))
                {
                    using (LinearGradientBrush lb = new LinearGradientBrush(pill, View.Mix(Palette.Emerald, Color.White, 0.1), View.Mix(Palette.Emerald, Color.Black, 0.3), LinearGradientMode.Vertical))
                        g.FillPath(lb, p);
                    using (Pen pen = new Pen(Palette.Gold, Math.Max(1f, S(1.2f)))) g.DrawPath(pen, p);
                }
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(250, 238, 204)))
                    g.DrawString(time, fTime, tb, pill.X + (pill.Width - ts.Width) / 2, pill.Y + (pill.Height - ts.Height) / 2);
                SizeF tt = g.MeasureString(title, fTitle);
                using (SolidBrush ib = new SolidBrush(Palette.Ivory))
                    g.DrawString(title, fTitle, ib, pill.Right + S(12), pill.Y + (pill.Height - tt.Height) / 2);
                if (!offerBreak)
                {
                    rClose = new Rectangle(sz.Width - pad - S(20) + shadow, (int)ys[0] + S(7) + shadow, S(20), S(20));
                    using (Pen xp = new Pen(Color.FromArgb(170, Palette.Ivory), Math.Max(1f, S(1.5f))))
                    {
                        float cx = rClose.X - shadow + S(10), cy = rClose.Y - shadow + S(10), d = S(5);
                        g.DrawLine(xp, cx - d, cy - d, cx + d, cy + d); g.DrawLine(xp, cx - d, cy + d, cx + d, cy - d);
                    }
                }
                else rClose = Rectangle.Empty;
                Palette.Divider(g, pad, sz.Width - pad, ys[6], S(5), Palette.Gold);

                using (SolidBrush light = new SolidBrush(Palette.Ivory))
                using (SolidBrush dim = new SolidBrush(Color.FromArgb(200, Palette.Gold)))
                {
                    g.DrawString(Words.T("ayah"), fAyah, light, new RectangleF(pad, ys[1], w, 1000));
                    g.DrawString(Words.T("ayahRef"), fRef, dim, new RectangleF(pad, ys[2], w, 1000));
                    g.DrawString(Words.T("hadith"), fAyah, light, new RectangleF(pad, ys[3], w, 1000));
                    g.DrawString(Words.T("hadRef"), fRef, dim, new RectangleF(pad, ys[4], w, 1000));
                }
                if (offerBreak)
                {
                    int left = Math.Max(0, (int)Math.Ceiling((breakAt - DateTime.Now).TotalSeconds));
                    string info = string.Format(Words.T("breakIn"), left, breakMinutes);
                    using (SolidBrush ob = new SolidBrush(Palette.Gold))
                        g.DrawString(info, fBtn, ob, pad, ys[5]);
                    float x = sz.Width - pad;
                    rIn10 = Btn(g, Words.T("in10"), ref x, ys[5] + S(26), shadow);
                    rIn5 = Btn(g, Words.T("in5"), ref x, ys[5] + S(26), shadow);
                }
            }
            return bmp;
        }

        Rectangle Btn(Graphics g, string text, ref float right, float y, int shadow)
        {
            SizeF s = g.MeasureString(text, fBtn);
            RectangleF r = new RectangleF(right - s.Width - S(24), y, s.Width + S(24), S(32));
            using (GraphicsPath p = Layered.Round(r, S(16)))
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(70, Palette.Emerald))) g.FillPath(b, p);
                using (Pen pen = new Pen(Palette.Gold, Math.Max(1f, S(1.1f)))) g.DrawPath(pen, p);
            }
            using (SolidBrush tb = new SolidBrush(Palette.Ivory))
                g.DrawString(text, fBtn, tb, r.X + S(12), r.Y + (r.Height - s.Height) / 2);
            right = r.X - S(8);
            return new Rectangle((int)r.X + shadow, (int)r.Y + shadow, (int)r.Width, (int)r.Height);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (offerBreak)
            {
                int m = rIn5.Contains(e.Location) ? 5 : rIn10.Contains(e.Location) ? 10 : 0;
                if (m == 0) return;   // перерыв назначен — закрыть можно только переносом
                Dismiss();
                Action<int> h = Postpone; if (h != null) h(m);
                return;
            }
            Dismiss();
        }
    }

    // ───────────────────────── Экран «Перерыв на намаз» ─────────────────────────
    // Полноэкранное окно поверх всех на N минут. Не блокирует систему на уровне Windows:
    // Ctrl+Alt+Del и Диспетчер задач продолжают работать (так и должно быть — это не вирус).
    class BreakForm : Form
    {
        readonly string prayer, time;
        readonly DateTime until;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        DateTime holdStart = DateTime.MinValue;
        bool allowClose;
        Rectangle holdBtn;
        readonly float dpi;
        Font fBig, fTime, fText, fSmall;
        static readonly List<BreakForm> open = new List<BreakForm>();

        public static void ShowAll(string prayer, string time, int minutes)
        {
            if (open.Count > 0) return;
            DateTime until = DateTime.Now.AddMinutes(minutes);
            foreach (Screen s in Screen.AllScreens)
            {
                BreakForm f = new BreakForm(prayer, time, until, s.Bounds);
                open.Add(f);
                f.Show();
            }
            Chat.OnBreakStart();   // позвать группу на совместный намаз (если включено в чате)
        }
        static void CloseAll()
        {
            foreach (BreakForm f in open.ToArray()) { f.allowClose = true; f.Close(); }
            open.Clear();
        }

        public BreakForm(string prayer, string time, DateTime until, Rectangle bounds)
        {
            this.prayer = prayer; this.time = time; this.until = until;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            Bounds = bounds; TopMost = true; DoubleBuffered = true; KeyPreview = true;
            BackColor = Palette.EmeraldDark;
            using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96f;
            fBig = Palette.Serif(30f, FontStyle.Bold);
            fTime = Palette.Serif(84f, FontStyle.Bold);
            fText = Palette.Serif(17f, FontStyle.Italic);
            fSmall = Fonts.Get("NB Sans", 11f, "Segoe UI", FontStyle.Regular);
            timer.Interval = 50;
            timer.Tick += delegate
            {
                if (DateTime.Now >= until) { CloseAll(); return; }
                if (holdStart != DateTime.MinValue && (DateTime.Now - holdStart).TotalSeconds >= 3) { CloseAll(); return; }
                if (Form.ActiveForm == null || !(Form.ActiveForm is BreakForm)) { TopMost = true; Activate(); }
                Invalidate();
            };
        }
        int S(float v) { return (int)Math.Round(v * dpi); }

        protected override void OnShown(EventArgs e) { base.OnShown(e); timer.Start(); Activate(); }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;   // Alt+F4 не закрывает
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e) { timer.Stop(); base.OnFormClosed(e); }

        protected override void OnPaint(PaintEventArgs e) { PaintTo(e.Graphics, ClientRectangle); }

        // Ночное небо из изумруда и лазурита, звёздная решётка, медальон-октаграмма вокруг таймера
        public void PaintTo(Graphics g, Rectangle r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (LinearGradientBrush lb = new LinearGradientBrush(r, Palette.EmeraldDark, Palette.LapisDark, LinearGradientMode.Vertical))
                g.FillRectangle(lb, r);
            Palette.Girih(g, r, S(96), Color.FromArgb(14, Palette.Gold), Math.Max(1f, S(1f)));
            StringFormat c = new StringFormat(); c.Alignment = StringAlignment.Center;
            string head = Words.T("breakT") + " · " + prayer + " · " + time;
            TimeSpan left = until - DateTime.Now; if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            string clock = ((int)left.TotalMinutes).ToString("00") + ":" + left.Seconds.ToString("00");
            float w = Math.Min(r.Width - S(80), S(900));
            // блоки сверху вниз, всё вместе — по центру экрана над кнопкой выхода
            SizeF hs0 = g.MeasureString(head, fBig, (int)w + S(200));
            SizeF cs = g.MeasureString(clock, fTime);
            float star = cs.Height * 1.0f;                                       // радиус медальона-октаграммы
            float hA = g.MeasureString(Words.T("ayah"), fText, (int)w).Height, hH = g.MeasureString(Words.T("hadith"), fText, (int)w).Height;
            float hRef = g.MeasureString("Ag", fSmall).Height;
            float total = hs0.Height + S(24) + 2 * star + S(24) + S(30) + hA + S(4) + hRef + S(14) + hH + S(4) + hRef;
            float avail = r.Height - S(110) - 2 * S(24);
            if (total > avail)   // не помещается — уменьшаем медальон (но не меньше высоты цифр)
            {
                float shrink = Math.Min((total - avail) / 2, star - cs.Height * 0.62f);
                star -= shrink; total -= 2 * shrink;
            }
            float y = Math.Max(S(24), (r.Height - S(110) - total) / 2);
            using (SolidBrush gold = new SolidBrush(Palette.Gold))
                g.DrawString(head, fBig, gold, new RectangleF(0, y, r.Width, hs0.Height), c);
            y += hs0.Height + S(24);
            float cx = r.Width / 2f, cy = y + star;
            using (GraphicsPath p = new GraphicsPath())
            {
                Palette.Star8(p, cx, cy, star);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(40, Palette.Emerald))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(120, Palette.Gold), Math.Max(1f, S(1.4f)))) g.DrawPath(pen, p);
            }
            using (Pen ring = new Pen(Color.FromArgb(70, Palette.Gold), Math.Max(1f, S(1f))))
                g.DrawEllipse(ring, cx - star * 0.8f, cy - star * 0.8f, star * 1.6f, star * 1.6f);
            using (SolidBrush ib = new SolidBrush(Palette.Ivory))
                g.DrawString(clock, fTime, ib, cx - cs.Width / 2, cy - cs.Height / 2);
            y = cy + star + S(24);
            Palette.Divider(g, cx - S(220), cx + S(220), y + S(8), S(7), Palette.Gold);
            y += S(30);
            using (SolidBrush light = new SolidBrush(Palette.Ivory))
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(210, Palette.Gold)))
            {
                float x = (r.Width - w) / 2;
                g.DrawString(Words.T("ayah"), fText, light, new RectangleF(x, y, w, hA), c); y += hA + S(4);
                g.DrawString(Words.T("ayahRef"), fSmall, dim, new RectangleF(x, y, w, hRef), c); y += hRef + S(14);
                g.DrawString(Words.T("hadith"), fText, light, new RectangleF(x, y, w, hH), c); y += hH + S(4);
                g.DrawString(Words.T("hadRef"), fSmall, dim, new RectangleF(x, y, w, hRef), c);
            }
            // последнее сообщение из группового чата — видно и на заблокированном экране
            string chatLine = Chat.RecentLine();
            if (chatLine != null)
            {
                float cw = Math.Min(r.Width - S(80), S(900));
                using (SolidBrush cb = new SolidBrush(Color.FromArgb(235, Palette.Gold)))
                    g.DrawString(chatLine, fSmall, cb, new RectangleF((r.Width - cw) / 2, r.Height - S(150), cw, S(40)), c);
            }
            // кнопка «удерживайте, чтобы выйти» (на экстренный случай)
            string hs = Words.T("holdSkip");
            SizeF bs = g.MeasureString(hs, fSmall);
            holdBtn = new Rectangle((int)(r.Width / 2 - bs.Width / 2 - S(24)), r.Height - S(100), (int)bs.Width + S(48), S(44));
            using (GraphicsPath p = Layered.Round(holdBtn, S(22)))
            {
                using (Pen pen = new Pen(Color.FromArgb(130, Palette.Gold), S(1.2f))) g.DrawPath(pen, p);
                if (holdStart != DateTime.MinValue)
                {
                    float k = (float)Math.Min(1, (DateTime.Now - holdStart).TotalSeconds / 3);
                    RectangleF fill = new RectangleF(holdBtn.X, holdBtn.Y, holdBtn.Width * k, holdBtn.Height);
                    g.SetClip(p); using (SolidBrush b = new SolidBrush(Color.FromArgb(90, Palette.Gold))) g.FillRectangle(b, fill); g.ResetClip();
                }
            }
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(190, Palette.Ivory)))
                g.DrawString(hs, fSmall, dim, holdBtn.X + (holdBtn.Width - bs.Width) / 2, holdBtn.Y + (holdBtn.Height - bs.Height) / 2);
        }

        protected override void OnMouseDown(MouseEventArgs e) { if (holdBtn.Contains(e.Location)) holdStart = DateTime.Now; base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { holdStart = DateTime.MinValue; base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape && holdStart == DateTime.MinValue) holdStart = DateTime.Now; e.Handled = true; base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) holdStart = DateTime.MinValue; base.OnKeyUp(e); }
    }

    // ───────────────────────── Окно на панели задач ─────────────────────────
    class BarForm : Form
    {
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("psapi.dll")] static extern int EmptyWorkingSet(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;

        const int AlertMinutes = 10;

        List<Region> regions;
        Region region;
        DateTime[] today, tomorrow, yesterday;
        DateTime computedFor = DateTime.MinValue;
        string lastSync = null, syncError = null, lastKey = null;
        volatile bool syncing;
        int offsetX, lastPeriod = int.MinValue;
        DateTime alertStart = DateTime.MinValue, alertUntil = DateTime.MinValue;
        View view;
        System.Windows.Forms.Timer timer, anim;
        ToolTip tip;
        ContextMenuStrip menu;
        ToolStripMenuItem soundItem;
        Point dragStart; int dragOffset0; bool dragging, dragMoved;
        int tick;

        public BarForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;

            Store.Load();
            // убранные настройки: «Расписание на экране блокировки» (заменил виджет) и общий перерыв «break»
            // (теперь break0..break5 — отдельно для каждого намаза)
            if (Store.Settings.Remove("lockscreen") | Store.Settings.Remove("lockFlip") | Store.Settings.Remove("break")) Store.Save();
            if (Store.Get("embed", "1") == "1")
            {
                embedTarget = FindWindow("Shell_TrayWnd", null);
                if (embedTarget != IntPtr.Zero) { TopLevel = false; embedded = true; parentTb = embedTarget; }
            }
            Skin.Load();
            int lix = Array.IndexOf(Lang.Codes, Store.Get("lang", "ru"));
            Lang.Cur = lix >= 0 ? lix : 2;
            regions = Store.LoadRegions();
            SelectRegion(int.Parse(Store.Get("regionId", "27")), false);

            ApplyTheme();
            offsetX = int.Parse(Store.Get("offsetX", DefaultOffset().ToString()));

            tip = new ToolTip(); tip.InitialDelay = 400; tip.AutoPopDelay = 30000;
            BuildMenu();
            ChatHooks.Init(delegate { return WidgetScreenRect(); });
            Chat.PrayerStarts = delegate   // чат стирает историю через 20 минут после каждого намаза (кроме восхода)
            {
                List<DateTime> l = new List<DateTime>();
                foreach (DateTime[] day in new[] { yesterday, today })
                    if (day != null) for (int i = 0; i < day.Length; i++) if (i != 1) l.Add(day[i]);
                return l;
            };

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1000;
            timer.Tick += delegate { OnTick(); };
            timer.Start();

            anim = new System.Windows.Forms.Timer();
            anim.Interval = 40;
            anim.Tick += delegate { OnAnim(); };

            Recalc();
            PlaceOnTaskbar();
            TaskbarProbe.Changed += delegate { try { if (IsHandleCreated) BeginInvoke((MethodInvoker)delegate { if (!dragging) { PlaceOnTaskbar(); Redraw(); } }); } catch { } };
            TaskbarProbe.Run();
            view.Bg = SampleTaskbarColor(view.Bg);

            DateTime ls;
            if (!DateTime.TryParse(Store.Get("lastSync", ""), CultureInfo.InvariantCulture, DateTimeStyles.None, out ls)
                || (DateTime.Now - ls).TotalDays > 7)
                StartSync();
            else lastSync = ls.ToString("dd.MM HH:mm");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                if (embedTarget != IntPtr.Zero)
                {
                    // Сразу создаём окно дочерним для панели задач
                    cp.Parent = embedTarget;
                    cp.Style = 0x40000000 /*CHILD*/ | 0x04000000 /*CLIPSIBLINGS*/;
                    // LAYERED: у дочернего окна своя поверхность в DWM — иначе XAML-панель Windows 11 его перекрывает
                    cp.ExStyle = 0x08000000 /*NOACTIVATE*/ | 0x00080000 /*LAYERED*/;
                }
                else cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/ | 0x8 /*TOPMOST*/;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation { get { return true; } }

        // Отступ слева = отступу часов от правого края панели (~18 px при 100%)
        int DefaultOffset() { return (int)Math.Round(14 * view.Dpi); }   // +4 px внутреннего поля до плашки

        void SelectRegion(int id, bool save)
        {
            region = null;
            foreach (Region r in regions) if (r.Id == id) { region = r; break; }
            if (region == null && regions.Count > 0) region = regions[0];
            if (save && region != null) { Store.Settings["regionId"] = region.Id.ToString(); Store.Save(); }
            computedFor = DateTime.MinValue;
        }

        void ApplyTheme()
        {
            bool light = false;
            try
            {
                object v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
                light = v is int && (int)v == 1;
            }
            catch { }
            View old = view;
            view = new View(light);
            using (Graphics g = CreateGraphics()) view.Dpi = g.DpiX / 96f;
            view.Bg = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28);
            if (old != null)
            {
                view.TodayLabel = old.TodayLabel; view.SchedNames = old.SchedNames; view.SchedTimes = old.SchedTimes; view.SchedCur = old.SchedCur; view.SchedNext = old.SchedNext;
                view.Time = old.Time; view.Name = old.Name; view.Elapsed = old.Elapsed; view.Sub = old.Sub; view.Sunrise = old.Sunrise;
                view.WarnSoon = old.WarnSoon; view.Progress = old.Progress; view.Alert = old.Alert;
                view.Countdown = old.Countdown; view.ShowTicker = old.ShowTicker; view.Prefix = old.Prefix;
                view.Hourglass = old.Hourglass; view.SandLeft = old.SandLeft;
                view.Bg = SampleTaskbarColor(view.Bg);
            }
        }

        Color SampleTaskbarColor(Color fallback)
        {
            try
            {
                RECT r; IntPtr tb = FindWindow("Shell_TrayWnd", null);
                if (tb == IntPtr.Zero || !GetWindowRect(tb, out r)) return fallback;
                int x = r.L + offsetX + Math.Max(Width, 300) + 14, y = r.T + (r.B - r.T) / 2;
                using (Bitmap b = new Bitmap(1, 1))
                {
                    using (Graphics g = Graphics.FromImage(b)) g.CopyFromScreen(x, y, 0, 0, new Size(1, 1));
                    Color c = b.GetPixel(0, 0);
                    return Color.FromArgb(c.R, c.G, c.B);
                }
            }
            catch { return fallback; }
        }

        void PlaceOnTaskbar()
        {
            RECT r; IntPtr tb = FindWindow("Shell_TrayWnd", null);
            Rectangle bar;
            if (tb != IntPtr.Zero && GetWindowRect(tb, out r)) bar = Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
            else { Rectangle s = Screen.PrimaryScreen.Bounds; bar = new Rectangle(s.Left, s.Bottom - 48, s.Width, 48); }
            Size sz = Size.Empty;
            int margin = Math.Max(3, (int)Math.Round(5 * view.Dpi));   // не заезжаем на рамку панели
            // Не наезжаем на кнопку «Пуск» (на маленьких экранах она быстро уезжает влево, когда открыто много программ):
            // на каждой ступени сначала пробуем своё место, потом сдвиг левее (до края панели), и лишь затем
            // упрощаем виджет. Возврат к более полной ступени — только с запасом места, чтобы виджет не мигал.
            int startLeft = TaskbarProbe.StartLeft;
            int limit = startLeft > bar.Left + offsetX ? startLeft - (int)Math.Round(10 * view.Dpi) : int.MaxValue;
            int minX = bar.Left + (int)Math.Round(4 * view.Dpi);
            int slack = (int)Math.Round(24 * view.Dpi);
            int x = bar.Left + offsetX, level;
            using (Graphics g = CreateGraphics())
            {
                for (level = 0; level < View.MaxCompact; level++)
                {
                    view.Compact = level;
                    sz = view.Measure(g, bar.Height - 2 * margin);
                    int room = limit - (level < placedLevel ? slack : 0);
                    if (limit == int.MaxValue || bar.Left + offsetX + sz.Width <= room) { x = bar.Left + offsetX; break; }
                    if (minX + sz.Width <= room) { x = Math.Max(minX, room - sz.Width); break; }
                }
            }
            placedLevel = level;
            // места нет даже для одного времени — прячемся, пока «Пуск» не освободит место
            bool hide = level >= View.MaxCompact;
            bool wasHidden = hiddenForSpace;
            hiddenForSpace = hide;
            if (hide) { if (Visible) Hide(); return; }
            if (wasHidden && IsHandleCreated) Show();
            Rectangle nb = new Rectangle(x, bar.Top + (bar.Height - sz.Height) / 2 + 1, sz.Width, sz.Height);
            screenRect = nb;
            if (embedded)
            {
                // координаты относительно панели задач
                POINT pt; pt.X = nb.X; pt.Y = nb.Y; ScreenToClient(parentTb, ref pt);
                Rectangle cb = new Rectangle(pt.X, pt.Y, nb.Width, nb.Height);
                if (cb != Bounds) { Bounds = cb; RenderLayered(); }
            }
            else if (nb != Bounds) Bounds = nb;
        }

        // ── Встраивание в панель задач ──
        // Виджет становится дочерним окном панели задач: он всегда над ней (и при открытом «Пуске»),
        // сам прячется вместе с панелью в полноэкранных приложениях.
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int val);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr h, ref POINT p);
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        static readonly IntPtr HWND_TOP = IntPtr.Zero;
        bool embedded; IntPtr parentTb = IntPtr.Zero, embedTarget = IntPtr.Zero;
        Rectangle screenRect;   // где виджет на экране (для карточки)
        int placedLevel;        // ступень упрощения, выбранная при последнем размещении
        // первый показ окна (Application.Run) идёт после размещения — не даём ему отменить скрытие
        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(value && !hiddenForSpace); }
        bool hiddenForSpace;    // скрыт: на панели нет места даже для времени

        void TryEmbed()
        {
            if (Store.Get("embed", "1") != "1") return;
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            if (tb == IntPtr.Zero) return;
            const int GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000), WS_CLIPSIBLINGS = 0x04000000;
            int st = GetWindowLong(Handle, GWL_STYLE);
            SetWindowLong(Handle, GWL_STYLE, (st & ~WS_POPUP) | WS_CHILD | WS_CLIPSIBLINGS);
            IntPtr prev = SetParent(Handle, tb);
            if (prev != IntPtr.Zero || GetLastParentOk(tb))
            {
                embedded = true; parentTb = tb;
            }
            else SetWindowLong(Handle, GWL_STYLE, st);   // не вышло — работаем как раньше (поверх всех окон)
        }
        [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
        bool GetLastParentOk(IntPtr tb) { return GetParent(Handle) == tb; }

        // Explorer перезапустился — панель пересоздана, наше окно ушло вместе с ней. Перезапускаемся.
        void CheckTaskbarAlive()
        {
            if (!embedded) return;
            IntPtr tb = FindWindow("Shell_TrayWnd", null);
            if (IsWindow(parentTb) && tb == parentTb) return;
            try { System.Diagnostics.Process.Start(Application.ExecutablePath, "/restart"); } catch { }
            Environment.Exit(0);
        }

        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); PlaceOnTaskbar(); KeepOnTop();
        }
        void Diag(string tag)
        {
            RECT r; GetWindowRect(Handle, out r);
            RECT t; GetWindowRect(FindWindow("Shell_TrayWnd", null), out t);
            POINT c; c.X = (r.L + r.R) / 2; c.Y = (r.T + r.B) / 2;
            IntPtr w = WindowFromPoint(c);
            StringBuilder cls = new StringBuilder(128); GetClassName(w, cls, 128);
            Store.Log(tag + ": embedded=" + embedded + " rect=" + r.L + "," + r.T + "," + r.R + "," + r.B
                + " taskbar=" + t.L + "," + t.T + "," + t.R + "," + t.B + " visible=" + IsWindowVisible(Handle)
                + " fromPoint=" + w + "(" + cls + ") self=" + Handle);
        }

        // Периоды: -1 = Иша (до Фаджра), 0..5 = индекс в today
        void Recalc()
        {
            DateTime now = DateTime.Now;
            if (computedFor != now.Date && region != null)
            {
                today = Astro.Compute(now.Date, region.Lat, region.Lng);
                tomorrow = Astro.Compute(now.Date.AddDays(1), region.Lat, region.Lng);
                yesterday = Astro.Compute(now.Date.AddDays(-1), region.Lat, region.Lng);
                computedFor = now.Date;
                UpdateTooltip();
            }
            if (today == null) return;

            int cur = -1;
            for (int i = 0; i < 6; i++) if (now >= today[i]) cur = i;
            int nameIdx; DateTime curTime, nextTime; int nextIdx;
            if (cur < 0) { nameIdx = 5; curTime = yesterday[5]; nextIdx = 0; nextTime = today[0]; }
            else if (cur == 5) { nameIdx = 5; curTime = today[5]; nextIdx = 0; nextTime = tomorrow[0]; }
            else { nameIdx = cur; curTime = today[cur]; nextIdx = cur + 1; nextTime = today[cur + 1]; }

            // Наступление нового периода → подсветка (кроме «Восхода» и смены суток после Иша)
            int period = cur;
            if (lastPeriod != int.MinValue && period != lastPeriod && !(lastPeriod == 5 && period == -1) && nameIdx != 1)
            {
                StartAlert(TimeSpan.FromMinutes(AlertMinutes), true);
                pendingPrayer = new KeyValuePair<int, DateTime>(nameIdx, curTime);   // карточку покажем после пересчёта размеров
            }
            // Запуск программы в первые минуты намаза — тоже подсветим и покажем карточку (без перерыва)
            if (lastPeriod == int.MinValue && nameIdx != 1 && (now - curTime).TotalMinutes < AlertMinutes && now >= curTime)
            {
                StartAlert(curTime.AddMinutes(AlertMinutes) - now, false);
                startupPrayer = new KeyValuePair<int, DateTime>(nameIdx, curTime);
            }
            lastPeriod = period;

            TimeSpan left = nextTime - now;
            view.Time = curTime.ToString("HH:mm");
            view.Name = Lang.Names[nameIdx];
            TimeSpan passed = now - curTime;
            view.Elapsed = passed.TotalMinutes < 1 ? Lang.T("justNow") : string.Format(Lang.T("passed"), Lang.Span((int)Math.Floor(passed.TotalMinutes)));
            view.TodayLabel = Lang.T("today");
            view.SchedNames = Lang.Names;
            view.SchedTimes = new string[6];
            for (int k = 0; k < 6; k++) view.SchedTimes[k] = today[k].ToString("HH:mm");
            view.SchedCur = cur;
            view.SchedNext = nextIdx;
            view.Sub = string.Format(Lang.T("next"), Lang.Names[nextIdx], nextTime.ToString("HH:mm")) + " · " + string.Format(Lang.T("left"), Lang.Span((int)Math.Ceiling(left.TotalMinutes)));
            view.Sunrise = nameIdx == 1;
            view.WarnSoon = left.TotalMinutes <= 10;
            // песочные часы — последние 15 минут перед следующим намазом (анимация идёт в OnAnim)
            view.Hourglass = left.TotalMinutes <= View.HourglassMin && left.TotalSeconds > 0;
            view.SandLeft = left.TotalMinutes / View.HourglassMin;
            if (view.Hourglass && !anim.Enabled) { anim.Interval = 70; anim.Start(); }
            double total = (nextTime - curTime).TotalSeconds;
            view.Progress = total > 0 ? (now - curTime).TotalSeconds / total : 0;

            // Через N минут после наступления переключаемся на следующий намаз с обратным отсчётом
            countdownMode = passed.TotalMinutes >= ShowCurrentMin && !view.Alert;
            view.Countdown = countdownMode;
            if (countdownMode)
            {
                view.Time = nextTime.ToString("HH:mm");
                view.Prefix = Lang.T("nextWord");
                view.Name = Lang.Names[nextIdx];
                view.Elapsed = string.Format(Lang.T("left"), Lang.Span((int)Math.Ceiling(left.TotalMinutes)));
                view.Sunrise = false;
            }
            else view.Prefix = "";
            view.ShowTicker = TickerEnabled || tickerPhase != 0;   // табло не зависит от режима — только от переключателя

            if (pendingPrayer.Value != DateTime.MinValue || startupPrayer.Value != DateTime.MinValue)
            {
                bool fresh = pendingPrayer.Value != DateTime.MinValue;
                KeyValuePair<int, DateTime> pp = fresh ? pendingPrayer : startupPrayer;
                pendingPrayer = startupPrayer = new KeyValuePair<int, DateTime>(0, DateTime.MinValue);
                BeginInvoke((MethodInvoker)delegate { OnPrayerStart(pp.Key, pp.Value, fresh); });
            }

            string key = view.Time + view.Name + view.Elapsed + view.Sub + view.WarnSoon + view.ShowTicker + view.Hourglass + (int)(view.Progress * 200);
            if (key != lastKey)
            {
                lastKey = key;
                if (!dragging) PlaceOnTaskbar();
                Redraw();
            }
        }

        KeyValuePair<int, DateTime> pendingPrayer = new KeyValuePair<int, DateTime>(0, DateTime.MinValue),
                                    startupPrayer = new KeyValuePair<int, DateTime>(0, DateTime.MinValue);
        PrayerCard card;
        DateTime breakLaterAt = DateTime.MinValue; string breakName, breakTime; int breakMin;

        Rectangle WidgetScreenRect()
        {
            RECT r;
            if (GetWindowRect(Handle, out r) && r.R > r.L) return Rectangle.FromLTRB(r.L, r.T, r.R, r.B);
            return screenRect;
        }

        // Перерыв (блокировка экрана) для каждого намаза: 10, 15 или 20 минут.
        // По умолчанию: Бомдод 15, Пешин 20, Аср 10, Шом 10, Хуфтон 15; у восхода перерыва нет.
        static readonly int[] BreakDefault = { 15, 0, 20, 10, 10, 15 };
        static readonly int[] BreakChoices = { 10, 15, 20 };
        static int BreakMinutes(int nameIdx)
        {
            if (nameIdx < 0 || nameIdx > 5 || BreakDefault[nameIdx] == 0) return 0;
            int m = (int)Cfg("break" + nameIdx, BreakDefault[nameIdx]);
            return Array.IndexOf(BreakChoices, m) >= 0 ? m : BreakDefault[nameIdx];
        }

        // Наступил намаз: карточка с аятом над виджетом и перерыв (при запуске программы посреди намаза — без перерыва)
        void OnPrayerStart(int nameIdx, DateTime time, bool fresh)
        {
            int brk = fresh ? BreakMinutes(nameIdx) : 0;
            if (Store.Get("card", "1") != "1" && brk == 0) return;
            ShowCard(Lang.Names[nameIdx], time.ToString("HH:mm"), brk);
        }

        void ShowCard(string name, string time, int breakMinutes)
        {
            try { if (card != null && !card.IsDisposed) card.Close(); } catch { }
            card = new PrayerCard(name, time, WidgetScreenRect(), view.Dpi, breakMinutes > 0, breakMinutes, 60);
            card.BreakNow += delegate { BreakForm.ShowAll(name, time, breakMinutes); };
            card.Postpone += delegate(int m) { breakName = name; breakTime = time; breakMin = breakMinutes; breakLaterAt = DateTime.Now.AddMinutes(m); };
            card.Show();
        }

        void StartAlert(TimeSpan duration, bool sound)
        {
            alertStart = DateTime.Now;
            alertUntil = alertStart + duration;
            view.Alert = true;
            if (sound && Store.Get("sound", "0") == "1") System.Media.SystemSounds.Asterisk.Play();
            anim.Interval = 40;
            anim.Start();
        }

        void StopAlert()
        {
            alertUntil = DateTime.MinValue;
            view.Alert = false;
            Redraw();
        }

        // Бегущая строка: 10 с показываем следующий намаз, затем:
        // 1) статичный текст уходит вниз, 2) проезжает расписание, 3) текст опускается сверху.
        const double SlideSec = 0.35;
        bool countdownMode;
        double ShowCurrentMin { get { return Cfg("showCurrent", 40); } }
        // Настройки бегущей строки (меню → «Бегущая строка»), хранятся в settings.ini
        static double Cfg(string key, double def)
        {
            double v; return double.TryParse(Store.Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : def;
        }
        // Табло включается и выключается вручную (меню «Бегущая строка» → «Показывать табло»), без привязки к режиму отсчёта
        static bool TickerEnabled { get { return Store.Get("tickerOn", "1") == "1"; } }
        double TickerPauseSec { get { return Cfg("tickerEvery", 10); } }   // 0 = только по кнопке ⟳
        double GapBeforeSec { get { return Cfg("tickerBefore", 1); } }
        double GapAfterSec { get { return Cfg("tickerAfter", 3); } }
        float TickerSpeed { get { return (float)Cfg("tickerSpeed", 55); } }
        // 0 статика, 1 уход вниз, 4 пауза, 2 прокрутка, 5 пауза, 3 появление сверху
        int tickerPhase;
        DateTime tickerStaticSince = DateTime.Now, tickerPhaseStart;
        float tickerStartX;

        void TickerTick()
        {
            if (tickerPhase != 0 || dragging || TickerPauseSec <= 0 || !TickerEnabled) return;
            if ((DateTime.Now - tickerStaticSince).TotalSeconds < TickerPauseSec) return;
            StartTicker();
        }

        void StartTicker()
        {
            if (tickerPhase != 0) return;
            tickerPhase = 1;
            tickerPhaseStart = DateTime.Now;
            if (!TickerEnabled)
            {
                // табло выключено — по кнопке ⟳ показываем его только на время прокрутки
                view.ShowTicker = true; view.StaticAlpha = 0f;
                tickerPhase = 4;
                lastKey = null; PlaceOnTaskbar();
            }
            anim.Interval = 40;
            anim.Start();
        }

        static float EaseOut(double t) { t = Math.Max(0, Math.Min(1, t)); return (float)(1 - Math.Pow(1 - t, 3)); }
        static float EaseIn(double t) { t = Math.Max(0, Math.Min(1, t)); return (float)(t * t * t); }

        void OnAnim()
        {
            DateTime now = DateTime.Now;
            double pt = (now - tickerPhaseStart).TotalSeconds;
            float boxH = view.TickerRect.Height;
            if (tickerPhase == 1)
            {
                view.StaticDY = EaseIn(pt / SlideSec) * boxH;
                view.StaticAlpha = 1f - (float)(pt / SlideSec);
                if (pt >= SlideSec) { tickerPhase = 4; tickerPhaseStart = now; view.StaticAlpha = 0f; pt = 0; }
            }
            if (tickerPhase == 4)
            {
                if (pt >= (!TickerEnabled ? 0.3 : GapBeforeSec))
                {
                    tickerPhase = 2; tickerPhaseStart = now;
                    tickerStartX = view.TickerRect.Width;      // расписание въезжает справа
                    view.TickerX = tickerStartX;
                    view.TickerScroll = true;
                }
            }
            else if (tickerPhase == 2)
            {
                float speed = TickerSpeed * view.Dpi;           // px в секунду
                view.TickerX = tickerStartX - (float)(pt * speed);
                if (view.TickerX < -view.ScheduleW - 4)
                {
                    tickerPhase = 5; tickerPhaseStart = now; pt = 0;
                    view.TickerScroll = false;
                    view.StaticDY = -boxH; view.StaticAlpha = 0f;
                }
            }
            if (tickerPhase == 5)
            {
                if (pt >= GapAfterSec)
                {
                    if (!TickerEnabled)
                    {
                        tickerPhase = 0; tickerStaticSince = now;
                        view.StaticDY = 0; view.StaticAlpha = 1f;
                        view.ShowTicker = false; lastKey = null; PlaceOnTaskbar();
                    }
                    else { tickerPhase = 3; tickerPhaseStart = now; pt = 0; }
                }
            }
            else if (tickerPhase == 3)
            {
                view.StaticDY = -(1f - EaseOut(pt / SlideSec)) * boxH;
                view.StaticAlpha = (float)Math.Min(1, pt / SlideSec);
                if (pt >= SlideSec)
                {
                    tickerPhase = 0; tickerStaticSince = now;
                    view.StaticDY = 0; view.StaticAlpha = 1f;
                }
            }
            if (view.Alert && now >= alertUntil) { view.Alert = false; }
            view.AlertT = (now - alertStart).TotalSeconds;
            view.SandT = (now - DateTime.Today).TotalSeconds;
            if (syncing) view.SyncAngle = (view.SyncAngle + 18) % 360;
            // первая минута — 25 кадров/с, дальше плавное дыхание 12 кадров/с (экономно)
            bool gap = (tickerPhase == 4 || tickerPhase == 5) && !view.Alert && !syncing;
            anim.Interval = gap ? 100 : ((view.Alert && view.AlertT > 60 && !syncing && tickerPhase == 0) ? 80 : 40);
            if (view.Hourglass && !view.Alert && !syncing && tickerPhase == 0) anim.Interval = 70;   // песок — 14 кадров/с
            if (!view.Alert && !syncing && tickerPhase == 0 && !view.Hourglass) anim.Stop();
            if (Visible) Redraw();
        }

        void UpdateTooltip()
        {
            if (today == null) return;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine((region != null ? Lang.City(region) : "") + " · " + DateTime.Now.ToString("dd.MM.yyyy"));
            string[] uz = { "Бомдод", "Қуёш", "Пешин", "Аср", "Шом", "Хуфтон" };
            for (int i = 0; i < 6; i++)
                sb.AppendLine(Lang.Names[i].PadRight(9) + today[i].ToString("HH:mm") + (Lang.Cur >= 2 ? "   (" + uz[i] + ")" : ""));
            sb.AppendLine();
            sb.Append(syncError != null ? "⚠ " + syncError : Lang.T("synced") + (lastSync ?? Lang.T("never")));
            tip.SetToolTip(this, sb.ToString());
        }

        void OnTick()
        {
            tick++;
            Recalc();
            KeepOnTop();
            TickerTick();
            if (breakLaterAt != DateTime.MinValue && DateTime.Now >= breakLaterAt) { breakLaterAt = DateTime.MinValue; BreakForm.ShowAll(breakName, breakTime, breakMin); }
            CheckTaskbarAlive();
            if (tick == 2 && embedded) PlaceOnTaskbar();
            if (tick % 30 == 0 && !dragging) PlaceOnTaskbar();
            if (tick % 600 == 0 && !view.Alert && tickerPhase == 0) { ApplyTheme(); Redraw(); }   // смена темы Windows
            if (tick == 5 || tick % 3600 == 0) EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
        }

        // Окна самой Windows (Пуск, поиск, центр уведомлений, Alt+Tab и т.п.) —
        // они полноэкранные, но это не «полноэкранное приложение», прятаться не нужно.
        static readonly string[] ShellProcs = { "explorer", "StartMenuExperienceHost", "SearchHost", "SearchApp",
            "ShellExperienceHost", "ShellHost", "TextInputHost", "LockApp", "Widgets", "WidgetBoard", "ApplicationFrameHost" };
        static readonly string[] ShellClasses = { "WorkerW", "Progman", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging",
            "TopLevelWindowForOverflowXamlIsland", "NotifyIconOverflowWindow", "Xaml_WindowedPopupClass" };
        IntPtr fsCacheHwnd = IntPtr.Zero; bool fsCacheShell;

        bool IsFullscreenForeground()
        {
            IntPtr fw = GetForegroundWindow();
            if (fw == IntPtr.Zero || fw == Handle || fw == GetDesktopWindow() || fw == GetShellWindow()) return false;
            if (fw != fsCacheHwnd)
            {
                fsCacheHwnd = fw; fsCacheShell = false;
                StringBuilder cls = new StringBuilder(128); GetClassName(fw, cls, 128);
                if (Array.IndexOf(ShellClasses, cls.ToString()) >= 0) fsCacheShell = true;
                else
                {
                    try
                    {
                        uint pid; GetWindowThreadProcessId(fw, out pid);
                        string pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
                        foreach (string sp in ShellProcs) if (string.Equals(sp, pn, StringComparison.OrdinalIgnoreCase)) fsCacheShell = true;
                    }
                    catch { }
                }
            }
            if (fsCacheShell) return false;
            RECT r; if (!GetWindowRect(fw, out r)) return false;
            Rectangle sb = Screen.FromHandle(fw).Bounds;
            return r.L <= sb.Left && r.T <= sb.Top && r.R >= sb.Right && r.B >= sb.Bottom;
        }

        // Мгновенная реакция на смену активного окна (Пуск/панель задач выходят наверх)
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr h);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        WinEventProc winEventProc; IntPtr winHook = IntPtr.Zero;
        System.Windows.Forms.Timer nudge; int nudgeLeft;

        void HookForeground()
        {
            winEventProc = delegate(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time)
            {
                // даём Windows закончить переключение, затем сразу возвращаемся поверх панели
                BeginInvoke((MethodInvoker)delegate { KeepOnTop(); nudgeLeft = 4; if (nudge != null) nudge.Start(); });
            };
            // EVENT_SYSTEM_FOREGROUND (3) … EVENT_SYSTEM_MINIMIZEEND (0x17); WINEVENT_OUTOFCONTEXT
            nudge = new System.Windows.Forms.Timer();
            nudge.Interval = 120;   // панель задач поднимается чуть позже события — повторяем несколько раз
            nudge.Tick += delegate { KeepOnTop(); if (--nudgeLeft <= 0) nudge.Stop(); };
            winHook = SetWinEventHook(0x0003, 0x0003, IntPtr.Zero, winEventProc, 0, 0, 0);
        }

        void KeepOnTop()
        {
            if (dragging) return;
            if (embedded)
            {
                if (!Visible && !hiddenForSpace) Show();
                SetWindowPos(Handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                return;
            }
            bool fs = IsFullscreenForeground();
            if (fs) { if (Visible) Hide(); return; }
            if (!Visible && !hiddenForSpace) Show();
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (embedded)
            {
                RenderLayered();
            }
            if (!embedded) { TopMost = true; HookForeground(); }
        }
        protected override void OnFormClosed(FormClosedEventArgs e) { if (winHook != IntPtr.Zero) UnhookWinEvent(winHook); base.OnFormClosed(e); }

        void Redraw()
        {
            if (embedded) RenderLayered(); else Invalidate();
        }

        // ── Прозрачный слой через UpdateLayeredWindow (попиксельная альфа) ──
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        bool ulwLogged;

        void RenderLayered()
        {
            if (!IsHandleCreated || Width <= 0 || Height <= 0 || view == null) return;
            view.Syncing = syncing;
            view.SyncErr = syncError != null;
            view.Transparent = true;
            using (Bitmap bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) view.Draw(g, bmp.Size);
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
                SIZE sz; sz.cx = bmp.Width; sz.cy = bmp.Height;
                POINT src; src.X = 0; src.Y = 0;
                BLENDFUNCTION bf; bf.Op = 0; bf.Flags = 0; bf.Alpha = 255; bf.Format = 1; // AC_SRC_ALPHA
                bool ok = UpdateLayeredWindow(Handle, screen, IntPtr.Zero, ref sz, mem, ref src, 0, ref bf, 2 /*ULW_ALPHA*/);
                if (!ok && !ulwLogged) { ulwLogged = true; Store.Log("UpdateLayeredWindow failed, err=" + Marshal.GetLastWin32Error()); }
                SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (embedded) return;
            view.Syncing = syncing;
            view.SyncErr = syncError != null;
            view.Draw(e.Graphics, ClientSize);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            bool hs = view.SyncRect.Contains(e.Location);
            if (hs != view.HoverSync || !view.Hover) { view.HoverSync = hs; view.Hover = true; Cursor = hs ? Cursors.Hand : Cursors.Default; Redraw(); }
            if (dragging)
            {
                int dx = Cursor.Position.X - dragStart.X;
                if (Math.Abs(dx) > 3) dragMoved = true;
                if (dragMoved) { offsetX = Math.Max(0, dragOffset0 + dx); PlaceOnTaskbar(); }
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { view.Hover = false; view.HoverSync = false; Cursor = Cursors.Default; Redraw(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !view.SyncRect.Contains(e.Location))
            { dragging = true; dragMoved = false; dragStart = Cursor.Position; dragOffset0 = offsetX; }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (dragging && dragMoved)
                {
                    Store.Settings["offsetX"] = offsetX.ToString(); Store.Save();
                    view.Bg = SampleTaskbarColor(view.Bg); Redraw();
                }
                else if (view.SyncRect.Contains(e.Location)) { StartSync(); StartTicker(); }
                else if (view.Alert) StopAlert();          // клик гасит подсветку
                dragging = false;
            }
            else if (e.Button == MouseButtons.Right) { RebuildCityMenu(); ChatMenu.Rebuild(chatMenu); menu.Show(Cursor.Position); }
            base.OnMouseUp(e);
        }

        void StartSync()
        {
            if (syncing) return;
            syncing = true; anim.Start(); Redraw();
            Thread t = new Thread(delegate()
            {
                List<Region> list = null; string err = null;
                DateTime t0 = DateTime.Now;
                try { list = Store.Download(); }
                catch (Exception ex) { err = Lang.T("syncFail") + ex.Message; }
                int spent = (int)(DateTime.Now - t0).TotalMilliseconds;
                if (spent < 700) Thread.Sleep(700 - spent);   // чтобы было видно, что обновление прошло
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        syncing = false;
                        syncError = err;
                        if (list != null)
                        {
                            int id = region != null ? region.Id : 27;
                            regions = list; SelectRegion(id, false);
                            lastSync = DateTime.Now.ToString("dd.MM HH:mm");
                            Store.Settings["lastSync"] = DateTime.Now.ToString("s", CultureInfo.InvariantCulture); Store.Save();
                        }
                        computedFor = DateTime.MinValue; lastKey = null;
                        Recalc(); UpdateTooltip(); Redraw();
                    });
                }
                catch { }
            });
            t.IsBackground = true; t.Start();
        }

        void SetLanguage(int idx)
        {
            Lang.Cur = idx;
            Store.Settings["lang"] = Lang.Codes[idx]; Store.Save();
            BuildMenu();
            ChatDock.Reopen();   // перерисовать на новом языке
            lastKey = null; Recalc(); UpdateTooltip(); Redraw();
        }

        ToolStripMenuItem cityMenu, chatMenu;
        // Меню сгруппировано: чат · город и язык · оформление · уведомления · настройки · выход
        void BuildMenu()
        {
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            MenuUi.Style(menu);
            string sec = " " + Lang.T("sec"), mn = " " + Lang.T("min");

            chatMenu = MenuUi.Sub(ChatT.T("menu"), "\uE8F2");   // наполняется при показе меню: участники группы, быстрые фразы
            ChatMenu.Rebuild(chatMenu);
            menu.Items.Add(chatMenu);
            menu.Items.Add(new ToolStripSeparator());

            cityMenu = MenuUi.Sub(Lang.T("city"), "\uE707");
            menu.Items.Add(cityMenu);
            ToolStripMenuItem langMenu = MenuUi.Sub(Lang.T("lang"), "\uE774");
            for (int li = 0; li < Lang.Titles.Length; li++)
            {
                int idx = li;
                ToolStripMenuItem it = MenuUi.Sub(Lang.Titles[li], null);
                it.Checked = li == Lang.Cur;
                it.Click += delegate { SetLanguage(idx); };
                langMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(langMenu);

            // Оформление: цветовая тема, бегущая строка, положение
            ToolStripMenuItem look = MenuUi.Sub(Lang.T("look"), "\uE790");
            ToolStripMenuItem skins = MenuUi.Sub(Lang.T("skin"), null);
            foreach (SkinDef sk in Skin.All)
            {
                SkinDef def = sk;
                skins.DropDownItems.Add(MenuUi.Swatch(def, def.Id == Skin.CurId, delegate { SetSkin(def.Id); }));
            }
            look.DropDownItems.Add(skins);
            ToolStripMenuItem tick = MenuUi.Sub(Lang.T("ticker"), null);
            ToolStripMenuItem tickOn = MenuUi.Sub(Lang.T("tShow"), null);
            tickOn.Checked = TickerEnabled;
            tickOn.Click += delegate
            {
                tickOn.Checked = !tickOn.Checked;
                Store.Settings["tickerOn"] = tickOn.Checked ? "1" : "0"; Store.Save();
                tickerStaticSince = DateTime.Now;
                lastKey = null; Recalc();
            };
            tick.DropDownItems.Add(tickOn);
            tick.DropDownItems.Add(new ToolStripSeparator());
            AddChoice(tick, Lang.T("tEvery"), "tickerEvery", 10, new double[] { 10, 20, 30, 60, 120, 0 },
                new string[] { "10" + sec, "20" + sec, "30" + sec, "1" + mn, "2" + mn, Lang.T("off") });
            AddChoice(tick, Lang.T("tBefore"), "tickerBefore", 1, new double[] { 0, 0.5, 1, 2, 3 },
                new string[] { "0" + sec, "0.5" + sec, "1" + sec, "2" + sec, "3" + sec });
            AddChoice(tick, Lang.T("tAfter"), "tickerAfter", 3, new double[] { 0, 1, 2, 3, 5 },
                new string[] { "0" + sec, "1" + sec, "2" + sec, "3" + sec, "5" + sec });
            AddChoice(tick, Lang.T("tSpeed"), "tickerSpeed", 55, new double[] { 35, 55, 80 },
                new string[] { Lang.T("slow"), Lang.T("normal"), Lang.T("fast") });
            look.DropDownItems.Add(tick);
            look.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem emb = MenuUi.Sub(Lang.T("embed"), null);
            emb.Checked = Store.Get("embed", "1") == "1";
            emb.Click += delegate
            {
                Store.Settings["embed"] = emb.Checked ? "0" : "1"; Store.Save();
                try { System.Diagnostics.Process.Start(Application.ExecutablePath, "/restart"); } catch { }
                Environment.Exit(0);
            };
            look.DropDownItems.Add(emb);
            look.DropDownItems.Add(MenuUi.Item(Lang.T("reset"), null, delegate { offsetX = DefaultOffset(); Store.Settings.Remove("offsetX"); Store.Save(); PlaceOnTaskbar(); }));
            menu.Items.Add(look);

            // Уведомления: звук, карточка, перерыв
            ToolStripMenuItem notif = MenuUi.Sub(Lang.T("alerts"), "\uEA8F");
            soundItem = MenuUi.Sub(Lang.T("sound"), null);
            soundItem.Checked = Store.Get("sound", "0") == "1";
            soundItem.Click += delegate
            {
                soundItem.Checked = !soundItem.Checked;
                Store.Settings["sound"] = soundItem.Checked ? "1" : "0"; Store.Save();
                if (soundItem.Checked) System.Media.SystemSounds.Asterisk.Play();
            };
            notif.DropDownItems.Add(soundItem);
            ToolStripMenuItem cardItem = MenuUi.Sub(Words.T("card"), null);
            cardItem.Checked = Store.Get("card", "1") == "1";
            cardItem.Click += delegate { cardItem.Checked = !cardItem.Checked; Store.Settings["card"] = cardItem.Checked ? "1" : "0"; Store.Save(); };
            notif.DropDownItems.Add(cardItem);
            ToolStripMenuItem showCur = MenuUi.Sub(Lang.T("showCur"), null);
            AddChoice(showCur, null, "showCurrent", 40, new double[] { 15, 20, 30, 40, 60 },
                new string[] { "15" + mn, "20" + mn, "30" + mn, "40" + mn, "60" + mn });
            notif.DropDownItems.Add(showCur);
            ToolStripMenuItem brk = MenuUi.Sub(Words.T("breakM"), null);
            // для каждого намаза своя длительность блокировки: 10 / 15 / 20 минут
            string[] brkLabels = new string[BreakChoices.Length];
            double[] brkValues = new double[BreakChoices.Length];
            for (int i = 0; i < BreakChoices.Length; i++) { brkValues[i] = BreakChoices[i]; brkLabels[i] = string.Format(Words.T("screen"), BreakChoices[i]); }
            for (int p = 0; p < 6; p++)
                if (BreakDefault[p] > 0)
                    AddChoice(brk, Lang.Names[p] + " — " + string.Format(Words.T("screen"), BreakMinutes(p)), "break" + p, BreakDefault[p], brkValues, brkLabels);
            notif.DropDownItems.Add(brk);
            notif.DropDownItems.Add(new ToolStripSeparator());
            notif.DropDownItems.Add(MenuUi.Item(Lang.T("test"), null, delegate
            {
                StartAlert(TimeSpan.FromSeconds(20), true);
                ShowCard(view.Name, view.Time, 0);
            }));
            menu.Items.Add(notif);

            // Настройки: автозапуск, данные islom.uz
            ToolStripMenuItem sys = MenuUi.Sub(Lang.T("system"), "\uE713");
            ToolStripMenuItem auto = MenuUi.Sub(Lang.T("autorun"), null);
            auto.Checked = IsAutostart();
            auto.Click += delegate { SetAutostart(!auto.Checked); auto.Checked = IsAutostart(); };
            sys.DropDownItems.Add(auto);
            sys.DropDownItems.Add(new ToolStripSeparator());
            sys.DropDownItems.Add(MenuUi.Item(Lang.T("sync"), "\uE895", delegate { StartSync(); }));
            sys.DropDownItems.Add(MenuUi.Item(Lang.T("open"), "\uE71B", delegate { try { System.Diagnostics.Process.Start("https://islom.uz/taqvim"); } catch { } }));
            menu.Items.Add(sys);

            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem ver = MenuUi.Sub("NamazBar " + Build.Version, null); ver.Enabled = false;
            menu.Items.Add(ver);
            menu.Items.Add(MenuUi.Item(Lang.T("exit"), "\uE7E8", delegate { Application.Exit(); }));
        }

        void SetSkin(string id)
        {
            Skin.Apply(id);
            Store.Settings["skin"] = id; Store.Save();
            ApplyTheme();
            BuildMenu();
            ChatDock.Reopen();
            lastKey = null; Recalc(); UpdateTooltip(); PlaceOnTaskbar(); Redraw();
        }

        void AddChoice(ToolStripMenuItem parent, string title, string key, double def, double[] values, string[] labels)
        {
            ToolStripMenuItem sub = title == null ? parent : MenuUi.Sub(title, null);
            double cur = Cfg(key, def);
            for (int i = 0; i < values.Length; i++)
            {
                double v = values[i];
                ToolStripMenuItem it = MenuUi.Sub(labels[i], null);
                it.Checked = Math.Abs(cur - v) < 0.001;
                it.Click += delegate
                {
                    Store.Settings[key] = v.ToString(CultureInfo.InvariantCulture); Store.Save();
                    foreach (ToolStripItem o in sub.DropDownItems) { ToolStripMenuItem mi = o as ToolStripMenuItem; if (mi != null) mi.Checked = mi == it; }
                    tickerStaticSince = DateTime.Now;
                    lastKey = null; Recalc();
                };
                sub.DropDownItems.Add(it);
            }
            if (sub != parent) parent.DropDownItems.Add(sub);
            lastKey = null;
        }

        void RebuildCityMenu()
        {
            cityMenu.DropDownItems.Clear();
            ToolStripMenuItem more = null;
            foreach (Region r in regions)
            {
                Region rr = r;
                ToolStripMenuItem it = MenuUi.Sub(Lang.City(r), null);
                it.Checked = region != null && region.Id == r.Id;
                it.Click += delegate { SelectRegion(rr.Id, true); lastPeriod = int.MinValue; Recalc(); UpdateTooltip(); };
                if (r.Order < 100) cityMenu.DropDownItems.Add(it);
                else
                {
                    if (more == null) more = MenuUi.Sub(Lang.T("other"), null);
                    more.DropDownItems.Add(it);
                }
            }
            if (more != null) { cityMenu.DropDownItems.Add(new ToolStripSeparator()); cityMenu.DropDownItems.Add(more); }
        }

        // Автозапуск: установленная версия (MSIX-пакет) — через StartupTask пакета
        // (запись в HKCU\...\Run из пакета виртуализируется и не работает); переносной exe — через реестр.
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetCurrentPackageFullName(ref int len, StringBuilder name);
        static bool IsPackaged
        {
            get
            {
                try { int len = 0; return GetCurrentPackageFullName(ref len, null) != 15700; }   // APPMODEL_ERROR_NO_PACKAGE
                catch { return false; }
            }
        }
        static bool IsAutostart()
        {
            if (IsPackaged) { try { return PackagedStartup.IsEnabled(); } catch { return false; } }
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("NamazBar") != null; }
            catch { return false; }
        }
        static void SetAutostart(bool on)
        {
            if (IsPackaged) { try { PackagedStartup.Set(on); } catch (Exception ex) { Store.Log("startup task: " + ex.Message); } return; }
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (on) k.SetValue("NamazBar", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("NamazBar", false);
                }
            }
            catch { }
        }

        // Все основные состояния виджета на панели — одной картинкой (тёмная и светлая тема, песочные часы)
        static void Preview(string path)
        {
            Lang.Cur = 2;
            string[] names = Lang.Names;
            object[][] states = {
                new object[] { false, "16:27", names[3], "прошло 12 мин", false, 0.0, false },
                new object[] { false, "18:18", names[4], "через 52 мин", true, 0.0, false },
                new object[] { false, "18:18", names[4], "через 12 мин", true, 0.8, false },
                new object[] { false, "18:18", names[4], "через 4 мин", true, 0.27, false },
                new object[] { false, "18:18", names[4], "через 1 мин", true, 0.07, false },
                new object[] { true,  "18:18", names[4], "через 9 мин", true, 0.6, false },
                new object[] { false, "04:56", names[0], "только что", false, 0.0, true },
            };
            int W = 900, rowH = 70;
            using (Bitmap all = new Bitmap(W, rowH * states.Length))
            using (Graphics ga = Graphics.FromImage(all))
            {
                for (int i = 0; i < states.Length; i++)
                {
                    object[] st = states[i];
                    bool light = (bool)st[0];
                    View v = new View(light); v.Dpi = 1.5f; v.Transparent = false;
                    v.Bg = light ? Color.FromArgb(238, 238, 238) : Color.FromArgb(32, 32, 32);
                    v.Time = (string)st[1]; v.Name = (string)st[2]; v.Elapsed = (string)st[3];
                    v.Countdown = (bool)st[4]; v.Prefix = v.Countdown ? "Далее" : "";
                    v.Hourglass = (double)st[5] > 0; v.SandLeft = (double)st[5]; v.SandT = i * 0.37;
                    v.Alert = (bool)st[6]; v.AlertT = 0.45;
                    v.ShowTicker = false; v.Progress = 0.6; v.WarnSoon = v.Hourglass && v.SandLeft < 0.67;
                    using (Bitmap b = new Bitmap(W, rowH))
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        Size sz = v.Measure(g, rowH - 8);
                        g.Clear(v.Bg);
                        using (Bitmap w = new Bitmap(sz.Width, sz.Height))
                        {
                            using (Graphics gw = Graphics.FromImage(w)) v.Draw(gw, sz);
                            g.DrawImage(w, 10, 4);
                        }
                        ga.DrawImage(b, 0, i * rowH);
                    }
                }
                all.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            string stem = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFileNameWithoutExtension(path));
            int shadow;
            using (PrayerCard c = new PrayerCard(names[4], "18:18", Rectangle.Empty, 1.5f, true, 10, 45))
            using (Bitmap b = c.RenderBitmap(out shadow)) b.Save(stem + "_card.png", System.Drawing.Imaging.ImageFormat.Png);
            Rectangle scr = new Rectangle(0, 0, 2880, 1620);   // 1080p при масштабе 150 %
            using (BreakForm f = new BreakForm(names[4], "18:18", DateTime.Now.AddMinutes(9.6), scr))
            using (Bitmap b = new Bitmap(scr.Width, scr.Height))
            {
                using (Graphics g = Graphics.FromImage(b)) f.PaintTo(g, scr);
                b.Save(stem + "_break.png", System.Drawing.Imaging.ImageFormat.Png);
            }
        }

        // Отдельный класс: типы WinRT загружаются только в установленной версии
        static class PackagedStartup
        {
            static Windows.ApplicationModel.StartupTask Get()
            {
                return WindowsRuntimeSystemExtensions.AsTask(Windows.ApplicationModel.StartupTask.GetAsync("NamazBarStartup")).Result;
            }
            public static bool IsEnabled()
            {
                Windows.ApplicationModel.StartupTaskState s = Get().State;
                return s == Windows.ApplicationModel.StartupTaskState.Enabled || s == Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            public static void Set(bool on)
            {
                Windows.ApplicationModel.StartupTask t = Get();
                if (on) WindowsRuntimeSystemExtensions.AsTask(t.RequestEnableAsync()).Wait();
                else t.Disable();
            }
        }

        [STAThread]
        static void Main(string[] args)
        {
            // Один exe для раздачи: внутри лежит пакет NamazBar.msix. Запуск вне пакета — установка/обновление
            // и запуск установленной версии; внутри пакета — обычная работа на панели задач.
            if (!IsPackaged && Setup.HasPayload)
            {
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Environment.Exit(Setup.Run(args));
            }
            int pv = Array.IndexOf(args, "/preview");   // для разработки: состояния виджета картинкой
            if (pv >= 0 && pv + 1 < args.Length) { Preview(args[pv + 1]); return; }
            bool restart = Array.IndexOf(args, "/restart") >= 0;
            try { File.Delete(Path.Combine(Store.Dir, "NamazBar.log")); } catch { }
            // Новый запуск заменяет старый экземпляр (удобно при обновлении)
            if (!restart)
            {
                System.Diagnostics.Process me = System.Diagnostics.Process.GetCurrentProcess();
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                    if (p.Id != me.Id && p.ProcessName.StartsWith("NamazBar", StringComparison.OrdinalIgnoreCase) && !Setup.IsSetupProcess(p.Id))
                        { try { p.Kill(); p.WaitForExit(3000); } catch { } }
                restart = true;   // старый мог оставить мьютекс «брошенным» — подождём его
            }
            bool created;
            using (Mutex m = new Mutex(true, "NamazBar_single_instance", out created))
            {
                if (!created)
                {
                    if (!restart) return;
                    try { if (!m.WaitOne(10000)) return; } catch (AbandonedMutexException) { }
                }
                if (restart)   // ждём, пока Explorer заново создаст панель задач
                    for (int i = 0; i < 60 && FindWindow("Shell_TrayWnd", null) == IntPtr.Zero; i++) Thread.Sleep(500);
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += delegate(object o, ThreadExceptionEventArgs ea) { Store.Log("ERROR " + ea.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object o, UnhandledExceptionEventArgs ea) { Store.Log("FATAL " + ea.ExceptionObject); };
                Application.Run(new BarForm());
            }
        }
    }

    // ───────────────────────── Установщик (тот же exe с вшитым пакетом) ─────────────────────────
    // NamazBar.exe для раздачи содержит NamazBar.msix. Запуск вне пакета:
    //  • пакет установлен и не старее вшитого — просто запускаем установленную версию;
    //  • иначе ставим/обновляем: доверие сертификату и Windows App Runtime (один запрос администратора),
    //    установка пакета, запуск. Остановка и удаление — Параметры → Приложения → NamazBar.
    static class Setup
    {
        const string PkgName = "NamazBar.Widget", PkgPublisher = "CN=NamazBar";
        const string RuntimeName = "Microsoft.WindowsAppRuntime.1.8";
        const string MsPublisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
        const string RuntimeUrl = "https://aka.ms/windowsappsdk/1.8/latest/windowsappruntimeinstall-x64.exe";

        static readonly System.Reflection.Assembly asm = typeof(Setup).Assembly;
        public static bool HasPayload { get { return asm.GetManifestResourceInfo("NamazBar.msix") != null; } }

        static byte[] Res(string name)
        {
            using (Stream st = asm.GetManifestResourceStream(name))
            using (MemoryStream ms = new MemoryStream()) { st.CopyTo(ms); return ms.ToArray(); }
        }

        static readonly Dictionary<string, string[]> t = new Dictionary<string, string[]> {
            { "prep",    new[] { "Tayyorlanmoqda...", "Тайёрланмоқда...", "Подготовка...", "Preparing..." } },
            { "admin",   new[] { "Administrator ruxsati kerak (bir marta)...", "Администратор рухсати керак (бир марта)...", "Нужны права администратора (один раз)...", "Administrator rights needed (once)..." } },
            { "install", new[] { "O'rnatilmoqda...", "Ўрнатилмоқда...", "Установка...", "Installing..." } },
            { "done",    new[] { "NamazBar o'rnatildi va panelda ishlayapti.\n\nQulf ekraniga vidjet qo'shasizmi?\n(Sozlamalar → Qulf ekrani → Vidjetlar → Add widget → NamazBar)\n\nO'chirish: Sozlamalar → Ilovalar → NamazBar.",
                                 "NamazBar ўрнатилди ва панелда ишлаяпти.\n\nҚулф экранига виджет қўшасизми?\n(Созламалар → Қулф экрани → Виджетлар → Add widget → NamazBar)\n\nЎчириш: Созламалар → Иловалар → NamazBar.",
                                 "NamazBar установлен и работает на панели задач.\n\nДобавить виджет на экран блокировки?\n(Параметры → Экран блокировки → Виджеты → Add widget → NamazBar)\n\nУдаление: Параметры → Приложения → NamazBar.",
                                 "NamazBar is installed and running on the taskbar.\n\nAdd the widget to the lock screen now?\n(Settings → Lock screen → Widgets → Add widget → NamazBar)\n\nUninstall: Settings → Apps → NamazBar." } },
            { "fail",    new[] { "O'rnatib bo'lmadi:\n", "Ўрнатиб бўлмади:\n", "Не удалось установить:\n", "Setup failed:\n" } },
            { "cancel",  new[] { "Administrator ruxsati berilmadi.", "Администратор рухсати берилмади.", "Права администратора не предоставлены.", "Administrator rights were not granted." } },
        };
        static string T(string k) { return t[k][Lang.Cur]; }

        // Метка «это установщик»: запущенный им NamazBar не должен закрыть его вместе со старыми копиями
        static EventWaitHandle marker;
        public static bool IsSetupProcess(int pid)
        {
            try { using (EventWaitHandle.OpenExisting("NamazBar_setup_" + pid)) return true; }
            catch { return false; }
        }

        public static int Run(string[] args)
        {
            marker = new EventWaitHandle(false, EventResetMode.ManualReset, "NamazBar_setup_" + System.Diagnostics.Process.GetCurrentProcess().Id);
            Store.Load();
            int lix = Array.IndexOf(Lang.Codes, Store.Get("lang", ""));
            if (lix < 0)
            {
                CultureInfo c = CultureInfo.CurrentUICulture;
                lix = c.TwoLetterISOLanguageName == "uz" ? (c.Name.IndexOf("Cyrl", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                    : c.TwoLetterISOLanguageName == "ru" ? 2 : 3;
            }
            Lang.Cur = lix;
            if (Array.IndexOf(args, "/setup-admin") >= 0) return AdminStep(args);

            try
            {
                Pm.Info inst = Pm.Installed(PkgName, PkgPublisher);
                if (inst != null && !inst.Dev && inst.Version >= PayloadVersion()) { Launch(inst.Family); return 0; }
            }
            catch (Exception ex) { Store.Log("setup: " + ex.Message); }

            SetupForm f = new SetupForm();
            Application.Run(f);
            return f.Code;
        }

        static Version PayloadVersion()
        {
            string xml = Encoding.UTF8.GetString(Res("NamazBar.AppxManifest.xml"));
            Match m = Regex.Match(xml, "<Identity[^>]*\\sVersion=\"([0-9.]+)\"");
            return m.Success ? new Version(m.Groups[1].Value) : new Version(0, 0);
        }

        static void Launch(string family)
        {
            System.Diagnostics.Process.Start("explorer.exe", "shell:AppsFolder\\" + family + "!NamazBar");
        }

        // Шаги установки (в фоне). status — вывод в окно. Бросает исключение с понятным текстом.
        public static void Install(Action<string> status)
        {
            status(T("prep"));
            byte[] cer = Res("NamazBar.cer");
            bool needCert = !CertTrusted(cer), needRuntime = !Pm.RuntimeInstalled(RuntimeName, MsPublisher);
            if (needCert || needRuntime)
            {
                status(T("admin"));
                System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,
                    "/setup-admin" + (needCert ? " cert" : "") + (needRuntime ? " runtime" : ""));
                psi.Verb = "runas"; psi.UseShellExecute = true;
                System.Diagnostics.Process p;
                try { p = System.Diagnostics.Process.Start(psi); }
                catch (System.ComponentModel.Win32Exception ex) { if (ex.NativeErrorCode == 1223) throw new Exception(T("cancel")); throw; }
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("administrator step: code " + p.ExitCode + " (" + Path.Combine(Store.Dir, "NamazBar.log") + ")");
            }

            status(T("install"));
            // переносная копия, если запускалась: останавливаем и убираем её автозапуск из реестра
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcesses())
                if (p.Id != me && !IsSetupProcess(p.Id) && (p.ProcessName.StartsWith("NamazBar", StringComparison.OrdinalIgnoreCase) || p.ProcessName == "NamazWidget"))
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) if (k != null) k.DeleteValue("NamazBar", false); } catch { }

            Pm.Info inst = Pm.Installed(PkgName, PkgPublisher);
            if (inst != null && inst.Dev) Pm.Remove(inst.FullName);   // копия из папки сборки блокирует установку пакета

            string msix = Path.Combine(Path.GetTempPath(), "NamazBar.msix");
            File.WriteAllBytes(msix, Res("NamazBar.msix"));
            try { Pm.Add(msix); }
            finally { try { File.Delete(msix); } catch { } }

            // служба виджетов кэширует список провайдеров — перезапуск, чтобы NamazBar сразу появился в «Add widget»
            foreach (string n in new[] { "WidgetService", "WidgetBoard" })
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(n)) try { p.Kill(); } catch { }

            Launch(Pm.Installed(PkgName, PkgPublisher).Family);
        }

        static bool CertTrusted(byte[] cer)
        {
            System.Security.Cryptography.X509Certificates.X509Certificate2 c = new System.Security.Cryptography.X509Certificates.X509Certificate2(cer);
            System.Security.Cryptography.X509Certificates.X509Store st = new System.Security.Cryptography.X509Certificates.X509Store(
                System.Security.Cryptography.X509Certificates.StoreName.TrustedPeople, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
            try
            {
                st.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                return st.Certificates.Find(System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint, c.Thumbprint, false).Count > 0;
            }
            finally { st.Close(); }
        }

        // Запускается с правами администратора: доверие сертификату NamazBar и установка Windows App Runtime
        static int AdminStep(string[] args)
        {
            try
            {
                if (Array.IndexOf(args, "cert") >= 0)
                {
                    System.Security.Cryptography.X509Certificates.X509Store st = new System.Security.Cryptography.X509Certificates.X509Store(
                        System.Security.Cryptography.X509Certificates.StoreName.TrustedPeople, System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                    st.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadWrite);
                    st.Add(new System.Security.Cryptography.X509Certificates.X509Certificate2(Res("NamazBar.cer")));
                    st.Close();
                }
                if (Array.IndexOf(args, "runtime") >= 0)
                {
                    string exe = Path.Combine(Path.GetTempPath(), "windowsappruntimeinstall-x64.exe");
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
                    using (WebClient wc = new WebClient()) wc.DownloadFile(RuntimeUrl, exe);
                    System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(exe, "--quiet");
                    psi.UseShellExecute = false; psi.CreateNoWindow = true;
                    using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi)) p.WaitForExit();
                    try { File.Delete(exe); } catch { }
                }
                return 0;
            }
            catch (Exception ex) { Store.Log("setup admin: " + ex); return 2; }
        }

        // PackageManager (WinRT) — в отдельном классе, чтобы типы грузились только при установке
        static class Pm
        {
            public class Info { public Version Version; public bool Dev; public string Family, FullName; }

            public static Info Installed(string name, string publisher)
            {
                Windows.Management.Deployment.PackageManager pm = new Windows.Management.Deployment.PackageManager();
                foreach (Windows.ApplicationModel.Package p in pm.FindPackagesForUser("", name, publisher))
                {
                    Windows.ApplicationModel.PackageVersion v = p.Id.Version;
                    Info i = new Info();
                    i.Version = new Version(v.Major, v.Minor, v.Build, v.Revision);
                    i.Dev = p.IsDevelopmentMode; i.Family = p.Id.FamilyName; i.FullName = p.Id.FullName;
                    return i;
                }
                return null;
            }

            public static bool RuntimeInstalled(string name, string publisher)
            {
                Windows.Management.Deployment.PackageManager pm = new Windows.Management.Deployment.PackageManager();
                foreach (Windows.ApplicationModel.Package p in pm.FindPackagesForUser("", name, publisher))
                    if (p.Id.Architecture == Windows.System.ProcessorArchitecture.X64) return true;
                return false;
            }

            public static void Remove(string fullName)
            {
                Wait(new Windows.Management.Deployment.PackageManager().RemovePackageAsync(fullName));
            }

            public static void Add(string path)
            {
                Wait(new Windows.Management.Deployment.PackageManager().AddPackageAsync(new Uri(path), null,
                    Windows.Management.Deployment.DeploymentOptions.ForceApplicationShutdown | Windows.Management.Deployment.DeploymentOptions.ForceUpdateFromAnyVersion));
            }

            static void Wait(Windows.Foundation.IAsyncOperationWithProgress<Windows.Management.Deployment.DeploymentResult, Windows.Management.Deployment.DeploymentProgress> op)
            {
                try { WindowsRuntimeSystemExtensions.AsTask(op).Wait(); }
                catch (AggregateException ex) { throw new Exception(ex.InnerException != null ? ex.InnerException.Message : ex.Message); }
            }
        }

        // Маленькое окно «Установка...»
        class SetupForm : Form
        {
            public int Code = 1;
            readonly Label label = new Label();

            public SetupForm()
            {
                Text = "NamazBar";
                try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
                StartPosition = FormStartPosition.CenterScreen; AutoScaleMode = AutoScaleMode.Dpi;
                ClientSize = new Size(420, 110);
                label.SetBounds(20, 20, 380, 30);
                label.Font = Fonts.Get("NB Sans Medium", 10.5f, "Segoe UI", FontStyle.Regular);
                ProgressBar bar = new ProgressBar();
                bar.SetBounds(20, 62, 380, 16); bar.Style = ProgressBarStyle.Marquee;
                Controls.Add(label); Controls.Add(bar);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Thread th = new Thread(delegate()
                {
                    string error = null;
                    try { Install(delegate(string s) { BeginInvoke((MethodInvoker)delegate { label.Text = s; }); }); }
                    catch (Exception ex) { error = ex.Message; Store.Log("setup: " + ex); }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Hide();
                        if (error != null)
                            MessageBox.Show(T("fail") + error, "NamazBar", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        else
                        {
                            Code = 0;
                            if (MessageBox.Show(T("done"), "NamazBar", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                                try { System.Diagnostics.Process.Start("ms-settings:lockscreen"); } catch { }
                        }
                        Close();
                    });
                });
                th.IsBackground = true; th.Start();
            }
        }
    }
}
