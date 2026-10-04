// NamazBar — групповой чат для тех, кто рядом: найти коллег/друзей и позвать на совместный намаз.
// Протокол: ../docs/protocol.md (WebSocket-реле server/). Совместимо с C# 5 / .NET Framework 4.x.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace NamazBar
{
    // ───────────────────────── JSON ─────────────────────────
    static class J
    {
        static readonly JavaScriptSerializer ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 20 };
        public static Dictionary<string, object> Parse(string s)
        {
            try { return ser.DeserializeObject(s) as Dictionary<string, object>; } catch { return null; }
        }
        public static string Dump(object o) { return ser.Serialize(o); }
        public static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : null;
        }
        public static bool Bool(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is bool && (bool)v;
        }
        public static long Long(Dictionary<string, object> d, string k)
        {
            object v; try { return d != null && d.TryGetValue(k, out v) && v != null ? Convert.ToInt64(v) : 0; } catch { return 0; }
        }
        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
        public static List<Dictionary<string, object>> List(Dictionary<string, object> d, string k)
        {
            List<Dictionary<string, object>> r = new List<Dictionary<string, object>>();
            object v; IEnumerable e = d != null && d.TryGetValue(k, out v) ? v as IEnumerable : null;
            if (e == null || e is string) return r;
            foreach (object o in e) { Dictionary<string, object> x = o as Dictionary<string, object>; if (x != null) r.Add(x); }
            return r;
        }
    }

    // ───────────────────────── Модель ─────────────────────────
    class ChatMember { public string UserId, Nick, Avatar; public bool Online; }

    class ChatMsg
    {
        public string Id, Code, From, Nick, Kind, Preset, Text; public DateTime Time;
        public string Display { get { return Kind == "preset" ? ChatT.Preset(Preset, Text) : Text; } }
    }

    class ChatGroup
    {
        public string Code, Name, Owner;
        public List<ChatMember> Members = new List<ChatMember>();
        public List<ChatMsg> Messages = new List<ChatMsg>();
        public int Unread;
        public int OnlineCount { get { int n = 0; foreach (ChatMember m in Members) if (m.Online) n++; return n; } }
    }

    class JoinReq { public string RequestId, Code, GroupName; public ChatMember From; }

    // ───────────────────────── Транспорт: WebSocket с переподключением ─────────────────────────
    class RelayLink
    {
        public event Action<Dictionary<string, object>> Received;
        public event Action<bool> Connection;
        public event Action<string> Failed;   // понятная причина неудачного подключения
        readonly string url;
        readonly object qLock = new object();
        BlockingCollection<string> outq;
        volatile bool stopped;
        CancellationTokenSource cts;

        public RelayLink(string url) { this.url = url; }

        public void Start()
        {
            Thread t = new Thread(Run); t.IsBackground = true; t.Name = "chat-link"; t.Start();
        }
        public void Stop()
        {
            stopped = true;
            try { CancellationTokenSource c = cts; if (c != null) c.Cancel(); } catch { }
        }
        public void Send(object msg)
        {
            string s = J.Dump(msg);
            lock (qLock) { if (outq != null && !outq.IsAddingCompleted) { try { outq.Add(s); } catch { } } }
        }

        void Run()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }   // TLS 1.2
            int delay = 1000;
            while (!stopped)
            {
                bool wasUp = false; DateTime upAt = DateTime.Now;
                ClientWebSocket ws = null;
                try
                {
                    cts = new CancellationTokenSource();
                    CancellationToken ct = cts.Token;
                    ws = new ClientWebSocket();
                    Task0.Wait(ws.ConnectAsync(new Uri(url), ct), 20000, cts);
                    BlockingCollection<string> q = new BlockingCollection<string>();
                    lock (qLock) outq = q;
                    ClientWebSocket w2 = ws; CancellationTokenSource c2 = cts;
                    Thread sender = new Thread(delegate() { SendLoop(w2, q, c2); });
                    sender.IsBackground = true; sender.Start();
                    wasUp = true; upAt = DateTime.Now;
                    Action<bool> cb = Connection; if (cb != null) cb(true);
                    ReceiveLoop(ws, ct);
                }
                catch (Exception ex)
                {
                    if (!stopped)
                    {
                        Store.Log("chat link: " + ex.GetBaseException().Message);
                        Action<string> f = Failed; if (f != null) f(Friendly(ex.GetBaseException().Message));
                    }
                }
                finally
                {
                    lock (qLock) { if (outq != null) { try { outq.CompleteAdding(); } catch { } outq = null; } }
                    try { cts.Cancel(); } catch { }
                    try { if (ws != null) ws.Dispose(); } catch { }
                    if (wasUp) { Action<bool> cb = Connection; if (cb != null) cb(false); }
                }
                if (stopped) break;
                if (wasUp && (DateTime.Now - upAt).TotalSeconds > 10) delay = 1000;
                // бесплатные хостинги «засыпают» и просыпаются до минуты — поэтому экспоненциальная пауза до 30 с
                for (int waited = 0; waited < delay && !stopped; waited += 250) Thread.Sleep(250);
                delay = Math.Min(delay * 2, 30000);
            }
        }

        // Причина, понятная пользователю (что проверить), вместо «Подключение…» без конца
        public static string Friendly(string m)
        {
            m = m ?? "";
            if (m.Contains("404")) return "nothing answers at this address (404) - copy the exact URL from your hosting dashboard";
            if (m.Contains("400")) return "the server answered but not at this path (400) - the address must end with /ws";
            if (m.Contains("401") || m.Contains("403")) return "access denied (" + (m.Contains("401") ? "401" : "403") + ")";
            if (m.Contains("502") || m.Contains("503") || m.Contains("504")) return "the server is starting up (free hosting sleeps) - retrying";
            if (m.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0)
                return "no answer yet - free hosting can need up to a minute to wake up, retrying";
            if (m.IndexOf("name could not be resolved", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("remote name", StringComparison.OrdinalIgnoreCase) >= 0
                || m.IndexOf("no such host", StringComparison.OrdinalIgnoreCase) >= 0)
                return "address not found (DNS) - check the server address";
            if (m.IndexOf("SSL", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("TLS", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("certificate", StringComparison.OrdinalIgnoreCase) >= 0)
                return "secure connection failed (TLS) - " + m;
            return m.Length > 120 ? m.Substring(0, 120) + "..." : m;
        }

        static class Task0
        {
            public static void Wait(System.Threading.Tasks.Task t, int ms, CancellationTokenSource cts)
            {
                if (!t.Wait(ms)) { cts.Cancel(); throw new TimeoutException("connect timeout"); }
            }
        }

        void SendLoop(ClientWebSocket ws, BlockingCollection<string> q, CancellationTokenSource c)
        {
            try
            {
                while (!c.IsCancellationRequested && !q.IsCompleted)
                {
                    string s;
                    if (!q.TryTake(out s, 25000)) { if (q.IsCompleted) break; s = "{\"type\":\"ping\"}"; }   // keep-alive
                    byte[] b = Encoding.UTF8.GetBytes(s);
                    ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, c.Token).Wait();
                }
            }
            catch { try { c.Cancel(); } catch { } }
        }

        void ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
        {
            byte[] buf = new byte[8192];
            MemoryStream ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                WebSocketReceiveResult r = ws.ReceiveAsync(new ArraySegment<byte>(buf), ct).Result;
                if (r.MessageType == WebSocketMessageType.Close) break;
                ms.Write(buf, 0, r.Count);
                if (ms.Length > 4 * 1024 * 1024) break;   // аватары всей группы приходят в одном welcome
                if (!r.EndOfMessage) continue;
                Dictionary<string, object> d = J.Parse(Encoding.UTF8.GetString(ms.ToArray()));
                ms.SetLength(0);
                Action<Dictionary<string, object>> cb = Received;
                if (d != null && cb != null) cb(d);
            }
        }
    }

    // ───────────────────────── Сервис чата (состояние + логика) ─────────────────────────
    static class Chat
    {
        public static event Action Changed;                 // обновить окно
        public static event Action<ChatGroup, ChatMsg> Incoming;
        public static event Action<JoinReq> JoinRequested;
        public static event Action<string> JoinSettled;     // requestId
        public static event Action<string, bool> Notice;    // text, isError

        public static readonly List<ChatGroup> Groups = new List<ChatGroup>();
        public static bool Connected;
        public static string LastError;   // почему не получается подключиться (null — всё в порядке)

        // История живёт недолго: через 20 минут после времени каждого намаза сообщения стираются
        public const int ClearAfterMinutes = 20;
        public static Func<List<DateTime>> PrayerStarts;   // начала намазов (вчера и сегодня), задаёт приложение
        static DateTime lastCutoff = DateTime.MinValue;

        // Последний уже наступивший момент «начало намаза + 20 минут»
        public static DateTime CurrentCutoff()
        {
            DateTime best = DateTime.MinValue;
            Func<List<DateTime>> f = PrayerStarts;
            List<DateTime> list = null;
            if (f != null) { try { list = f(); } catch { } }
            if (list == null) return best;
            DateTime now = DateTime.Now;
            foreach (DateTime p in list) { DateTime c = p.AddMinutes(ClearAfterMinutes); if (c <= now && c > best) best = c; }
            return best;
        }

        static long ToMs(DateTime local) { return (long)(local.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds; }

        // Вызывается раз в полминуты: сменилась граница — стираем историю у себя и просим сервер стереть у всех
        public static void PruneTick()
        {
            DateTime cut = CurrentCutoff();
            if (cut == DateTime.MinValue || cut <= lastCutoff) return;
            lastCutoff = cut;
            bool changed = false;
            foreach (ChatGroup g in Groups) if (g.Messages.RemoveAll(delegate(ChatMsg m) { return m.Time <= cut; }) > 0) changed = true;
            SendClears(cut);
            if (changed) Fire();
        }

        static void SendClears(DateTime cut)
        {
            if (!Connected || cut == DateTime.MinValue || (DateTime.Now - cut).TotalHours > 23) return;   // сервер принимает границу не старше суток
            long ms = ToMs(cut);
            foreach (ChatGroup g in Groups) Send(new Dictionary<string, object> { { "type", "clearHistory" }, { "code", g.Code }, { "before", ms } });
        }
        public static string UserId, Nick, Avatar, Server;
        public static ChatGroup Active;                     // открытая в окне группа (для счётчика непрочитанных)
        static string lastLine; static DateTime lastLineAt;

        // последняя входящая реплика (для экрана перерыва): живёт 10 минут
        public static string RecentLine() { return lastLine != null && (DateTime.Now - lastLineAt).TotalMinutes < 10 ? lastLine : null; }
        public static void RememberLine(ChatGroup g, ChatMsg m) { lastLine = m.Nick + " · " + g.Name + ": " + m.Display; lastLineAt = DateTime.Now; }

        static RelayLink link;
        static SynchronizationContext sync;
        static readonly Dictionary<string, string> tokens = new Dictionary<string, string>();   // код -> токен
        static readonly Dictionary<string, string> names = new Dictionary<string, string>();    // код -> название
        static readonly Dictionary<string, DateTime> autoSent = new Dictionary<string, DateTime>();

        public static bool Configured { get { return !string.IsNullOrEmpty(Server) && !string.IsNullOrEmpty(Nick); } }
        static string AvatarFile { get { return Path.Combine(Store.Dir, "avatar.txt"); } }

        public static void Start()
        {
            sync = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            Server = NormalizeServer(Store.Get("chatServer", ""));
            Nick = Store.Get("chatNick", "").Trim();
            UserId = Store.Get("chatUserId", "");
            if (UserId.Length < 16)
            {
                UserId = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N").Substring(0, 8);
                Store.Settings["chatUserId"] = UserId; Store.Save();
            }
            try { Avatar = File.Exists(AvatarFile) ? File.ReadAllText(AvatarFile, Encoding.ASCII).Trim() : null; } catch { Avatar = null; }
            tokens.Clear(); names.Clear();
            foreach (string part in Store.Get("chatGroups", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] p = part.Split('|');
                if (p.Length != 3) continue;
                tokens[p[0]] = p[2]; names[p[0]] = Uri.UnescapeDataString(p[1]);
            }
            Connect();
        }

        static void Connect()
        {
            Disconnect();
            if (!Configured) { Fire(); return; }
            Uri u;
            if (!Uri.TryCreate(Server, UriKind.Absolute, out u) || (u.Scheme != "ws" && u.Scheme != "wss")) { Notice2("Server URL must start with ws:// or wss://", true); return; }
            link = new RelayLink(Server);
            RelayLink mine = link;
            link.Connection += delegate(bool up) { Post(delegate { if (link != mine) return; Connected = up; if (up) { LastError = null; ProbeKind = 0; Hello(); } Fire(); }); };
            link.Failed += delegate(string why) { Post(delegate { if (link != mine) return; LastError = why; if (!probing && (why.Contains("starting up") || why.Contains("no answer yet"))) Wake(null); Fire(); }); };
            link.Received += delegate(Dictionary<string, object> d) { Post(delegate { if (link == mine) Handle(d); }); };
            link.Start();
        }

        static void Disconnect()
        {
            if (link != null) { link.Stop(); link = null; }
            Connected = false; LastError = null;
        }

        public static void Stop() { Disconnect(); }

        // ---- состояние сервера: проверка /healthz и «будильник» для бесплатного хостинга
        public static int ProbeKind;          // 0 нет, 1 проверяем, 2 отвечает, 3 спит/просыпается, 4 недоступен
        public static int ProbeMs; public static string ProbeMsg; static DateTime ProbeAt, wakeStart;
        static volatile bool probing;
        public static bool Probing { get { return probing; } }

        static int ProbeOnce(string wsUrl, out int ms, out string msg)
        {
            ms = 0; msg = null;
            Uri u;
            if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out u)) { msg = "bad address"; return 4; }
            string http = (u.Scheme == "wss" ? "https://" : "http://") + u.Authority + "/healthz";
            DateTime t0 = DateTime.Now;
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(http);
                r.Timeout = 12000; r.UserAgent = "NamazBar";
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                {
                    ms = (int)(DateTime.Now - t0).TotalMilliseconds;
                    return resp.StatusCode == HttpStatusCode.OK ? 2 : 3;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse hr = ex.Response as HttpWebResponse;
                if (hr != null)
                {
                    int c = (int)hr.StatusCode;
                    if (c == 502 || c == 503 || c == 504 || c == 429) return 3;
                    msg = c == 404 ? "this address is not a NamazBar server (404)" : "HTTP " + c; return 4;
                }
                if (ex.Status == WebExceptionStatus.Timeout) return 3;
                if (ex.Status == WebExceptionStatus.NameResolutionFailure) { msg = "address not found (DNS)"; return 4; }
                msg = RelayLink.Friendly(ex.Message); return 4;
            }
            catch (Exception ex) { msg = RelayLink.Friendly(ex.Message); return 4; }
        }

        // Проверить сервер и, если он спит, будить запросами до 90 с; ответил — сразу подключаемся
        public static void Wake(string server)
        {
            string target = NormalizeServer(string.IsNullOrEmpty(server) ? Server : server);
            if (string.IsNullOrEmpty(target) || probing) return;
            probing = true; wakeStart = DateTime.Now; ProbeAt = wakeStart; ProbeKind = 1; ProbeMsg = null; Fire();
            Thread t = new Thread(delegate()
            {
                try
                {
                    try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
                    while (true)
                    {
                        int ms; string msg; int k = ProbeOnce(target, out ms, out msg);
                        bool late = (DateTime.Now - wakeStart).TotalSeconds > 90;
                        if (k == 3 && late) { k = 4; msg = "no answer after 90 s"; }
                        int kk = k, mm = ms; string mg = msg;
                        Post(delegate
                        {
                            ProbeKind = kk; ProbeMs = mm; ProbeMsg = mg; ProbeAt = DateTime.Now;
                            if (kk == 2 && target == Server && !Connected) Connect();
                            Fire();
                        });
                        if (k != 3) break;
                        Thread.Sleep(3000);
                    }
                }
                finally { probing = false; Post(delegate { Fire(); }); }
            });
            t.IsBackground = true; t.Name = "chat-wake"; t.Start();
        }

        // kind: 0 серый, 1 зелёный, 2 жёлтый, 3 красный
        public static void Status(out int kind, out string text)
        {
            bool fresh = ProbeKind != 0 && (DateTime.Now - ProbeAt).TotalSeconds < 60;
            if (Connected) { kind = 1; text = ChatT.T("srvActive") + (fresh && ProbeKind == 2 && ProbeMs > 0 ? "  ·  " + ProbeMs + " ms" : ""); return; }
            if (probing)
            {
                kind = 2;
                text = ProbeKind == 3 ? ChatT.T("srvSleep") + "  " + (int)(DateTime.Now - wakeStart).TotalSeconds + " s" : ChatT.T("srvChecking");
                return;
            }
            if (!Configured && !fresh) { kind = 0; text = ChatT.T("setup"); return; }
            if (fresh && ProbeKind == 4) { kind = 3; text = ChatT.T("srvDown") + (ProbeMsg != null ? " - " + ProbeMsg : ""); return; }
            if (fresh && ProbeKind == 2) { kind = 2; text = ChatT.T("srvConnecting"); return; }
            string e = LastError;
            if (!string.IsNullOrEmpty(e))
            {
                if (e.Contains("starting up") || e.Contains("no answer yet")) { kind = 2; text = ChatT.T("srvSleep"); }
                else { kind = 3; text = ChatT.T("srvDown") + " - " + e; }
                return;
            }
            kind = 2; text = ChatT.T("srvConnecting");
        }
        public static string StatusText() { int k; string t; Status(out k, out t); return t; }

        // «example.onrender.com», «https://…» и «wss://…» без пути приводим к рабочему wss://…/ws
        public static string NormalizeServer(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return s;
            if (s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) s = "wss://" + s.Substring(8);
            else if (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) s = "ws://" + s.Substring(7);
            else if (s.IndexOf("://", StringComparison.Ordinal) < 0) s = "wss://" + s;
            Uri u;
            if (Uri.TryCreate(s, UriKind.Absolute, out u) && (u.AbsolutePath == "/" || u.AbsolutePath.Length == 0) && u.Query.Length == 0)
                s = s.TrimEnd('/') + "/ws";
            return s;
        }

        public static void Configure(string server, string nick)
        {
            Store.Settings["chatServer"] = NormalizeServer(server);
            Store.Settings["chatNick"] = (nick ?? "").Trim();
            Store.Save();
            string oldServer = Server;
            Server = Store.Get("chatServer", ""); Nick = Store.Get("chatNick", "");
            if (link == null || oldServer != Server) Connect();
            else Send(new Dictionary<string, object> { { "type", "updateProfile" }, { "nick", Nick }, { "avatar", Avatar } });
            Fire();
        }

        public static void SetAvatar(string dataUrl)
        {
            Avatar = dataUrl;
            try { Directory.CreateDirectory(Store.Dir); File.WriteAllText(AvatarFile, dataUrl ?? "", Encoding.ASCII); } catch { }
            if (Connected) Send(new Dictionary<string, object> { { "type", "updateProfile" }, { "nick", Nick }, { "avatar", Avatar } });
            Fire();
        }

        static void Post(Action a) { sync.Post(delegate { try { a(); } catch (Exception ex) { Store.Log("chat: " + ex); } }, null); }
        static void Fire() { Action a = Changed; if (a != null) a(); }
        static void Notice2(string t, bool err) { Action<string, bool> a = Notice; if (a != null) a(t, err); }

        static void Send(Dictionary<string, object> m) { RelayLink l = link; if (l != null && Connected) l.Send(m); }

        static void Hello()
        {
            List<object> gs = new List<object>();
            foreach (KeyValuePair<string, string> kv in tokens)
                gs.Add(new Dictionary<string, object> { { "code", kv.Key }, { "name", names.ContainsKey(kv.Key) ? names[kv.Key] : kv.Key }, { "token", kv.Value } });
            link.Send(new Dictionary<string, object> {
                { "type", "hello" }, { "userId", UserId }, { "nick", Nick }, { "avatar", Avatar }, { "groups", gs } });
        }

        static void SaveGroups()
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in tokens)
                sb.Append(kv.Key).Append('|').Append(Uri.EscapeDataString(names.ContainsKey(kv.Key) ? names[kv.Key] : kv.Key)).Append('|').Append(kv.Value).Append(';');
            Store.Settings["chatGroups"] = sb.ToString(); Store.Save();
        }

        // ---- команды ----
        public static void CreateGroup(string name) { Send(new Dictionary<string, object> { { "type", "createGroup" }, { "name", name } }); }
        public static void ClearChat(string code) { Send(new Dictionary<string, object> { { "type", "clearChat" }, { "code", code } }); }
        public static void RequestJoin(string code) { Send(new Dictionary<string, object> { { "type", "joinRequest" }, { "code", (code ?? "").Trim().ToUpperInvariant() } }); }
        public static void Decide(string requestId, bool approve) { Send(new Dictionary<string, object> { { "type", "decide" }, { "requestId", requestId }, { "approve", approve } }); }
        public static void Leave(string code) { Send(new Dictionary<string, object> { { "type", "leave" }, { "code", code } }); }
        public static void SendPreset(string code, string preset)
        {
            Send(new Dictionary<string, object> { { "type", "send" }, { "code", code }, { "kind", "preset" }, { "preset", preset } });
        }

        // перерыв на намаз начался (экран блокируется): позвать группу, если включено
        public static void OnBreakStart()
        {
            if (!Connected || Store.Get("chatAuto", "0") != "1") return;
            foreach (ChatGroup g in Groups)
            {
                DateTime last;
                if (autoSent.TryGetValue(g.Code, out last) && (DateTime.Now - last).TotalMinutes < 10) continue;
                autoSent[g.Code] = DateTime.Now;
                SendPreset(g.Code, "together");
            }
        }

        // ---- входящие ----
        static ChatGroup Find(string code) { foreach (ChatGroup g in Groups) if (g.Code == code) return g; return null; }

        static ChatMember ParseMember(Dictionary<string, object> m)
        {
            return new ChatMember { UserId = J.Str(m, "userId"), Nick = J.Str(m, "nick") ?? "?", Avatar = J.Str(m, "avatar"), Online = J.Bool(m, "online") };
        }

        static ChatMsg ParseMsg(Dictionary<string, object> m)
        {
            long ts = J.Long(m, "ts");
            return new ChatMsg {
                Id = J.Str(m, "id"), Code = J.Str(m, "code"), From = J.Str(m, "from"), Nick = J.Str(m, "nick"),
                Kind = J.Str(m, "kind"), Preset = J.Str(m, "preset"), Text = J.Str(m, "text") ?? "",
                Time = ts > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ts).ToLocalTime() : DateTime.Now };
        }

        static ChatGroup ApplyGroup(Dictionary<string, object> g)
        {
            string code = J.Str(g, "code");
            ChatGroup grp = Find(code);
            if (grp == null) { grp = new ChatGroup { Code = code }; Groups.Add(grp); }
            grp.Name = J.Str(g, "name") ?? code;
            grp.Owner = J.Str(g, "owner");
            names[code] = grp.Name;
            grp.Members.Clear();
            foreach (Dictionary<string, object> m in J.List(g, "members")) grp.Members.Add(ParseMember(m));
            if (grp.Messages.Count == 0)
                foreach (Dictionary<string, object> m in J.List(g, "history")) { ChatMsg x = ParseMsg(m); if (x.Code == null) x.Code = code; grp.Messages.Add(x); }
            DateTime cut = CurrentCutoff();
            if (cut != DateTime.MinValue) grp.Messages.RemoveAll(delegate(ChatMsg m) { return m.Time <= cut; });
            return grp;
        }

        static void Handle(Dictionary<string, object> d)
        {
            string type = J.Str(d, "type");
            switch (type)
            {
                case "welcome":
                {
                    List<string> keep = new List<string>();
                    foreach (Dictionary<string, object> g in J.List(d, "groups")) keep.Add(ApplyGroup(g).Code);
                    Groups.RemoveAll(delegate(ChatGroup g) { return !keep.Contains(g.Code); });
                    foreach (string c in new List<string>(tokens.Keys)) if (!keep.Contains(c)) { tokens.Remove(c); names.Remove(c); }
                    SaveGroups();
                    SendClears(CurrentCutoff());   // сервер мог хранить историю до границы
                    break;
                }
                case "historyCleared":
                {
                    ChatGroup g = Find(J.Str(d, "code"));
                    long before = J.Long(d, "before");
                    if (g != null && before > 0)
                    {
                        DateTime cut = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(before).ToLocalTime();
                        g.Messages.RemoveAll(delegate(ChatMsg m) { return m.Time <= cut; });
                    }
                    break;
                }
                case "groupCreated":
                case "joined":
                {
                    ChatGroup g = ApplyGroup(J.Obj(d, "group"));
                    tokens[g.Code] = J.Str(d, "token"); SaveGroups();
                    Notice2(string.Format(ChatT.T("joined"), g.Name) + "  —  " + string.Format(ChatT.T("code"), g.Code), false);
                    break;
                }
                case "members":
                {
                    ChatGroup g = Find(J.Str(d, "code"));
                    if (g == null) break;
                    g.Owner = J.Str(d, "owner");
                    g.Members.Clear();
                    foreach (Dictionary<string, object> m in J.List(d, "members")) g.Members.Add(ParseMember(m));
                    break;
                }
                case "message":
                {
                    ChatMsg m = ParseMsg(d);
                    ChatGroup g = Find(m.Code);
                    if (g == null) break;
                    g.Messages.Add(m);
                    if (g.Messages.Count > 200) g.Messages.RemoveAt(0);
                    if (m.From != UserId)
                    {
                        if (g != Active) g.Unread++;
                        Action<ChatGroup, ChatMsg> cb = Incoming; if (cb != null) cb(g, m);
                    }
                    break;
                }
                case "joinRequest":
                {
                    Dictionary<string, object> f = J.Obj(d, "from");
                    ChatGroup g = Find(J.Str(d, "code"));
                    Action<JoinReq> cb = JoinRequested;
                    if (cb != null && f != null) cb(new JoinReq { RequestId = J.Str(d, "requestId"), Code = J.Str(d, "code"), GroupName = g != null ? g.Name : J.Str(d, "code"), From = ParseMember(f) });
                    break;
                }
                case "joinSettled": { Action<string> cb = JoinSettled; if (cb != null) cb(J.Str(d, "requestId")); break; }
                case "joinPending": Notice2(string.Format(ChatT.T("waiting"), J.Str(d, "name")), false); break;
                case "joinDenied": Notice2(ChatT.T("denied"), true); break;
                case "left": { ChatGroup g = Find(J.Str(d, "code")); if (g != null) { Groups.Remove(g); tokens.Remove(g.Code); names.Remove(g.Code); SaveGroups(); if (Active == g) Active = null; } break; }
                case "error":
                {
                    string em = J.Str(d, "message") ?? "error";
                    if (em.IndexOf("within the last 24 hours", StringComparison.Ordinal) < 0) Notice2(em, true);   // расхождение часов при очистке — не показываем
                    break;
                }
            }
            Fire();
        }
    }

    // Связка с приложением: уведомления о сообщениях и запросах на вступление
    static class ChatHooks
    {
        public static void Init(Func<Rectangle> toastAnchor)
        {
            ChatToast.Anchor = toastAnchor;
            Chat.Incoming += delegate(ChatGroup g, ChatMsg m)
            {
                Chat.RememberLine(g, m);
                bool watching = ChatDock.IsWatching(g);
                if (!watching) ChatToast.Message(g, m);
                Beep();
            };
            Chat.JoinRequested += delegate(JoinReq r) { ChatToast.Request(r); Beep(); };
            Chat.JoinSettled += delegate(string id) { ChatToast.Settled(id); };
            Chat.Notice += delegate(string text, bool error) { if (!ChatDock.IsOpen) ChatToast.Info(text, error); };
            Chat.Start();
            System.Windows.Forms.Timer prune = new System.Windows.Forms.Timer { Interval = 30000 };
            prune.Tick += delegate { Chat.PruneTick(); };
            prune.Start();
        }
        static void Beep() { if (Store.Get("chatSound", "1") != "0") System.Media.SystemSounds.Asterisk.Play(); }
    }

    // ───────────────────────── Тексты ─────────────────────────
    static class ChatT
    {
        // 0 = uz-lotin, 1 = uz-kirill, 2 = ru, 3 = en — тот же порядок, что в Lang
        static readonly Dictionary<string, string[]> t = new Dictionary<string, string[]> {
            { "menu",     new[] { "Namoz do'stlari (chat)…", "Намоз дўстлари (чат)…", "Чат для совместного намаза…", "Prayer group chat…" } },
            { "title",    new[] { "NamazBar — chat", "NamazBar — чат", "NamazBar — чат", "NamazBar — chat" } },
            { "nick",     new[] { "Taxallus", "Тахаллус", "Ник", "Nickname" } },
            { "server",   new[] { "Server manzili (wss://…)", "Сервер манзили (wss://…)", "Адрес сервера (wss://…)", "Server address (wss://…)" } },
            { "save",     new[] { "Saqlash", "Сақлаш", "Сохранить", "Save" } },
            { "groups",   new[] { "Guruhlar", "Гуруҳлар", "Группы", "Groups" } },
            { "create",   new[] { "Yaratish", "Яратиш", "Создать", "Create" } },
            { "join",     new[] { "Qo'shilish", "Қўшилиш", "Войти", "Join" } },
            { "leave",    new[] { "Chiqish", "Чиқиш", "Выйти", "Leave" } },
            { "newName",  new[] { "Guruh nomi (masalan: Ofis)", "Гуруҳ номи (масалан: Офис)", "Название группы (например: Офис)", "Group name (e.g. Office)" } },
            { "enterCode",new[] { "Guruh kodi", "Гуруҳ коди", "Код группы", "Group code" } },
            { "connected",new[] { "Ulangan", "Уланган", "Подключено", "Connected" } },
            { "srvActive",     new[] { "Faol", "Фаол", "Активен", "Active" } },
            { "srvConnecting", new[] { "Ulanmoqda…", "Уланмоқда…", "Подключается…", "Connecting…" } },
            { "srvSleep",      new[] { "Uyquda - uyg‘otilmoqda…", "Уйқуда - уйғотилмоқда…", "В спячке - будим…", "Asleep - waking up…" } },
            { "srvDown",       new[] { "Mavjud emas", "Мавжуд эмас", "Недоступен", "Unreachable" } },
            { "srvChecking",   new[] { "Tekshirilmoqda…", "Текширилмоқда…", "Проверка…", "Checking…" } },
            { "srvWake",       new[] { "Tekshirish", "Текшириш", "Проверить / разбудить", "Check / wake" } },
            { "connecting",new[] { "Ulanmoqda…", "Уланмоқда…", "Подключение…", "Connecting…" } },
            { "setup",    new[] { "Taxallus va server manzilini kiriting", "Тахаллус ва сервер манзилини киритинг", "Укажите ник и адрес сервера", "Set a nickname and the server address" } },
            { "reqText",  new[] { "{0} «{1}» guruhiga qo'shilmoqchi", "{0} «{1}» гуруҳига қўшилмоқчи", "{0} хочет вступить в «{1}»", "{0} wants to join “{1}”" } },
            { "allow",    new[] { "Qabul qilish", "Қабул қилиш", "Принять", "Allow" } },
            { "deny",     new[] { "Rad etish", "Рад этиш", "Отклонить", "Deny" } },
            { "auto",     new[] { "Tanaffus boshlanganda «Birga o'qiymiz» yuborish", "Танаффус бошланганда «Бирга ўқиймиз» юбориш", "При начале перерыва отправлять «Давайте вместе»", "Send “Let's do namaz together” when the break starts" } },
            { "sound",    new[] { "Ovoz", "Овоз", "Звук", "Sound" } },
            { "waiting",  new[] { "Tasdiqlash kutilmoqda: {0}", "Тасдиқлаш кутилмоқда: {0}", "Ожидание подтверждения: {0}", "Waiting for approval: {0}" } },
            { "denied",   new[] { "So'rov rad etildi", "Сўров рад этилди", "Запрос отклонён", "Request declined" } },
            { "joined",   new[] { "Siz «{0}» guruhidasiz", "Сиз «{0}» гуруҳидасиз", "Вы в группе «{0}»", "You are in “{0}”" } },
            { "code",     new[] { "Kod: {0}", "Код: {0}", "Код: {0}", "Code: {0}" } },
            { "copy",     new[] { "Kodni nusxalash", "Кодни нусхалаш", "Копировать код", "Copy code" } },
            { "noGroup",  new[] { "Guruh yarating yoki kod bilan qo'shiling", "Гуруҳ яратинг ёки код билан қўшилинг", "Создайте группу или войдите по коду", "Create a group or join with a code" } },
            { "online",   new[] { "{0} onlayn", "{0} онлайн", "{0} онлайн", "{0} online" } },
            { "pick",     new[] { "Rasm tanlash", "Расм танлаш", "Выбрать фото", "Choose picture" } },
            { "you",      new[] { "Siz", "Сиз", "Вы", "You" } },
            { "bad",      new[] { "Rasmni ochib bo'lmadi", "Расмни очиб бўлмади", "Не удалось открыть изображение", "Could not open the image" } },
            { "quick",    new[] { "Tezkor xabarlar", "Тезкор хабарлар", "Быстрые сообщения", "Quick messages" } },
            { "noGroupHint", new[] { "Kodni do'stlaringizga yuboring: ular qo'shilishni so'raydi, siz tasdiqlaysiz.", "Кодни дўстларингизга юборинг: улар қўшилишни сўрайди, сиз тасдиқлайсиз.", "Отправьте код друзьям: они попросятся в группу, а вы подтвердите.", "Share the code with friends: they ask to join and you approve." } },
            { "settings", new[] { "Sozlamalar", "Созламалар", "Настройки", "Settings" } },
            { "chatCleared", new[] { "Chat tozalandi", "Чат тозаланди", "Чат очищен", "Chat cleared" } },
            { "copied",   new[] { "Kod nusxalandi", "Код нусхаланди", "Код скопирован", "Code copied" } },
            { "today",    new[] { "Bugun", "Бугун", "Сегодня", "Today" } },
            { "yesterday", new[] { "Kecha", "Кеча", "Вчера", "Yesterday" } },
            { "setupBtn", new[] { "Chatni sozlash", "Чатни созлаш", "Настроить чат", "Set up chat" } },
            { "notConn",  new[] { "Serverga ulanmagan. Bir ozdan keyin urinib ko'ring", "Серверга уланмаган. Бир оздан кейин уриниб кўринг", "Нет соединения с сервером. Попробуйте чуть позже", "Not connected to the server. Try again in a moment" } },
            { "needName", new[] { "Guruh nomini kiriting", "Гуруҳ номини киритинг", "Введите название группы", "Enter a group name" } },
            { "needCode", new[] { "Guruh kodini kiriting", "Гуруҳ кодини киритинг", "Введите код группы", "Enter the group code" } },
            { "creating", new[] { "Guruh yaratilmoqda…", "Гуруҳ яратилмоқда…", "Создаём группу…", "Creating the group…" } },
            { "sendingReq", new[] { "So'rov yuborildi, tasdiqlashni kuting…", "Сўров юборилди, тасдиқлашни кутинг…", "Запрос отправлен, ждём подтверждения…", "Request sent, waiting for approval…" } },
            { "openChat", new[] { "Chatni ochish", "Чатни очиш", "Открыть чат", "Open chat" } },
            { "chatTab",  new[] { "Chat", "Чат", "Чат", "Chat" } },
            { "members",  new[] { "A'zolar", "Аъзолар", "Участники", "Members" } },
            { "offlineN", new[] { "{0} oflayn", "{0} офлайн", "{0} не в сети", "{0} offline" } },
            { "stOn",     new[] { "onlayn", "онлайн", "в сети", "online" } },
            { "stOff",    new[] { "oflayn", "офлайн", "не в сети", "offline" } },
            { "send",     new[] { "Xabar yuborish", "Хабар юбориш", "Отправить сообщение", "Send a message" } },
            { "openWin",  new[] { "Chat oynasini ochish…", "Чат ойнасини очиш…", "Открыть окно чата…", "Open chat window…" } },
            { "ok",       new[] { "OK", "OK", "OK", "OK" } },
            { "cancel",   new[] { "Bekor qilish", "Бекор қилиш", "Отмена", "Cancel" } },
        };
        public static string T(string k) { return t[k][Lang.Cur]; }

        public static readonly string[] PresetIds = { "together", "coming", "wait", "where", "ready", "done" };
        static readonly Dictionary<string, string[]> presets = new Dictionary<string, string[]> {
            { "together", new[] { "Birga namoz o'qiymiz", "Бирга намоз ўқиймиз", "Давайте совершим намаз вместе", "Let's do namaz together" } },
            { "coming",   new[] { "Namoz uchun oldingizga kelyapman", "Намоз учун олдингизга келяпман", "Иду к вам совершить намаз", "I'm coming to you to perform namaz" } },
            { "wait",     new[] { "Meni 5 daqiqa kuting", "Мени 5 дақиқа кутинг", "Подождите меня 5 минут", "Wait for me, 5 minutes" } },
            { "where",    new[] { "Qayerda o'qiymiz?", "Қаерда ўқиймиз?", "Где совершаем намаз?", "Where are we praying?" } },
            { "ready",    new[] { "Men tayyorman", "Мен тайёрман", "Я готов", "I'm ready" } },
            { "done",     new[] { "Men o'qib bo'ldim", "Мен ўқиб бўлдим", "Я уже совершил намаз", "I have already prayed" } },
        };
        public static string Preset(string id, string fallback)
        {
            string[] v; return id != null && presets.TryGetValue(id, out v) ? v[Lang.Cur] : fallback;
        }
    }

    // ───────────────────────── Аватары ─────────────────────────
    static class Avatars
    {
        static readonly Dictionary<string, Image> cache = new Dictionary<string, Image>();
        static readonly Color[] tints = { Color.FromArgb(66, 133, 244), Color.FromArgb(219, 68, 55), Color.FromArgb(15, 157, 88), Color.FromArgb(171, 71, 188), Color.FromArgb(0, 137, 123), Color.FromArgb(244, 124, 32) };

        static Image Decode(string dataUrl)
        {
            if (string.IsNullOrEmpty(dataUrl)) return null;
            Image img;
            if (cache.TryGetValue(dataUrl, out img)) return img;
            try
            {
                int i = dataUrl.IndexOf(',');
                using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(dataUrl.Substring(i + 1))))
                using (Image src = Image.FromStream(ms)) img = new Bitmap(src);
            }
            catch { img = null; }
            if (cache.Count > 200) cache.Clear();
            cache[dataUrl] = img;
            return img;
        }

        public static void Draw(Graphics g, Rectangle r, string userId, string nick, string dataUrl)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddEllipse(r);
                Image img = Decode(dataUrl);
                if (img != null)
                {
                    System.Drawing.Region oldClip = g.Clip;
                    g.SetClip(p, CombineMode.Intersect);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(img, r);
                    g.Clip = oldClip;
                }
                else
                {
                    int h = 0; foreach (char c in userId ?? nick ?? "") h = h * 31 + c;
                    using (SolidBrush b = new SolidBrush(tints[Math.Abs(h) % tints.Length])) g.FillPath(b, p);
                    string ch = string.IsNullOrEmpty(nick) ? "?" : nick.Substring(0, 1).ToUpperInvariant();
                    using (Font f = new Font("Segoe UI", r.Height * 0.38f, FontStyle.Bold, GraphicsUnit.Pixel))
                        TextRenderer.DrawText(g, ch, f, r, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                using (Pen pen = new Pen(Color.FromArgb(70, 0, 0, 0), 1f)) g.DrawPath(pen, p);
            }
            g.SmoothingMode = old;
        }

        // Выбранный файл -> квадрат 96x96 JPEG в data URL (лимит сервера 32 КБ)
        public static string FromFile(string path)
        {
            using (Image src = Image.FromFile(path))
            {
                int side = Math.Min(src.Width, src.Height);
                Rectangle crop = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                ImageCodecInfo jpeg = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") jpeg = c;
                foreach (int size in new[] { 96, 64 })
                    using (Bitmap bmp = new Bitmap(size, size))
                    {
                        using (Graphics g = Graphics.FromImage(bmp))
                        {
                            g.Clear(Color.White);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.DrawImage(src, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
                        }
                        using (MemoryStream ms = new MemoryStream())
                        using (EncoderParameters ep = new EncoderParameters(1))
                        {
                            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                            bmp.Save(ms, jpeg, ep);
                            string url = "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
                            if (url.Length < 30000) return url;
                        }
                    }
            }
            throw new InvalidOperationException("image too large");
        }
    }

    // ───────────────────────── Мелкие диалоги ─────────────────────────
    static class Dlg
    {
        public static string Ask(IWin32Window owner, string title, string label, string def)
        {
            using (Form f = new Form())
            {
                f.Text = title; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterParent;
                f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false;
                f.Font = Fonts.Get("NB Sans", 10f, "Segoe UI", FontStyle.Regular);
                float k; using (Graphics g = f.CreateGraphics()) k = g.DpiX / 96f;
                f.ClientSize = new Size((int)(360 * k), (int)(130 * k));
                Label l = new Label { Text = label, Left = (int)(14 * k), Top = (int)(14 * k), Width = (int)(330 * k), Height = (int)(22 * k) };
                TextBox tb = new TextBox { Text = def, Left = (int)(14 * k), Top = (int)(40 * k), Width = (int)(330 * k) };
                Button ok = new Button { Text = ChatT.T("ok"), DialogResult = DialogResult.OK, Left = (int)(170 * k), Top = (int)(84 * k), Width = (int)(84 * k), Height = (int)(30 * k) };
                Button no = new Button { Text = ChatT.T("cancel"), DialogResult = DialogResult.Cancel, Left = (int)(260 * k), Top = (int)(84 * k), Width = (int)(84 * k), Height = (int)(30 * k) };
                f.Controls.AddRange(new Control[] { l, tb, ok, no });
                f.AcceptButton = ok; f.CancelButton = no;
                return f.ShowDialog(owner) == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }
    }

    // ───────────────────────── Всплывающие уведомления ─────────────────────────
    class ChatToast : Form
    {
        static readonly List<ChatToast> open = new List<ChatToast>();
        readonly float k;
        readonly string line1, line2;
        readonly ChatMember who;
        public string RequestId;
        readonly System.Windows.Forms.Timer life = new System.Windows.Forms.Timer();

        ChatToast(ChatMember who, string line1, string line2, bool hasButtons, int seconds)
        {
            this.who = who; this.line1 = line1; this.line2 = line2;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
            TopMost = true; DoubleBuffered = true; BackColor = Ui.Panel;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            Size = new Size((int)(360 * k), (int)((hasButtons ? 112 : 72) * k));
            if (hasButtons)
            {
                Button a = MakeBtn(ChatT.T("allow"), 14, true), d = MakeBtn(ChatT.T("deny"), 130, false);
                a.Click += delegate { Chat.Decide(RequestId, true); Close(); };
                d.Click += delegate { Chat.Decide(RequestId, false); Close(); };
                Controls.Add(a); Controls.Add(d);
            }
            else Click += delegate { ChatDock.ShowDock(); Close(); };
            if (seconds > 0) { life.Interval = seconds * 1000; life.Tick += delegate { Close(); }; life.Start(); }
        }

        Button MakeBtn(string text, int x, bool primary)
        {
            Button b = new Button();
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? Ui.Accent : Ui.Raised; b.ForeColor = primary ? Ui.OnAccent : Ui.Text;
            b.Bounds = new Rectangle((int)(x * k), (int)(70 * k), (int)(108 * k), (int)(30 * k));
            b.Font = Fonts.Get("NB Sans Medium", 9f, "Segoe UI Semibold", FontStyle.Regular);
            return b;
        }

        public static void Message(ChatGroup g, ChatMsg m)
        {
            ChatMember who = new ChatMember { UserId = m.From, Nick = m.Nick, Avatar = null };
            foreach (ChatMember x in g.Members) if (x.UserId == m.From) who = x;
            Display(new ChatToast(who, m.Nick + " · " + g.Name, m.Display, false, 10));
        }
        public static void Request(JoinReq r)
        {
            ChatToast t = new ChatToast(r.From, string.Format(ChatT.T("reqText"), r.From.Nick, r.GroupName), "", true, 0);
            t.RequestId = r.RequestId;
            Display(t);
        }
        public static void Info(string text, bool error) { Display(new ChatToast(null, "NamazBar", text, false, error ? 8 : 5)); }
        public static void Settled(string requestId)
        {
            foreach (ChatToast t in open.ToArray()) if (t.RequestId == requestId) t.Close();
        }

        static void Display(ChatToast t)
        {
            open.Add(t);
            t.FormClosed += delegate { open.Remove(t); Restack(); };
            Restack();
            t.Show();
        }
        // Где стоит виджет NamazBar на панели задач — уведомления выстраиваются слева, прямо над ним
        public static Func<Rectangle> Anchor;

        static void Restack()
        {
            Rectangle anchor = Rectangle.Empty;
            try { if (Anchor != null) anchor = Anchor(); } catch { }
            if (anchor.Width <= 0 || anchor.Height <= 0)   // виджет не найден — правый нижний угол
            {
                Rectangle w0 = Screen.PrimaryScreen.WorkingArea;
                int y0 = w0.Bottom - 12;
                foreach (ChatToast t in open) { y0 -= t.Height + 8; t.Location = new Point(w0.Right - t.Width - 12, y0); }
                return;
            }
            Screen scr = Screen.FromRectangle(anchor);
            Rectangle wa = scr.WorkingArea;
            bool above = anchor.Top > scr.Bounds.Top + scr.Bounds.Height / 2;   // панель внизу — уведомления над ней
            int y = above ? Math.Min(anchor.Top, wa.Bottom) - 8 : Math.Max(anchor.Bottom, wa.Top) + 8;
            foreach (ChatToast t in open)
            {
                int x = Math.Max(wa.Left + 8, Math.Min(anchor.Left, wa.Right - t.Width - 8));
                if (above) { y -= t.Height; t.Location = new Point(x, y); y -= 8; }
                else { t.Location = new Point(x, y); y += t.Height + 8; }
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000; return cp; }   // TOOLWINDOW | NOACTIVATE
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (Pen p = new Pen(Ui.Border, 1f)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int av = (int)(44 * k), pad = (int)(14 * k);
            if (who != null) Avatars.Draw(g, new Rectangle(pad, pad, av, av), who.UserId, who.Nick, who.Avatar);
            int x = who != null ? pad + av + (int)(10 * k) : pad;
            Rectangle r1 = new Rectangle(x, pad - 2, Width - x - pad, (int)(24 * k));
            using (Font f = Fonts.Get("NB Sans Bold", 9.5f, "Segoe UI", FontStyle.Bold))
                TextRenderer.DrawText(g, line1, f, r1, Ui.Text, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (line2.Length > 0)
            {
                Rectangle r2 = new Rectangle(x, pad + (int)(22 * k), Width - x - pad, (int)(32 * k));
                using (Font f = Fonts.Get("NB Sans", 9.5f, "Segoe UI", FontStyle.Regular))
                    TextRenderer.DrawText(g, line2, f, r2, Ui.Dim, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
        }
    }

    // ───────────────────────── Чат прямо в меню ─────────────────────────
    // Строка меню: заголовок группы или участник (круглый аватар, ник, справа точка: зелёная — онлайн, серая — нет)
    class ChatRowItem : ToolStripMenuItem
    {
        static Font fHead, fNick;
        readonly ChatMember member;
        readonly string head;
        readonly float k;

        public ChatRowItem(ChatMember m, string head, float k)
        {
            this.member = m; this.head = head; this.k = k;
            AutoSize = false; Padding = Padding.Empty;
            Size = new Size((int)(270 * k), (int)((head != null ? 30 : 34) * k));
            if (fHead == null) fHead = Fonts.Get("NB Sans Bold", 9.5f, "Segoe UI", FontStyle.Bold);
            if (fNick == null) fNick = Fonts.Get("NB Sans", 10.5f, "Segoe UI", FontStyle.Regular);
        }

        public override Size GetPreferredSize(Size constrainingSize) { return Size; }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            TextFormatFlags tf = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            if (head != null)
            {
                TextRenderer.DrawText(g, head, fHead, new Rectangle((int)(14 * k), 0, Width - (int)(28 * k), Height), Palette.Gold, tf);
                using (Pen p = new Pen(Color.FromArgb(70, Palette.Gold))) g.DrawLine(p, (int)(14 * k), Height - 1, Width - (int)(14 * k), Height - 1);
                return;
            }
            int av = (int)(24 * k);
            Rectangle ar = new Rectangle((int)(14 * k), (Height - av) / 2, av, av);
            Avatars.Draw(g, ar, member.UserId, member.Nick, member.Avatar);
            if (!member.Online) using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, MenuUi.Surface))) g.FillEllipse(dim, ar);
            int dot = (int)(10 * k);
            Rectangle dr = new Rectangle(Width - (int)(24 * k), (Height - dot) / 2, dot, dot);
            using (SolidBrush b = new SolidBrush(member.Online ? Color.FromArgb(70, 200, 120) : Color.FromArgb(120, 124, 130))) g.FillEllipse(b, dr);
            Color fg = member.Online ? Palette.Ivory : Color.FromArgb(150, Palette.Ivory);
            TextRenderer.DrawText(g, member.Nick, fNick, new Rectangle(ar.Right + (int)(10 * k), 0, dr.X - ar.Right - (int)(18 * k), Height), fg, tf);
        }
    }

    static class ChatMenu
    {
        static float Dpi() { using (Bitmap b = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(b)) return g.DpiX / 96f; }

        public static void ShowWindow() { ChatDock.ShowDock(); }

        // Перестраивает выпадающее меню «Чат»: группы с участниками (онлайн сверху, офлайн снизу), быстрые фразы, управление
        public static void Rebuild(ToolStripMenuItem root)
        {
            ToolStripDropDownMenu dd = root.DropDown as ToolStripDropDownMenu;
            if (dd != null) { dd.ShowImageMargin = false; dd.ShowCheckMargin = false; }
            root.DropDownItems.Clear();
            float k = Dpi();

            if (!Chat.Configured)
            {
                root.DropDownItems.Add(MenuUi.Item(ChatT.T("setup"), null, ShowWindow));
                return;
            }
            if (!Chat.Connected)
            {
                ToolStripMenuItem st = MenuUi.Sub(Chat.StatusText(), null);
                st.Enabled = false;
                root.DropDownItems.Add(st);
            }
            foreach (ChatGroup g in Chat.Groups)
            {
                string title = g.Name + "  ·  " + g.OnlineCount + "/" + g.Members.Count;
                root.DropDownItems.Add(new ChatRowItem(null, title, k));
                List<ChatMember> sorted = new List<ChatMember>(g.Members);
                sorted.Sort(delegate(ChatMember a, ChatMember b)
                {
                    if (a.Online != b.Online) return a.Online ? -1 : 1;
                    return string.Compare(a.Nick, b.Nick, StringComparison.CurrentCultureIgnoreCase);
                });
                foreach (ChatMember m in sorted) root.DropDownItems.Add(new ChatRowItem(m, null, k));
            }
            if (Chat.Groups.Count > 0) root.DropDownItems.Add(new ToolStripSeparator());

            if (Chat.Connected)
            {
                foreach (ChatGroup g in Chat.Groups)
                {
                    ChatGroup grp = g;
                    ToolStripMenuItem send = MenuUi.Sub(Chat.Groups.Count > 1 ? ChatT.T("send") + " → " + g.Name : ChatT.T("send"), null);
                    foreach (string id in ChatT.PresetIds)
                    {
                        string pid = id;
                        send.DropDownItems.Add(MenuUi.Item(ChatT.Preset(id, id), null, delegate { Chat.SendPreset(grp.Code, pid); }));
                    }
                    root.DropDownItems.Add(send);
                }
                root.DropDownItems.Add(MenuUi.Item(ChatT.T("create") + " / " + ChatT.T("join") + "…", null, delegate { ChatDock.ShowDock(true); }));
                root.DropDownItems.Add(new ToolStripSeparator());
            }
            root.DropDownItems.Add(MenuUi.Item(ChatT.T("openChat"), null, ShowWindow));
        }
    }

    // ───────────────────────── Дизайн окна чата ─────────────────────────
    // Единая тёмная тема от выбранного скина: фон, панели, золотой акцент, читаемые состояния кнопок.
    static class Ui
    {
        public static readonly float K = Dpi();
        static float Dpi() { using (Bitmap b = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(b)) return g.DpiX / 96f; }
        public static int S(float v) { return (int)Math.Round(v * K); }

        // Нейтральная тема в духе Google: светлая или тёмная — как в настройках Windows, один синий акцент, не зависит от скина
        public static bool Dark = DetectDark();
        static bool DetectDark()
        {
            try { object v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1); return v is int && (int)v == 0; }
            catch { return false; }
        }
        static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }
        public static Color Bg { get { return Dark ? C(32, 33, 36) : C(255, 255, 255); } }           // окно и сообщения
        public static Color Panel { get { return Dark ? C(41, 42, 45) : C(248, 249, 250); } }        // шапка, вкладки, быстрые ответы
        public static Color Raised { get { return Dark ? C(48, 49, 52) : C(241, 243, 244); } }       // чужие сообщения, поля, чипы
        public static Color Border { get { return Dark ? C(60, 64, 67) : C(218, 220, 224); } }
        public static Color Text { get { return Dark ? C(232, 234, 237) : C(32, 33, 36); } }
        public static Color Dim { get { return Dark ? C(154, 160, 166) : C(95, 99, 104); } }
        public static Color Accent { get { return Dark ? C(138, 180, 248) : C(26, 115, 232); } }     // синий Google
        public static Color OnAccent { get { return Dark ? C(32, 33, 36) : C(255, 255, 255); } }
        public static Color MineBubble { get { return Dark ? C(47, 72, 112) : C(211, 227, 253); } }
        public static Color Danger { get { return Dark ? C(242, 139, 130) : C(217, 48, 37); } }
        public static Color Online { get { return Dark ? C(87, 201, 121) : C(30, 142, 62); } }
        public static Color Offline { get { return Dark ? C(128, 134, 139) : C(154, 160, 166); } }

        static Font body, bold, small, big, caps, title;
        public static Font Title { get { if (title == null) title = Fonts.Get("NB Sans Medium", 10.5f, "Segoe UI Semibold", FontStyle.Regular); return title; } }
        public static Font Body { get { if (body == null) body = Fonts.Get("NB Sans", 9.5f, "Segoe UI", FontStyle.Regular); return body; } }
        public static Font Medium { get { if (bold == null) bold = Fonts.Get("NB Sans Medium", 9.5f, "Segoe UI Semibold", FontStyle.Regular); return bold; } }
        public static Font Small { get { if (small == null) small = Fonts.Get("NB Sans", 8f, "Segoe UI", FontStyle.Regular); return small; } }
        public static Font Big { get { if (big == null) big = Fonts.Get("NB Sans Bold", 12f, "Segoe UI", FontStyle.Bold); return big; } }
        public static Font Caps { get { if (caps == null) caps = Fonts.Get("NB Sans Bold", 8f, "Segoe UI", FontStyle.Bold); return caps; } }

        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
        public static void FillRound(Graphics g, Rectangle r, int rad, Color c)
        {
            using (GraphicsPath p = Round(r, rad)) using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
        }
        public static void StrokeRound(Graphics g, Rectangle r, int rad, Color c, float w)
        {
            using (GraphicsPath p = Round(r, rad)) using (Pen pen = new Pen(c, w)) g.DrawPath(pen, p);
        }
        public static Color Fade(Color c, Color behind, double visible) { return View.Mix(behind, c, visible); }
    }

    enum UiKind { Primary, Secondary, Danger, Chip }

    // Кнопка со всеми состояниями (обычная, наведение, нажатие, фокус, недоступна — текст остаётся читаемым)
    class UiButton : Control
    {
        public UiKind Kind;
        bool hover, down;
        public UiButton(string text, UiKind kind)
        {
            Text = text; Kind = kind; TabStop = true; Cursor = Cursors.Hand; Font = Ui.Medium;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            Height = Ui.S(kind == UiKind.Chip ? 28 : 34);
        }
        public int PreferredWidth()
        {
            Size t = TextRenderer.MeasureText(Text, Font, new Size(2000, 100), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return t.Width + Ui.S(Kind == UiKind.Chip ? 24 : 24);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; } base.OnKeyDown(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            int rad = Kind == UiKind.Chip ? Height / 2 : Ui.S(8);
            double vis = Enabled ? 1.0 : 0.5;
            Color fill = Color.Empty, border = Color.Empty, fg;
            switch (Kind)
            {
                case UiKind.Primary:      // залитая синяя кнопка
                    fill = down ? View.Mix(Ui.Accent, Color.Black, 0.2) : (hover ? View.Mix(Ui.Accent, Color.White, Ui.Dark ? 0.15 : 0.08) : Ui.Accent);
                    fg = Ui.OnAccent; break;
                case UiKind.Danger:       // контурная, красный текст
                    border = Ui.Border; fill = down ? Color.FromArgb(50, Ui.Danger) : (hover ? Color.FromArgb(24, Ui.Danger) : Color.Empty);
                    fg = Ui.Danger; break;
                case UiKind.Chip:         // быстрые ответы: контур и нейтральный фон
                    border = Ui.Border; fill = down ? View.Mix(behind, Ui.Text, 0.16) : (hover ? Ui.Raised : Color.Empty);
                    fg = Ui.Text; break;
                default:                  // контурная, синий текст
                    border = Ui.Border; fill = down ? Color.FromArgb(40, Ui.Accent) : (hover ? Color.FromArgb(22, Ui.Accent) : Color.Empty);
                    fg = Ui.Accent; break;
            }
            if (fill != Color.Empty) Ui.FillRound(g, r, rad, Ui.Fade(fill, behind, vis * (fill.A / 255.0)));
            if (border != Color.Empty) Ui.StrokeRound(g, r, rad, Ui.Fade(border, behind, vis), 1f);
            Color text = Enabled ? fg : View.Mix(behind, fg, 0.5);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, new Rectangle(2, 2, Width - 5, Height - 5), Math.Max(2, rad - 2), Ui.Accent, 1f);
        }
    }

    // Переключатель с подписью (вместо стандартного флажка, который плохо смотрится на тёмном фоне)
    class UiToggle : Control
    {
        bool on;
        public event EventHandler CheckedChanged;
        public bool Checked { get { return on; } set { if (on != value) { on = value; Invalidate(); EventHandler h = CheckedChanged; if (h != null) h(this, EventArgs.Empty); } } }
        public UiToggle(string text, bool initial)
        {
            Text = text; on = initial; Cursor = Cursors.Hand; Font = Ui.Small; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }
        protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !Checked; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            int tw = Ui.S(36), th = Ui.S(20), ty = Ui.S(2);
            Rectangle track = new Rectangle(0, ty, tw, th);
            Ui.FillRound(g, track, th / 2, on ? Ui.Accent : Ui.Border);
            int kn = th - Ui.S(6);
            int kx = on ? tw - kn - Ui.S(3) : Ui.S(3);
            using (SolidBrush b = new SolidBrush(on ? Ui.OnAccent : Ui.Dim)) g.FillEllipse(b, kx, ty + Ui.S(3), kn, kn);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, new Rectangle(-1, ty - 1, tw + 1, th + 1), th / 2, Ui.Text, 1f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + Ui.S(10), 0, Width - tw - Ui.S(10), Height), Ui.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }

    // Поле ввода с подписью сверху и золотой рамкой при фокусе
    class UiInput : Control
    {
        public readonly TextBox Box;
        readonly string caption;
        public UiInput(string caption, string text)
        {
            this.caption = caption;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Box = new TextBox { BorderStyle = BorderStyle.None, Text = text, Font = Ui.Body };
            Box.GotFocus += delegate { Invalidate(); }; Box.LostFocus += delegate { Invalidate(); };
            Controls.Add(Box);
            Height = Ui.S(48);
        }
        public override string Text { get { return Box.Text; } set { Box.Text = value; } }
        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.BackColor = Ui.Bg; Box.ForeColor = Ui.Text;
            Box.SetBounds(Ui.S(10), Ui.S(22), Width - Ui.S(20), Ui.S(18));
            base.OnLayout(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            Rectangle r = new Rectangle(0, Ui.S(12), Width - 1, Height - Ui.S(12) - 1);
            Ui.FillRound(g, r, Ui.S(6), Ui.Bg);
            Ui.StrokeRound(g, r, Ui.S(6), Box.Focused ? Ui.Accent : Ui.Border, Box.Focused ? 2f : 1f);
            TextRenderer.DrawText(g, caption, Ui.Small, new Rectangle(Ui.S(2), 0, Width, Ui.S(14)), Box.Focused ? Ui.Accent : Ui.Dim, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    }

    // Состояние соединения: цветная точка + текст
    class StatusPill : Control
    {
        public Color Dot = Color.Gray;
        public StatusPill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Ui.Small; Height = Ui.S(24);
        }
        public void Set(Color dot, string text) { Dot = dot; Text = text; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Height / 2, View.Mix(behind, Ui.Text, 0.10));
            int d = Ui.S(9);
            using (SolidBrush b = new SolidBrush(Dot)) g.FillEllipse(b, Ui.S(10), (Height - d) / 2, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(26), 0, Width - Ui.S(34), Height), Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    // Аватар профиля с золотым кольцом и значком «+» (нажать — выбрать фото)
    class AvatarBox : Control
    {
        public AvatarBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Cursor = Cursors.Hand;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Ui.Bg);
            int pad = Ui.S(4);
            Rectangle r = new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad);
            Avatars.Draw(g, r, Chat.UserId, Chat.Nick, Chat.Avatar);
            using (Pen p = new Pen(Ui.Border, 1.5f)) g.DrawEllipse(p, r.X - 3, r.Y - 3, r.Width + 5, r.Height + 5);
            int b = Ui.S(20);
            Rectangle br = new Rectangle(Width - b - 1, Height - b - 1, b, b);
            using (SolidBrush sb = new SolidBrush(Ui.Accent)) g.FillEllipse(sb, br);
            using (Pen pp = new Pen(Ui.OnAccent, 2f))
            {
                g.DrawLine(pp, br.X + br.Width / 2f, br.Y + b * 0.28f, br.X + br.Width / 2f, br.Bottom - b * 0.28f);
                g.DrawLine(pp, br.X + b * 0.28f, br.Y + br.Height / 2f, br.Right - b * 0.28f, br.Y + br.Height / 2f);
            }
        }
    }

    class BufListBox : ListBox
    {
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idlist);
        // Никаких SetStyle(UserPaint/...) для нативного ListBox: иначе элементы рисуются только после клика или прокрутки
        public BufListBox() { }
        // тёмная полоса прокрутки (Windows 10/11); на старых системах просто остаётся обычной
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { SetWindowTheme(Handle, "DarkMode_Explorer", null); } catch { }
        }
    }

    // Круглая кнопка-значок (шестерёнка, «ещё», копировать) как в мессенджерах
    class UiIconButton : Control
    {
        bool hover, down;
        public string Glyph;
        static string family;
        static string Family
        {
            get
            {
                if (family == null)
                {
                    family = "Segoe UI Symbol";
                    try { using (Font f = new Font("Segoe MDL2 Assets", 10f)) if (f.Name == "Segoe MDL2 Assets") family = "Segoe MDL2 Assets"; } catch { }
                }
                return family;
            }
        }
        public UiIconButton(string glyph)
        {
            Glyph = glyph; Cursor = Cursors.Hand; TabStop = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            if (hover || down) using (SolidBrush b = new SolidBrush(Color.FromArgb(down ? 50 : 30, Ui.Text))) g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
            using (Font f = new Font(Family, Height * 0.38f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, Glyph, f, new Rectangle(0, 0, Width, Height), Enabled ? Ui.Text : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // ───────────────────────── Список сообщений (общий для окна и док-панели) ─────────────────────────
    class MessageView : BufListBox
    {
        public ChatGroup Group;
        string shownCode; int shownCount = -1;

        public MessageView()
        {
            DrawMode = DrawMode.OwnerDrawVariable; BorderStyle = BorderStyle.None; BackColor = Ui.Bg; IntegralHeight = false;
        }

        public void SetGroup(ChatGroup g)
        {
            Group = g;
            if (g == null) { Items.Clear(); shownCode = null; shownCount = -1; return; }
            if (shownCode != g.Code || shownCount != g.Messages.Count) Fill(true);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); Fill(false); }

        static string DayLabel(DateTime d)
        {
            if (d.Date == DateTime.Today) return ChatT.T("today");
            if (d.Date == DateTime.Today.AddDays(-1)) return ChatT.T("yesterday");
            return d.ToString("d MMMM");
        }

        public void Fill(bool toEnd)
        {
            ChatGroup g = Group;
            if (g == null) return;
            int top = TopIndex;
            BeginUpdate();
            Items.Clear();
            DateTime lastDay = DateTime.MinValue;
            foreach (ChatMsg m in g.Messages)
            {
                if (m.Time.Date != lastDay) { Items.Add(new DaySep { Text = DayLabel(m.Time) }); lastDay = m.Time.Date; }
                Items.Add(m);
            }
            EndUpdate();
            if (toEnd || shownCode != g.Code) ScrollToEnd();
            else if (top < Items.Count) TopIndex = top;
            shownCode = g.Code; shownCount = g.Messages.Count;
            Invalidate(); Update();   // сразу перерисовать: новые сообщения видны без прокрутки
        }

        // Показать конец переписки: верхний элемент подбирается так, чтобы последние сообщения заполняли окно целиком
        void ScrollToEnd()
        {
            if (Items.Count == 0) return;
            int h = 0, i = Items.Count - 1;
            for (; i >= 0; i--)
            {
                h += GetItemHeight(i);
                if (h > ClientSize.Height) { i++; break; }
            }
            TopIndex = Math.Max(0, Math.Min(i, Items.Count - 1));
        }

        // ---- пузыри с «хвостиком», аватар у последнего в серии, разделители дней
        int BubbleMaxW() { return Math.Max(S(140), (int)(ClientSize.Width * 0.74)); }
        static int S(float v) { return Ui.S(v); }
        Size TextSize(Graphics g, string text, int maxW)
        {
            return TextRenderer.MeasureText(g, text, Ui.Body, new Size(maxW, 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
        ChatMsg MsgAt(int i) { return i >= 0 && i < Items.Count ? Items[i] as ChatMsg : null; }
        bool FirstOfRun(int i) { ChatMsg p = MsgAt(i - 1), m = MsgAt(i); return p == null || m == null || p.From != m.From; }
        bool LastOfRun(int i) { ChatMsg n = MsgAt(i + 1), m = MsgAt(i); return n == null || m == null || n.From != m.From; }

        protected override void OnMeasureItem(MeasureItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) { e.ItemHeight = S(20); return; }
            DaySep d = Items[e.Index] as DaySep;
            if (d != null) { e.ItemHeight = S(32); return; }
            ChatMsg m = (ChatMsg)Items[e.Index];
            bool mine = m.From == Chat.UserId;
            Size t = TextSize(e.Graphics, m.Display, BubbleMaxW() - S(22));
            int bubbleH = S(7) + t.Height + S(2) + S(13) + S(5);
            int nameH = (!mine && FirstOfRun(e.Index)) ? S(17) : 0;
            e.ItemHeight = nameH + bubbleH + (LastOfRun(e.Index) ? S(8) : S(2));
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Graphics gr = e.Graphics; gr.SmoothingMode = SmoothingMode.AntiAlias;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Ui.Bg)) gr.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            DaySep d = Items[e.Index] as DaySep;
            if (d != null)
            {
                Size ts = TextRenderer.MeasureText(gr, d.Text, Ui.Small, new Size(400, 40), TextFormatFlags.NoPadding);
                Rectangle pr = new Rectangle(e.Bounds.X + (e.Bounds.Width - ts.Width - S(24)) / 2, e.Bounds.Y + S(8), ts.Width + S(24), S(24));
                Ui.FillRound(gr, pr, S(12), View.Mix(Ui.Bg, Ui.Text, 0.12));
                TextRenderer.DrawText(gr, d.Text, Ui.Small, pr, Ui.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            ChatMsg m = (ChatMsg)Items[e.Index];
            ChatGroup g = Group;
            bool mine = m.From == Chat.UserId;
            bool first = FirstOfRun(e.Index), last = LastOfRun(e.Index);
            string text = m.Display;
            Size t = TextSize(gr, text, BubbleMaxW() - S(22));
            string time = m.Time.ToString("HH:mm");
            int timeW = TextRenderer.MeasureText(gr, time, Ui.Small, new Size(200, 40), TextFormatFlags.NoPadding).Width;
            int bw = Math.Max(t.Width, timeW + S(4)) + S(20);
            int bh = S(7) + t.Height + S(2) + S(13) + S(5);
            int top = e.Bounds.Y + ((!mine && first) ? S(17) : 0);
            int bx = mine ? e.Bounds.Right - S(46) - bw : e.Bounds.X + S(46);
            Rectangle br = new Rectangle(bx, top, bw, bh);
            Color fill = mine ? Ui.MineBubble : Ui.Raised;
            using (GraphicsPath p = Ui.Round(br, S(12))) using (SolidBrush fb = new SolidBrush(fill)) gr.FillPath(fb, p);
            if (last)   // «хвостик» у последнего пузыря серии
            {
                Point[] tail = mine
                    ? new[] { new Point(br.Right - S(10), br.Bottom - S(16)), new Point(br.Right + S(7), br.Bottom), new Point(br.Right - S(18), br.Bottom - S(2)) }
                    : new[] { new Point(br.X + S(10), br.Bottom - S(16)), new Point(br.X - S(7), br.Bottom), new Point(br.X + S(18), br.Bottom - S(2)) };
                using (SolidBrush fb = new SolidBrush(fill)) gr.FillPolygon(fb, tail);
            }
            if (mine)
            {
                if (last) Avatars.Draw(gr, new Rectangle(e.Bounds.Right - S(38), br.Bottom - S(28), S(28), S(28)), Chat.UserId, Chat.Nick, Chat.Avatar);
            }
            else
            {
                if (first) TextRenderer.DrawText(gr, m.Nick, Ui.Small, new Point(bx + S(4), e.Bounds.Y + S(1)), NickColor(m.From));
                if (last)
                {
                    ChatMember who = null;
                    if (g != null) foreach (ChatMember x in g.Members) if (x.UserId == m.From) who = x;
                    Avatars.Draw(gr, new Rectangle(e.Bounds.X + S(10), br.Bottom - S(28), S(28), S(28)), m.From, m.Nick, who != null ? who.Avatar : null);
                }
            }
            TextRenderer.DrawText(gr, text, Ui.Body, new Rectangle(br.X + S(10), br.Y + S(7), t.Width + S(2), t.Height + S(2)), Ui.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(gr, time, Ui.Small, new Point(br.Right - S(10) - timeW, br.Bottom - S(5) - S(12)), View.Mix(Ui.Text, fill, 0.55));
        }

        static readonly Color[] nickTints = { Color.FromArgb(240, 150, 110), Color.FromArgb(120, 200, 150), Color.FromArgb(120, 170, 240), Color.FromArgb(220, 140, 200), Color.FromArgb(230, 200, 100), Color.FromArgb(120, 210, 210) };
        static Color NickColor(string userId) { int h = 0; foreach (char c in userId ?? "") h = h * 31 + c; return nickTints[Math.Abs(h) % nickTints.Length]; }
    }

    // Список участников: «в сети» сверху, «не в сети» снизу; аватар, ник, точка справа
    class MemberList : BufListBox
    {
        public ChatGroup Group;
        public MemberList() { DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = Ui.S(46); BorderStyle = BorderStyle.None; BackColor = Ui.Bg; IntegralHeight = false; }

        public void SetGroup(ChatGroup g)
        {
            Group = g;
            BeginUpdate(); Items.Clear();
            if (g != null)
            {
                List<ChatMember> on = new List<ChatMember>(), off = new List<ChatMember>();
                foreach (ChatMember m in g.Members) (m.Online ? on : off).Add(m);
                Comparison<ChatMember> cmp = delegate(ChatMember a, ChatMember b) { return string.Compare(a.Nick, b.Nick, StringComparison.CurrentCultureIgnoreCase); };
                on.Sort(cmp); off.Sort(cmp);
                if (on.Count > 0) { Items.Add(string.Format(ChatT.T("online"), on.Count)); foreach (ChatMember m in on) Items.Add(m); }
                if (off.Count > 0) { Items.Add(string.Format(ChatT.T("offlineN"), off.Count)); foreach (ChatMember m in off) Items.Add(m); }
            }
            EndUpdate();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Ui.Bg)) g.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= Items.Count) return;
            string head = Items[e.Index] as string;
            if (head != null)
            {
                TextRenderer.DrawText(g, head, Ui.Small, new Rectangle(e.Bounds.X + Ui.S(14), e.Bounds.Y + Ui.S(16), e.Bounds.Width, Ui.S(24)), Ui.Dim, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                return;
            }
            ChatMember m = (ChatMember)Items[e.Index];
            int av = Ui.S(32);
            Rectangle ar = new Rectangle(e.Bounds.X + Ui.S(12), e.Bounds.Y + (e.Bounds.Height - av) / 2, av, av);
            Avatars.Draw(g, ar, m.UserId, m.Nick, m.Avatar);
            if (!m.Online) using (SolidBrush dim = new SolidBrush(Color.FromArgb(120, Ui.Bg))) g.FillEllipse(dim, ar);
            int d = Ui.S(11);
            Rectangle dr = new Rectangle(e.Bounds.Right - Ui.S(24), e.Bounds.Y + (e.Bounds.Height - d) / 2, d, d);
            using (SolidBrush dot = new SolidBrush(m.Online ? Ui.Online : Ui.Offline)) g.FillEllipse(dot, dr);
            int tx = ar.Right + Ui.S(10);
            Color fg = m.Online ? Ui.Text : Ui.Dim;
            string nick = m.UserId == Chat.UserId ? m.Nick + "  (" + ChatT.T("you") + ")" : m.Nick;
            TextRenderer.DrawText(g, nick, Ui.Medium, new Rectangle(tx, e.Bounds.Y + Ui.S(6), dr.X - tx - Ui.S(10), Ui.S(18)), fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, ChatT.T(m.Online ? "stOn" : "stOff"), Ui.Small, new Rectangle(tx, e.Bounds.Y + Ui.S(25), dr.X - tx - Ui.S(10), Ui.S(14)), m.Online ? Ui.Online : Ui.Dim, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        }
    }

    // Сообщение о результате (успех / ошибка) внизу панели, как «snackbar» в приложениях Google
    class Snackbar : Control
    {
        string text = ""; bool error;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 5000 };
        public Snackbar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = Ui.S(44); Visible = false; Font = Ui.Body;
            timer.Tick += delegate { timer.Stop(); Visible = false; };
        }
        public void Show(string t, bool isError)
        {
            text = t; error = isError;
            timer.Interval = isError ? 7000 : 4500; timer.Stop(); timer.Start();
            Visible = true; BringToFront(); Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            Color bg = error ? (Ui.Dark ? Color.FromArgb(242, 139, 130) : Color.FromArgb(217, 48, 37))
                             : (Ui.Dark ? Color.FromArgb(232, 234, 237) : Color.FromArgb(50, 50, 52));
            Color fg = error ? (Ui.Dark ? Color.FromArgb(32, 33, 36) : Color.White) : (Ui.Dark ? Color.FromArgb(32, 33, 36) : Color.White);
            Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Ui.S(8), bg);
            int d = Ui.S(18); Rectangle ic = new Rectangle(Ui.S(12), (Height - d) / 2, d, d);
            using (Pen p = new Pen(fg, 2f))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                if (error) { g.DrawLine(p, ic.X + d / 2f, ic.Y + d * 0.2f, ic.X + d / 2f, ic.Y + d * 0.62f); g.DrawLine(p, ic.X + d / 2f, ic.Y + d * 0.84f, ic.X + d / 2f, ic.Y + d * 0.86f); }
                else g.DrawLines(p, new PointF[] { new PointF(ic.X + d * 0.15f, ic.Y + d * 0.55f), new PointF(ic.X + d * 0.4f, ic.Y + d * 0.8f), new PointF(ic.X + d * 0.85f, ic.Y + d * 0.25f) });
            }
            TextRenderer.DrawText(g, text, Ui.Small, new Rectangle(ic.Right + Ui.S(10), 0, Width - ic.Right - Ui.S(22), Height), fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        }
    }

    // Вкладки с золотым подчёркиванием (как «Friends / Chats» в приложении Xbox)
    class UiTabs : Control
    {
        public string[] Items = new string[0];
        int sel, hot = -1;
        public event EventHandler Changed;
        public int Selected { get { return sel; } set { if (sel != value) { sel = value; Invalidate(); EventHandler h = Changed; if (h != null) h(this, EventArgs.Empty); } } }
        public UiTabs()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand; Height = Ui.S(40);
        }
        int TabAt(int x) { if (Items.Length == 0) return -1; int i = x * Items.Length / Math.Max(1, Width); return Math.Min(Items.Length - 1, Math.Max(0, i)); }
        protected override void OnMouseMove(MouseEventArgs e) { int h = TabAt(e.X); if (h != hot) { hot = h; Invalidate(); } base.OnMouseMove(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { int i = TabAt(e.X); if (i >= 0) Selected = i; base.OnMouseDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            using (Pen line = new Pen(Color.FromArgb(40, Ui.Text))) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            if (Items.Length == 0) return;
            int w = Width / Items.Length;
            for (int i = 0; i < Items.Length; i++)
            {
                Rectangle r = new Rectangle(i * w, 0, w, Height - 2);
                bool on = i == sel;
                TextRenderer.DrawText(g, Items[i], Ui.Medium, r, on ? Ui.Accent : (i == hot ? Ui.Text : Ui.Dim), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                if (on) using (SolidBrush b = new SolidBrush(Ui.Accent)) g.FillRectangle(b, r.X + Ui.S(14), Height - Ui.S(3), w - Ui.S(28), Ui.S(3));
            }
        }
    }

    // ───────────────────────── Окно чата (в духе Telegram / WhatsApp) ─────────────────────────
    class DaySep { public string Text; }

    // ───────────────────────── Док-панель чата (в духе панели «Friends» в приложении Xbox) ─────────────────────────
    // Единственное окно чата: компактная панель над виджетом NamazBar. Свёрнутая — узкая полоса «группа ⌃».
    // Внутри: вкладки групп (и «+» для новой/вступить), «Чат» / «Участники», быстрые ответы, настройки профиля и сервера.
    class ChatDock : Form
    {
        static ChatDock instance;
        public static bool IsOpen { get { return instance != null && !instance.IsDisposed && instance.Visible; } }
        public static bool IsWatching(ChatGroup g) { return IsOpen && !instance.collapsed && instance.CurrentGroup == g && instance.ContainsFocus; }
        public static void CloseIfOpen() { if (instance != null && !instance.IsDisposed) instance.Close(); }
        public static void ShowDock() { ShowDock(false); }
        public static void ShowDock(bool addGroup)
        {
            if (instance == null || instance.IsDisposed) instance = new ChatDock();
            instance.collapsed = false; instance.forceSettings = false;
            if (addGroup) instance.groupIdx = Chat.Groups.Count;
            instance.Show(); instance.Refresh2(); instance.Activate();
        }
        public static void Reopen() { bool was = IsOpen; CloseIfOpen(); if (was) ShowDock(); }

        static void PickAvatar(IWin32Window owner)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|*.*|*.*";
                d.Title = ChatT.T("pick");
                if (d.ShowDialog(owner) != DialogResult.OK) return;
                try { Chat.SetAvatar(Avatars.FromFile(d.FileName)); }
                catch (Exception ex) { Store.Log("avatar: " + ex.Message); MessageBox.Show(owner, ChatT.T("bad"), ChatT.T("title")); }
            }
        }

        const int WS_EX_TOOLWINDOW = 0x80;
        protected override CreateParams CreateParams { get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW; return cp; } }

        bool collapsed, forceSettings, pendingSelectNew;
        int groupIdx, view, lastGroupCount;
        string noticeText; DateTime noticeAt = DateTime.MinValue; bool noticeError;
        System.Windows.Forms.Timer statusTimer;
        Label lblTitle, lblBadge, lblSetupHint, lblAddHint, lblPick;
        UiIconButton btnCollapse, btnClose, btnGear;
        UiTabs groupTabs, viewTabs;
        MessageView msgs; MemberList memberList;
        StatusPill pill, setPill; Snackbar snack; UiButton btnWake;
        Panel bar, quick, memberFoot, settingsPanel, addPanel, codeBar;
        Label lblCode; UiIconButton btnCopyIcon, btnClear;
        bool applying; Size regionSize; int dockH;
        const int WM_NCHITTEST = 0x84, WM_EXITSIZEMOVE = 0x232, HTTOP = 12;
        UiButton btnCopyCode, btnLeave, btnSave, btnCancelSettings, btnCreate, btnJoin;
        UiInput inNick, inServer, inName, inCode;
        UiToggle tgAuto, tgSound;
        AvatarBox avatarBox;
        readonly List<UiButton> chips = new List<UiButton>();

        int S(float v) { return Ui.S(v); }
        ChatGroup CurrentGroup { get { return groupIdx >= 0 && groupIdx < Chat.Groups.Count ? Chat.Groups[groupIdx] : null; } }

        ChatDock()
        {
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.Body; DoubleBuffered = true;
            int saved; if (!int.TryParse(Store.Get("chatDockH", ""), out saved)) saved = 0;
            dockH = Math.Max(S(300), saved > 0 ? saved : S(470));
            Size = new Size(S(340), dockH);
            Build();
            lastGroupCount = Chat.Groups.Count;
            Chat.Changed += OnChanged; Chat.Notice += OnNotice;
            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += delegate { UpdatePill(); };
            statusTimer.Start();
            FormClosed += delegate { statusTimer.Stop(); Chat.Changed -= OnChanged; Chat.Notice -= OnNotice; Chat.Active = null; };
            KeyPreview = true;
            KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); };
            Deactivate += delegate { Chat.Active = null; };
            Activated += delegate { Chat.Active = CurrentGroup; if (CurrentGroup != null) CurrentGroup.Unread = 0; Invalidate(true); };
            Load += delegate { Refresh2(); };
        }

        Label MakeLabel(Font f, Color c) { return new Label { Font = f, ForeColor = c, AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true }; }

        void Build()
        {
            // ---- шапка
            bar = new Panel { BackColor = Ui.Panel };
            bar.Click += delegate { Toggle(); };
            lblTitle = MakeLabel(Ui.Title, Ui.Text); lblTitle.TextAlign = ContentAlignment.MiddleLeft; lblTitle.Click += delegate { Toggle(); }; lblTitle.Cursor = Cursors.Hand;
            lblBadge = MakeLabel(Ui.Small, Ui.OnAccent); lblBadge.TextAlign = ContentAlignment.MiddleCenter; lblBadge.BackColor = Ui.Accent; lblBadge.Visible = false;
            btnCollapse = new UiIconButton(""); btnCollapse.Click += delegate { Toggle(); };
            btnGear = new UiIconButton(""); btnGear.Click += delegate { OpenSettings(); };
            btnClose = new UiIconButton(""); btnClose.Click += delegate { Close(); };
            bar.Controls.AddRange(new Control[] { lblTitle, lblBadge, btnGear, btnCollapse, btnClose });

            // ---- вкладки групп («+» — новая группа / вступить) и «Чат / Участники»
            groupTabs = new UiTabs(); groupTabs.Changed += delegate { groupIdx = groupTabs.Selected; forceSettings = false; view = 0; viewTabs.Selected = 0; Refresh2(); };
            viewTabs = new UiTabs { Items = new[] { ChatT.T("chatTab"), ChatT.T("members") } };
            viewTabs.Changed += delegate { view = viewTabs.Selected; Refresh2(); };
            msgs = new MessageView(); memberList = new MemberList();
            pill = new StatusPill(); snack = new Snackbar();
            pill.Cursor = Cursors.Hand; pill.Click += delegate { Chat.Wake(null); };

            // ---- быстрые ответы
            quick = new Panel { BackColor = Ui.Panel };
            quick.Paint += delegate(object o, PaintEventArgs e) { using (Pen p = new Pen(Color.FromArgb(40, Ui.Text))) e.Graphics.DrawLine(p, 0, 0, quick.Width, 0); };
            foreach (string id in ChatT.PresetIds)
            {
                string pid = id;
                UiButton b = new UiButton(ChatT.Preset(id, id), UiKind.Chip); b.Font = Ui.Small; b.Height = S(26);
                b.Click += delegate
                {
                    ChatGroup g = CurrentGroup; if (g == null) return;
                    if (!Chat.Connected) { snack.Show(ChatT.T("notConn"), true); return; }
                    Chat.SendPreset(g.Code, pid);
                };
                quick.Controls.Add(b); chips.Add(b);
            }

            // ---- строка с кодом группы и значком «копировать» (код нужен сразу после создания группы)
            codeBar = new Panel { BackColor = Ui.Bg };
            lblCode = MakeLabel(Ui.Small, Ui.Dim); lblCode.TextAlign = ContentAlignment.MiddleLeft;
            btnCopyIcon = new UiIconButton("\uE8C8");
            btnCopyIcon.Click += delegate { CopyCode(); };
            lblCode.Cursor = Cursors.Hand; lblCode.Click += delegate { CopyCode(); };
            btnClear = new UiIconButton("\uE74D");
            btnClear.Click += delegate
            {
                ChatGroup g = CurrentGroup; if (g == null) return;
                if (!Chat.Connected) { snack.Show(ChatT.T("notConn"), true); return; }
                Chat.ClearChat(g.Code); snack.Show(ChatT.T("chatCleared"), false);
            };
            codeBar.Controls.AddRange(new Control[] { lblCode, btnCopyIcon, btnClear });

            // ---- участники: код группы и «Выйти»
            memberFoot = new Panel { BackColor = Ui.Panel };
            memberFoot.Paint += delegate(object o, PaintEventArgs e) { using (Pen p = new Pen(Color.FromArgb(40, Ui.Text))) e.Graphics.DrawLine(p, 0, 0, memberFoot.Width, 0); };
            btnCopyCode = new UiButton(ChatT.T("copy"), UiKind.Secondary);
            btnCopyCode.Click += delegate { CopyCode(); };
            btnLeave = new UiButton(ChatT.T("leave"), UiKind.Danger);
            btnLeave.Click += delegate { ChatGroup g = CurrentGroup; if (g != null) Chat.Leave(g.Code); };
            memberFoot.Controls.AddRange(new Control[] { btnCopyCode, btnLeave });

            // ---- настройки: фото, ник, адрес сервера, переключатели
            settingsPanel = new Panel { BackColor = Ui.Bg };
            lblSetupHint = MakeLabel(Ui.Medium, Ui.Accent); lblSetupHint.TextAlign = ContentAlignment.MiddleCenter;
            avatarBox = new AvatarBox(); avatarBox.Click += delegate { PickAvatar(this); avatarBox.Invalidate(); };
            lblPick = MakeLabel(Ui.Small, Ui.Dim); lblPick.Text = ChatT.T("pick"); lblPick.TextAlign = ContentAlignment.MiddleCenter;
            inNick = new UiInput(ChatT.T("nick"), ""); inNick.Box.MaxLength = 24;
            inServer = new UiInput(ChatT.T("server"), "");
            setPill = new StatusPill(); setPill.Cursor = Cursors.Hand; setPill.Click += delegate { Chat.Wake(inServer.Text); };
            btnWake = new UiButton(ChatT.T("srvWake"), UiKind.Secondary); btnWake.Font = Ui.Small;
            btnWake.Click += delegate { Chat.Wake(inServer.Text); UpdatePill(); };
            tgAuto = new UiToggle(ChatT.T("auto"), false);
            tgSound = new UiToggle(ChatT.T("sound"), true);
            btnSave = new UiButton(ChatT.T("save"), UiKind.Primary); btnSave.Click += delegate { SaveSettings(); };
            btnCancelSettings = new UiButton(ChatT.T("cancel"), UiKind.Secondary); btnCancelSettings.Click += delegate { forceSettings = false; Refresh2(); };
            settingsPanel.Controls.AddRange(new Control[] { lblSetupHint, avatarBox, lblPick, inNick, inServer, setPill, btnWake, tgAuto, tgSound, btnCancelSettings, btnSave });

            // ---- «+»: новая группа или вступить по коду (прямо в панели, без отдельных окон)
            addPanel = new Panel { BackColor = Ui.Bg };
            lblAddHint = MakeLabel(Ui.Small, Ui.Dim); lblAddHint.Text = ChatT.T("noGroupHint"); lblAddHint.TextAlign = ContentAlignment.MiddleCenter; lblAddHint.AutoEllipsis = false;
            inName = new UiInput(ChatT.T("newName"), ""); inName.Box.MaxLength = 40;
            inCode = new UiInput(ChatT.T("enterCode"), ""); inCode.Box.MaxLength = 12;
            btnCreate = new UiButton("+  " + ChatT.T("create"), UiKind.Primary); btnCreate.Click += delegate { DoCreate(); };
            btnJoin = new UiButton(ChatT.T("join"), UiKind.Secondary); btnJoin.Click += delegate { DoJoin(); };
            inName.Box.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoCreate(); } };
            inCode.Box.KeyDown += delegate(object o, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; DoJoin(); } };
            addPanel.Controls.AddRange(new Control[] { lblAddHint, inName, btnCreate, inCode, btnJoin });
            addPanel.Paint += PaintAdd;

            Controls.AddRange(new Control[] { snack, bar, groupTabs, viewTabs, codeBar, msgs, memberList, memberFoot, pill, quick, settingsPanel, addPanel });
        }

        void CopyCode()
        {
            ChatGroup g = CurrentGroup; if (g == null) return;
            try { Clipboard.SetText(g.Code); OnNotice(ChatT.T("copied") + ": " + g.Code, false); } catch { }
        }

        void PaintAdd(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float cx = addPanel.Width / 2f, cy = S(46), r = S(28);
            using (GraphicsPath p = new GraphicsPath())
            {
                Palette.Star8(p, cx, cy, r);
                using (SolidBrush b = new SolidBrush(Ui.Raised)) g.FillPath(b, p);
                using (Pen pen = new Pen(Ui.Border, 1.4f)) g.DrawPath(pen, p);
            }
        }

        void DoCreate()
        {
            string n = inName.Text.Trim();
            if (n.Length == 0) { snack.Show(ChatT.T("needName"), true); return; }
            if (!Chat.Connected) { snack.Show(ChatT.T("notConn"), true); return; }
            pendingSelectNew = true; Chat.CreateGroup(n); inName.Text = "";
            snack.Show(ChatT.T("creating"), false);
        }
        void DoJoin()
        {
            string c = inCode.Text.Trim();
            if (c.Length == 0) { snack.Show(ChatT.T("needCode"), true); return; }
            if (!Chat.Connected) { snack.Show(ChatT.T("notConn"), true); return; }
            Chat.RequestJoin(c); inCode.Text = "";
            snack.Show(ChatT.T("sendingReq"), false);
        }

        void OpenSettings()
        {
            collapsed = false; forceSettings = true;
            inNick.Text = Chat.Nick ?? ""; inServer.Text = Chat.Server ?? "";
            tgAuto.Checked = Store.Get("chatAuto", "0") == "1"; tgSound.Checked = Store.Get("chatSound", "1") != "0";
            Refresh2(); Activate();
        }

        void SaveSettings()
        {
            Store.Settings["chatAuto"] = tgAuto.Checked ? "1" : "0";
            Store.Settings["chatSound"] = tgSound.Checked ? "1" : "0";
            Store.Save();
            Chat.Configure(inServer.Text, inNick.Text);
            forceSettings = false;
            Refresh2();
        }

        void Toggle() { collapsed = !collapsed; Refresh2(); if (!collapsed) Activate(); }

        void OnNotice(string text, bool error) { if (IsDisposed) return; snack.Show(text, error); }

        void OnChanged()
        {
            if (IsDisposed) return;
            int n = Chat.Groups.Count;
            if (pendingSelectNew && n > lastGroupCount) { groupIdx = n - 1; view = 0; viewTabs.Selected = 0; pendingSelectNew = false; }
            else if (n > lastGroupCount && groupIdx >= lastGroupCount) groupIdx = n - 1;   // вступили в группу — открываем её
            lastGroupCount = n;
            Refresh2();
        }

        // Событие (ошибка, «ожидание подтверждения») показываем 8 секунд, дальше — состояние соединения
        void UpdatePill()
        {
            if (pill == null || IsDisposed) return;
            int kind; string text; Chat.Status(out kind, out text);
            Color c = kind == 1 ? Ui.Online : kind == 2 ? Color.FromArgb(240, 178, 84) : kind == 3 ? Color.FromArgb(240, 110, 90) : Color.Gray;
            pill.Set(c, text); setPill.Set(c, text);
            btnWake.Enabled = !Chat.Probing;
            bool want = !collapsed && Chat.Configured && !forceSettings;
            if (pill.Visible != want) ApplyState();
        }

        void Refresh2()
        {
            if (IsDisposed) return;
            int n = Chat.Groups.Count;
            groupIdx = Math.Max(0, Math.Min(groupIdx, n));   // n — вкладка «+»
            string[] names = new string[n + 1];
            for (int i = 0; i < n; i++) names[i] = Chat.Groups[i].Name;
            names[n] = "+";
            groupTabs.Items = names;
            if (groupTabs.Selected != groupIdx) groupTabs.Selected = groupIdx;
            groupTabs.Invalidate();
            ChatGroup g = CurrentGroup;
            if (g != null && !collapsed && ContainsFocus) g.Unread = 0;
            int unread = 0; foreach (ChatGroup x in Chat.Groups) unread += x.Unread;
            lblTitle.Text = (g != null && Chat.Configured && !forceSettings) ? g.Name : ChatT.T("menu").TrimEnd('…');
            lblBadge.Visible = unread > 0 && collapsed; lblBadge.Text = unread > 99 ? "99+" : unread.ToString();
            msgs.SetGroup(g); memberList.SetGroup(g);
            lblCode.Text = g != null ? string.Format(ChatT.T("code"), g.Code) : "";
            foreach (UiButton b in chips) b.Enabled = g != null;
            btnLeave.Enabled = Chat.Connected;
            lblSetupHint.Text = Chat.Configured ? "" : ChatT.T("setup");
            avatarBox.Invalidate();
            UpdatePill();
            ApplyState();
        }

        void ApplyState()
        {
            if (IsDisposed || bar == null) return;
            bool cfg = Chat.Configured; int n = Chat.Groups.Count;
            int W = collapsed ? S(230) : S(340), barH = collapsed ? S(34) : S(40);   // свёрнутая — узкая тонкая полоса
            btnCollapse.Glyph = collapsed ? "" : "";
            bool showSettings = !collapsed && (!cfg || forceSettings);
            bool showAdd = !collapsed && cfg && !forceSettings && (n == 0 || groupIdx >= n);
            bool showGroup = !collapsed && cfg && !forceSettings && n > 0 && groupIdx < n;
            groupTabs.Visible = !collapsed && cfg && !forceSettings;
            viewTabs.Visible = codeBar.Visible = showGroup;
            msgs.Visible = quick.Visible = showGroup && view == 0;
            memberList.Visible = memberFoot.Visible = showGroup && view == 1;
            settingsPanel.Visible = showSettings; addPanel.Visible = showAdd;
            btnCancelSettings.Visible = cfg;
            pill.Visible = !collapsed && cfg && !forceSettings;
            int H = collapsed ? barH : dockH;
            Rectangle anchor = Rectangle.Empty;
            try { if (ChatToast.Anchor != null) anchor = ChatToast.Anchor(); } catch { }
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int x, bottom;
            if (anchor.Width > 0 && anchor.Height > 0)
            {
                Screen scr = Screen.FromRectangle(anchor); wa = scr.WorkingArea;
                x = Math.Max(wa.Left + 8, Math.Min(anchor.Left, wa.Right - W - 8)); bottom = Math.Min(anchor.Top, wa.Bottom) - 8;
            }
            else { x = wa.Right - W - 12; bottom = wa.Bottom - 12; }
            if (!collapsed) { H = Math.Max(S(300), Math.Min(H, wa.Height - 16)); dockH = H; }
            Rectangle target = new Rectangle(x, Math.Max(wa.Top + 8, bottom - H), W, H);
            applying = true;
            try { if (Bounds != target) SetBounds(target.X, target.Y, target.Width, target.Height); }
            finally { applying = false; }
            LayoutChildren(W, H, barH);
            bool resized = regionSize != target.Size; regionSize = target.Size;
            if (resized) { try { using (GraphicsPath p = Ui.Round(new Rectangle(0, 0, W, H), S(12))) Region = new System.Drawing.Region(p); } catch { } }
            // снэкбар — над быстрыми ответами / внизу панели
            snack.SetBounds(S(10), H - (quick.Visible ? quick.Height : (memberFoot.Visible ? memberFoot.Height : 0)) - S(52), W - S(20), S(44));
            if (snack.Visible) snack.BringToFront();
        }

        void LayoutChildren(int W, int H, int barH)
        {
            int grip = collapsed ? 0 : S(5);   // верхняя кромка: за неё можно тянуть окно вверх
            bar.SetBounds(0, grip, W, barH);
            int bs = barH - S(8), by0 = S(4);   // кнопки в шапке подстраиваются под её высоту
            btnClose.SetBounds(W - bs - S(6), by0, bs, bs);
            btnCollapse.SetBounds(btnClose.Left - bs - S(2), by0, bs, bs);
            btnGear.Visible = !collapsed;
            btnGear.SetBounds(btnCollapse.Left - bs - S(2), by0, bs, bs);
            int rightEdge = collapsed ? btnCollapse.Left : btnGear.Left;
            lblBadge.SetBounds(rightEdge - S(40), (barH - S(20)) / 2, S(34), S(20));
            lblTitle.SetBounds(S(14), 0, rightEdge - S(14) - (lblBadge.Visible ? S(44) : S(4)), barH);
            int y = barH + grip;
            if (groupTabs.Visible) { groupTabs.SetBounds(0, y, W, S(34)); y += S(34); }
            if (viewTabs.Visible) { viewTabs.SetBounds(0, y, W, S(34)); y += S(34); }
            if (codeBar.Visible)
            {
                codeBar.SetBounds(0, y, W, S(30));
                bool own = CurrentGroup != null && CurrentGroup.Owner == Chat.UserId;
                btnClear.Visible = own;
                btnClear.SetBounds(W - S(40), S(2), S(26), S(26));
                btnCopyIcon.SetBounds(W - S(40) - (own ? S(30) : 0), S(2), S(26), S(26));
                lblCode.SetBounds(S(14), 0, btnCopyIcon.Left - S(18), S(30));
                y += S(30);
            }
            if (pill.Visible) { pill.SetBounds(S(12), y + S(4), W - S(24), S(22)); y += S(30); }
            int rest = Math.Max(S(40), H - y);
            // быстрые ответы — снизу
            int qpad = S(10), x = qpad, cy = S(8), rowH = S(26), gap = S(6);
            foreach (UiButton b in chips)
            {
                int w = Math.Min(W - 2 * qpad, b.PreferredWidth());
                if (x + w > W - qpad && x > qpad) { x = qpad; cy += rowH + gap; }
                b.SetBounds(x, cy, w, rowH); x += w + gap;
            }
            int qh = cy + rowH + S(8);
            quick.SetBounds(0, H - qh, W, qh);
            msgs.SetBounds(0, y, W, Math.Max(S(40), (quick.Visible ? H - qh : H) - y));
            int fh = S(54);
            memberFoot.SetBounds(0, H - fh, W, fh);
            btnCopyCode.SetBounds(S(10), S(9), (W - S(30)) / 2, S(36));
            btnLeave.SetBounds(S(20) + (W - S(30)) / 2, S(9), (W - S(30)) / 2, S(36));
            memberList.SetBounds(0, y, W, Math.Max(S(40), H - fh - y));
            // настройки
            settingsPanel.SetBounds(0, y, W, rest);
            int pw = W - S(40), px = S(20), py = S(6);
            lblSetupHint.SetBounds(px, py, pw, lblSetupHint.Text.Length > 0 ? S(24) : 0);
            py += lblSetupHint.Text.Length > 0 ? S(28) : S(4);
            avatarBox.SetBounds((W - S(68)) / 2, py, S(68), S(68)); py += S(70);
            lblPick.SetBounds(px, py, pw, S(16)); py += S(22);
            inNick.SetBounds(px, py, pw, S(48)); py += S(54);
            inServer.SetBounds(px, py, pw, S(48)); py += S(54);
            btnWake.SetBounds(px + pw - S(118), py, S(118), S(26));
            setPill.SetBounds(px, py + S(2), pw - S(124), S(22)); py += S(34);
            tgAuto.SetBounds(px, py, pw, S(34)); py += S(38);
            tgSound.SetBounds(px, py, pw, S(24));
            int by = rest - S(48);
            btnSave.SetBounds(W - S(20) - S(110), by, S(110), S(36));
            btnCancelSettings.SetBounds(btnSave.Left - S(8) - S(96), by, S(96), S(36));
            // «+»
            addPanel.SetBounds(0, y, W, rest);
            lblAddHint.SetBounds(S(24), S(66), W - S(48), S(34));
            inName.SetBounds(px, S(108), pw, S(48));
            btnCreate.SetBounds(px, S(162), pw, S(38));
            inCode.SetBounds(px, S(218), pw, S(48));
            btnJoin.SetBounds(px, S(272), pw, S(38));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCHITTEST && !collapsed)
            {
                int py = (short)(((long)m.LParam >> 16) & 0xFFFF);
                if (py - Top < S(5)) { m.Result = (IntPtr)HTTOP; return; }
            }
            base.WndProc(ref m);
            if (m.Msg == WM_EXITSIZEMOVE && !collapsed) { Store.Settings["chatDockH"] = dockH.ToString(); Store.Save(); }
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (!applying && !collapsed && bar != null && Height != dockH) { dockH = Height; ApplyState(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!collapsed) using (SolidBrush b = new SolidBrush(Ui.Panel)) e.Graphics.FillRectangle(b, 0, 0, Width, S(5));
            using (Pen p = new Pen(Ui.Border, 1f)) e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            base.OnPaint(e);
        }
    }
}
