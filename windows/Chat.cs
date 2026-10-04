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
        public string Code, Name;
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
            link.Connection += delegate(bool up) { Post(delegate { if (link != mine) return; Connected = up; if (up) { LastError = null; Hello(); } Fire(); }); };
            link.Failed += delegate(string why) { Post(delegate { if (link != mine) return; LastError = why; Fire(); }); };
            link.Received += delegate(Dictionary<string, object> d) { Post(delegate { if (link == mine) Handle(d); }); };
            link.Start();
        }

        static void Disconnect()
        {
            if (link != null) { link.Stop(); link = null; }
            Connected = false; LastError = null;
        }

        public static void Stop() { Disconnect(); }

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
            names[code] = grp.Name;
            grp.Members.Clear();
            foreach (Dictionary<string, object> m in J.List(g, "members")) grp.Members.Add(ParseMember(m));
            if (grp.Messages.Count == 0)
                foreach (Dictionary<string, object> m in J.List(g, "history")) { ChatMsg x = ParseMsg(m); if (x.Code == null) x.Code = code; grp.Messages.Add(x); }
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
                case "error": Notice2(J.Str(d, "message") ?? "error", true); break;
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
                bool watching = ChatForm.IsOpen && Form.ActiveForm is ChatForm && Chat.Active == g;
                if (!watching) ChatToast.Message(g, m);
                Beep();
            };
            Chat.JoinRequested += delegate(JoinReq r) { ChatToast.Request(r); Beep(); };
            Chat.JoinSettled += delegate(string id) { ChatToast.Settled(id); };
            Chat.Notice += delegate(string text, bool error) { if (!ChatForm.IsOpen) ChatToast.Info(text, error); };
            Chat.Start();
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
            { "copied",   new[] { "Kod nusxalandi", "Код нусхаланди", "Код скопирован", "Code copied" } },
            { "today",    new[] { "Bugun", "Бугун", "Сегодня", "Today" } },
            { "yesterday", new[] { "Kecha", "Кеча", "Вчера", "Yesterday" } },
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
        static readonly Color[] tints = { Palette.Emerald, Palette.Lapis, Palette.GoldDeep, Palette.Terracotta, Palette.Saffron, Color.FromArgb(90, 70, 130) };

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
            TopMost = true; DoubleBuffered = true; BackColor = Palette.EmeraldDark;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            Size = new Size((int)(360 * k), (int)((hasButtons ? 112 : 72) * k));
            if (hasButtons)
            {
                Button a = MakeBtn(ChatT.T("allow"), 14, true), d = MakeBtn(ChatT.T("deny"), 130, false);
                a.Click += delegate { Chat.Decide(RequestId, true); Close(); };
                d.Click += delegate { Chat.Decide(RequestId, false); Close(); };
                Controls.Add(a); Controls.Add(d);
            }
            else Click += delegate { ChatForm.ShowSingle(); Close(); };
            if (seconds > 0) { life.Interval = seconds * 1000; life.Tick += delegate { Close(); }; life.Start(); }
        }

        Button MakeBtn(string text, int x, bool primary)
        {
            Button b = new Button();
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
            b.BackColor = primary ? Palette.Gold : Palette.Emerald; b.ForeColor = primary ? Palette.Ink : Palette.Ivory;
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
            using (Pen p = new Pen(Palette.Gold, 2f)) g.DrawRectangle(p, 1, 1, Width - 3, Height - 3);
            int av = (int)(44 * k), pad = (int)(14 * k);
            if (who != null) Avatars.Draw(g, new Rectangle(pad, pad, av, av), who.UserId, who.Nick, who.Avatar);
            int x = who != null ? pad + av + (int)(10 * k) : pad;
            Rectangle r1 = new Rectangle(x, pad - 2, Width - x - pad, (int)(24 * k));
            using (Font f = Fonts.Get("NB Sans Bold", 9.5f, "Segoe UI", FontStyle.Bold))
                TextRenderer.DrawText(g, line1, f, r1, Palette.Gold, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            if (line2.Length > 0)
            {
                Rectangle r2 = new Rectangle(x, pad + (int)(22 * k), Width - x - pad, (int)(32 * k));
                using (Font f = Fonts.Get("NB Sans", 9.5f, "Segoe UI", FontStyle.Regular))
                    TextRenderer.DrawText(g, line2, f, r2, Palette.Ivory, TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
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

        public static void ShowWindow() { ChatForm.ShowSingle(); }

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
                ToolStripMenuItem st = MenuUi.Sub(ChatT.T("connecting"), null);
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
                root.DropDownItems.Add(MenuUi.Item(ChatT.T("create") + "…", null, delegate
                {
                    string n = Dlg.Ask(null, ChatT.T("create"), ChatT.T("newName"), "");
                    if (!string.IsNullOrEmpty(n)) Chat.CreateGroup(n);
                }));
                root.DropDownItems.Add(MenuUi.Item(ChatT.T("join") + "…", null, delegate
                {
                    string c = Dlg.Ask(null, ChatT.T("join"), ChatT.T("enterCode"), "");
                    if (!string.IsNullOrEmpty(c)) Chat.RequestJoin(c);
                }));
                root.DropDownItems.Add(new ToolStripSeparator());
            }
            root.DropDownItems.Add(MenuUi.Item(ChatT.T("openWin"), null, ShowWindow));
        }
    }

    // ───────────────────────── Дизайн окна чата ─────────────────────────
    // Единая тёмная тема от выбранного скина: фон, панели, золотой акцент, читаемые состояния кнопок.
    static class Ui
    {
        public static readonly float K = Dpi();
        static float Dpi() { using (Bitmap b = new Bitmap(1, 1)) using (Graphics g = Graphics.FromImage(b)) return g.DpiX / 96f; }
        public static int S(float v) { return (int)Math.Round(v * K); }

        public static Color Bg { get { return View.Mix(Palette.EmeraldDark, Color.Black, 0.55); } }       // окно и сообщения
        public static Color Panel { get { return View.Mix(Palette.EmeraldDark, Color.Black, 0.28); } }    // боковая панель
        public static Color Raised { get { return View.Mix(Palette.EmeraldDark, Palette.Ivory, 0.12); } } // поля, чужие сообщения
        public static Color Text { get { return Palette.Ivory; } }
        public static Color Dim { get { return View.Mix(Palette.Ivory, Bg, 0.42); } }
        public static Color Online { get { return Color.FromArgb(70, 200, 120); } }

        static Font body, bold, small, big, caps;
        public static Font Body { get { if (body == null) body = Fonts.Get("NB Sans", 10f, "Segoe UI", FontStyle.Regular); return body; } }
        public static Font Medium { get { if (bold == null) bold = Fonts.Get("NB Sans Medium", 10f, "Segoe UI Semibold", FontStyle.Regular); return bold; } }
        public static Font Small { get { if (small == null) small = Fonts.Get("NB Sans", 8.5f, "Segoe UI", FontStyle.Regular); return small; } }
        public static Font Big { get { if (big == null) big = Fonts.Get("NB Sans Bold", 14f, "Segoe UI", FontStyle.Bold); return big; } }
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
            Height = Ui.S(kind == UiKind.Chip ? 34 : 38);
        }
        public int PreferredWidth()
        {
            Size t = TextRenderer.MeasureText(Text, Font, new Size(2000, 100), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return t.Width + Ui.S(Kind == UiKind.Chip ? 34 : 28);
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
            double vis = Enabled ? 1.0 : 0.45;
            Color fill = Color.Empty, border = Color.Empty, fg;
            switch (Kind)
            {
                case UiKind.Primary:
                    fill = down ? View.Mix(Palette.Gold, Color.Black, 0.18) : (hover ? View.Mix(Palette.Gold, Color.White, 0.18) : Palette.Gold);
                    fg = Palette.Ink; break;
                case UiKind.Danger:
                    border = Palette.Terracotta; fill = down ? Color.FromArgb(70, Palette.Terracotta) : (hover ? Color.FromArgb(40, Palette.Terracotta) : Color.Empty);
                    fg = Color.FromArgb(255, 170, 140); break;
                case UiKind.Chip:
                    border = View.Mix(behind, Palette.Gold, 0.55); fill = down ? Palette.Emerald : (hover ? View.Mix(Palette.Emerald, Palette.Gold, 0.15) : View.Mix(behind, Palette.Emerald, 0.55));
                    fg = Palette.Ivory; break;
                default:
                    border = View.Mix(behind, Palette.Gold, 0.65); fill = down ? Color.FromArgb(50, Palette.Gold) : (hover ? Color.FromArgb(28, Palette.Gold) : Color.Empty);
                    fg = Palette.Ivory; break;
            }
            if (fill != Color.Empty) Ui.FillRound(g, r, rad, Ui.Fade(fill, behind, vis * (fill.A / 255.0)));
            if (border != Color.Empty) Ui.StrokeRound(g, r, rad, Ui.Fade(border, behind, vis), 1.2f);
            Color text = Enabled ? fg : View.Mix(behind, fg, 0.5);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, new Rectangle(2, 2, Width - 5, Height - 5), Math.Max(2, rad - 2), Palette.Ivory, 1f);
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
            Ui.FillRound(g, track, th / 2, on ? Palette.Gold : View.Mix(behind, Palette.Ivory, 0.22));
            int kn = th - Ui.S(6);
            int kx = on ? tw - kn - Ui.S(3) : Ui.S(3);
            using (SolidBrush b = new SolidBrush(on ? Palette.Ink : Palette.Ivory)) g.FillEllipse(b, kx, ty + Ui.S(3), kn, kn);
            if (Focused && ShowFocusCues) Ui.StrokeRound(g, new Rectangle(-1, ty - 1, tw + 1, th + 1), th / 2, Palette.Ivory, 1f);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(tw + Ui.S(10), 0, Width - tw - Ui.S(10), Height), Palette.Ivory, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
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
            Height = Ui.S(54);
        }
        public override string Text { get { return Box.Text; } set { Box.Text = value; } }
        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.BackColor = Ui.Raised; Box.ForeColor = Ui.Text;
            Box.SetBounds(Ui.S(12), Ui.S(26), Width - Ui.S(24), Ui.S(20));
            base.OnLayout(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            Color behind = Parent != null ? Parent.BackColor : Ui.Bg;
            g.Clear(behind);
            Rectangle r = new Rectangle(0, Ui.S(14), Width - 1, Height - Ui.S(14) - 1);
            Ui.FillRound(g, r, Ui.S(8), Ui.Raised);
            Ui.StrokeRound(g, r, Ui.S(8), Box.Focused ? Palette.Gold : View.Mix(Ui.Raised, Palette.Ivory, 0.18), Box.Focused ? 1.6f : 1f);
            TextRenderer.DrawText(g, caption.ToUpperInvariant(), Ui.Caps, new Rectangle(Ui.S(2), 0, Width, Ui.S(14)), Palette.Gold, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
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
            Ui.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Height / 2, View.Mix(behind, Palette.Ivory, 0.10));
            int d = Ui.S(9);
            using (SolidBrush b = new SolidBrush(Dot)) g.FillEllipse(b, Ui.S(10), (Height - d) / 2, d, d);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(26), 0, Width - Ui.S(34), Height), Palette.Ivory, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
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
            using (Pen p = new Pen(Palette.Gold, 2f)) g.DrawEllipse(p, r.X - 3, r.Y - 3, r.Width + 5, r.Height + 5);
            int b = Ui.S(20);
            Rectangle br = new Rectangle(Width - b - 1, Height - b - 1, b, b);
            using (SolidBrush sb = new SolidBrush(Palette.Gold)) g.FillEllipse(sb, br);
            using (Pen pp = new Pen(Palette.Ink, 2f))
            {
                g.DrawLine(pp, br.X + br.Width / 2f, br.Y + b * 0.28f, br.X + br.Width / 2f, br.Bottom - b * 0.28f);
                g.DrawLine(pp, br.X + b * 0.28f, br.Y + br.Height / 2f, br.Right - b * 0.28f, br.Y + br.Height / 2f);
            }
        }
    }

    class BufListBox : ListBox
    {
        public BufListBox() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); }
        protected override void OnPaintBackground(PaintEventArgs e) { using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillRectangle(b, e.ClipRectangle); }
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
            if (hover || down) using (SolidBrush b = new SolidBrush(Color.FromArgb(down ? 50 : 30, Palette.Ivory))) g.FillEllipse(b, 1, 1, Width - 3, Height - 3);
            using (Font f = new Font(Family, Height * 0.38f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(g, Glyph, f, new Rectangle(0, 0, Width, Height), Enabled ? Palette.Ivory : Ui.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    // Настройки профиля и сервера (как «Настройки» в Telegram): ник, фото, адрес, переключатели
    class ChatSettings : Form
    {
        readonly UiInput inNick, inServer;
        readonly UiToggle tgAuto, tgSound;
        readonly AvatarBox avatar;

        public ChatSettings()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = ChatT.T("settings");
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Ui.S(480), Ui.S(500));
            BackColor = Ui.Panel; ForeColor = Ui.Text; Font = Ui.Body;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            int pad = Ui.S(24), w = ClientSize.Width - 2 * pad, y = Ui.S(22);
            avatar = new AvatarBox { Bounds = new Rectangle((ClientSize.Width - Ui.S(96)) / 2, y, Ui.S(96), Ui.S(96)) };
            avatar.Click += delegate { ChatForm.PickAvatar(this); avatar.Invalidate(); };
            Label hint = new Label { Text = ChatT.T("pick"), Font = Ui.Small, ForeColor = Ui.Dim, AutoSize = false, TextAlign = ContentAlignment.MiddleCenter,
                                      Bounds = new Rectangle(pad, y + Ui.S(100), w, Ui.S(20)), BackColor = Color.Transparent };
            y += Ui.S(134);
            inNick = new UiInput(ChatT.T("nick"), Chat.Nick ?? "") { Bounds = new Rectangle(pad, y, w, Ui.S(54)) }; inNick.Box.MaxLength = 24;
            y += Ui.S(66);
            inServer = new UiInput(ChatT.T("server"), Chat.Server ?? "") { Bounds = new Rectangle(pad, y, w, Ui.S(54)) };
            y += Ui.S(70);
            tgAuto = new UiToggle(ChatT.T("auto"), Store.Get("chatAuto", "0") == "1") { Bounds = new Rectangle(pad, y, w, Ui.S(40)) };
            y += Ui.S(46);
            tgSound = new UiToggle(ChatT.T("sound"), Store.Get("chatSound", "1") != "0") { Bounds = new Rectangle(pad, y, w, Ui.S(26)) };
            UiButton save = new UiButton(ChatT.T("save"), UiKind.Primary) { Bounds = new Rectangle(ClientSize.Width - pad - Ui.S(120), ClientSize.Height - Ui.S(60), Ui.S(120), Ui.S(40)) };
            UiButton cancel = new UiButton(ChatT.T("cancel"), UiKind.Secondary) { Bounds = new Rectangle(save.Left - Ui.S(12) - Ui.S(110), save.Top, Ui.S(110), Ui.S(40)) };
            save.Click += delegate
            {
                Store.Settings["chatAuto"] = tgAuto.Checked ? "1" : "0";
                Store.Settings["chatSound"] = tgSound.Checked ? "1" : "0";
                Store.Save();
                Chat.Configure(inServer.Text, inNick.Text);
                DialogResult = DialogResult.OK; Close();
            };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.AddRange(new Control[] { avatar, hint, inNick, inServer, tgAuto, tgSound, cancel, save });
        }
    }

    // ───────────────────────── Окно чата (в духе Telegram / WhatsApp) ─────────────────────────
    class DaySep { public string Text; }

    class ChatForm : Form
    {
        static ChatForm instance;
        public static bool IsOpen { get { return instance != null && !instance.IsDisposed; } }
        public static void CloseIfOpen() { if (IsOpen) instance.Close(); }
        public static void ShowSingle()
        {
            if (IsOpen) { if (instance.WindowState == FormWindowState.Minimized) instance.WindowState = FormWindowState.Normal; instance.Activate(); return; }
            instance = new ChatForm();
            instance.Show();
        }

        public static void PickAvatar(IWin32Window owner)
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

        Panel side, profile, chatHead, main, quick, empty, members;
        AvatarBox avatarBox;
        Label lblNick, lblName, lblSub;
        StatusPill pill;
        UiIconButton btnGear, btnMore, btnCopy;
        UiButton btnNew, btnJoin;
        BufListBox lstGroups, lstMsg;
        readonly List<UiButton> chips = new List<UiButton>();
        string noticeText; DateTime noticeAt = DateTime.MinValue; bool noticeError;
        System.Windows.Forms.Timer statusTimer;
        string shownCode; int shownCount = -1;
        bool updating;
        ContextMenuStrip moreMenu;

        int S(float v) { return Ui.S(v); }

        ChatForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = ChatT.T("title");
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(980), S(660)); MinimumSize = new Size(S(820), S(520));
            BackColor = Ui.Bg; ForeColor = Ui.Text; Font = Ui.Body;
            DoubleBuffered = true;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Build();
            Chat.Changed += OnChanged; Chat.Notice += OnNotice;
            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += delegate { UpdatePill(); };
            statusTimer.Start();
            FormClosed += delegate { statusTimer.Stop(); Chat.Changed -= OnChanged; Chat.Notice -= OnNotice; Chat.Active = null; };
            Resize += delegate { Relayout(); };
            Load += delegate
            {
                Relayout(); OnChanged();
                if (!Chat.Configured) BeginInvoke((MethodInvoker)delegate { OpenSettings(); });   // первый запуск: сразу настройки
            };
        }

        Label MakeLabel(Font f, Color c) { return new Label { Font = f, ForeColor = c, AutoSize = false, BackColor = Color.Transparent, UseMnemonic = false, AutoEllipsis = true }; }

        void Build()
        {
            // ---- левая колонка: профиль, список чатов, кнопки
            side = new Panel { BackColor = Ui.Panel };
            profile = new Panel { BackColor = Ui.Panel };
            avatarBox = new AvatarBox(); avatarBox.Click += delegate { OpenSettings(); };
            lblNick = MakeLabel(Ui.Medium, Palette.Ivory);
            pill = new StatusPill();
            btnGear = new UiIconButton(""); btnGear.Click += delegate { OpenSettings(); };
            profile.Controls.AddRange(new Control[] { avatarBox, lblNick, pill, btnGear });
            profile.Paint += delegate(object o, PaintEventArgs e) { using (Pen p = new Pen(Color.FromArgb(40, Palette.Ivory))) e.Graphics.DrawLine(p, 0, profile.Height - 1, profile.Width, profile.Height - 1); };
            lstGroups = new BufListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(72), BorderStyle = BorderStyle.None, BackColor = Ui.Panel, IntegralHeight = false };
            lstGroups.DrawItem += DrawGroup;
            lstGroups.SelectedIndexChanged += delegate { SelectGroup(); };
            btnNew = new UiButton("+  " + ChatT.T("create"), UiKind.Primary);
            btnNew.Click += delegate { string n = Dlg.Ask(this, ChatT.T("create"), ChatT.T("newName"), ""); if (!string.IsNullOrEmpty(n)) Chat.CreateGroup(n); };
            btnJoin = new UiButton(ChatT.T("join"), UiKind.Secondary);
            btnJoin.Click += delegate { string c = Dlg.Ask(this, ChatT.T("join"), ChatT.T("enterCode"), ""); if (!string.IsNullOrEmpty(c)) Chat.RequestJoin(c); };
            side.Controls.AddRange(new Control[] { profile, lstGroups, btnNew, btnJoin });

            // ---- правая часть: шапка чата, сообщения, быстрые ответы
            main = new Panel { BackColor = Ui.Bg };
            chatHead = new Panel { BackColor = Ui.Panel };
            chatHead.Paint += PaintHead;
            lblName = MakeLabel(Ui.Big, Palette.Ivory);
            lblSub = MakeLabel(Ui.Small, Ui.Dim);
            members = new Panel { BackColor = Ui.Panel }; members.Paint += PaintMemberStack;
            btnCopy = new UiIconButton(""); btnCopy.Click += delegate { CopyCode(); };
            btnMore = new UiIconButton("");
            moreMenu = new ContextMenuStrip(); MenuUi.Style(moreMenu);
            btnMore.Click += delegate { BuildMoreMenu(); moreMenu.Show(btnMore, new Point(btnMore.Width, btnMore.Height), ToolStripDropDownDirection.BelowLeft); };
            chatHead.Controls.AddRange(new Control[] { lblName, lblSub, members, btnCopy, btnMore });
            lstMsg = new BufListBox { DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.None, BackColor = Ui.Bg, IntegralHeight = false };
            lstMsg.MeasureItem += MeasureMsg; lstMsg.DrawItem += DrawMsg;
            lstMsg.Resize += delegate { FillMessages(false); };
            empty = new Panel { BackColor = Ui.Bg }; empty.Paint += PaintEmpty;
            quick = new Panel { BackColor = Ui.Panel };
            quick.Paint += delegate(object o, PaintEventArgs e) { using (Pen p = new Pen(Color.FromArgb(40, Palette.Ivory))) e.Graphics.DrawLine(p, 0, 0, quick.Width, 0); };
            foreach (string id in ChatT.PresetIds)
            {
                string pid = id;
                UiButton b = new UiButton(ChatT.Preset(id, id), UiKind.Chip);
                b.Click += delegate { ChatGroup g = Current; if (g != null && Chat.Connected) Chat.SendPreset(g.Code, pid); };
                quick.Controls.Add(b); chips.Add(b);
            }
            main.Controls.AddRange(new Control[] { chatHead, lstMsg, empty, quick });
            Controls.Add(main); Controls.Add(side);
        }

        void OpenSettings()
        {
            using (ChatSettings f = new ChatSettings()) f.ShowDialog(this);
            RefreshProfile();
            OnChanged();
        }
        void RefreshProfile() { lblNick.Text = string.IsNullOrEmpty(Chat.Nick) ? ChatT.T("nick") : Chat.Nick; avatarBox.Invalidate(); }

        void CopyCode() { ChatGroup g = Current; if (g != null) { try { Clipboard.SetText(g.Code); noticeText = ChatT.T("copied"); noticeError = false; noticeAt = DateTime.Now; UpdatePill(); } catch { } } }

        void BuildMoreMenu()
        {
            moreMenu.Items.Clear();
            ChatGroup g = Current; if (g == null) return;
            moreMenu.Items.Add(MenuUi.Item(ChatT.T("copy"), "", delegate { CopyCode(); }));
            moreMenu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem leave = MenuUi.Item(ChatT.T("leave"), "", delegate { Chat.Leave(g.Code); });
            leave.Enabled = Chat.Connected;
            moreMenu.Items.Add(leave);
        }

        // ---- раскладка вручную
        void Relayout()
        {
            if (side == null || ClientSize.Width < 100) return;
            int W = ClientSize.Width, H = ClientSize.Height, sw = Math.Max(S(300), Math.Min(S(360), W / 3));
            side.SetBounds(0, 0, sw, H);
            int ph = S(84);
            profile.SetBounds(0, 0, sw, ph);
            avatarBox.SetBounds(S(10), S(14), S(56), S(56));
            btnGear.SetBounds(sw - S(52), S(22), S(40), S(40));
            lblNick.SetBounds(S(74), S(14), sw - S(74) - S(60), S(24));
            pill.SetBounds(S(74), S(42), sw - S(74) - S(60), S(24));
            int pad = S(12), bw = sw - 2 * pad, yb = H - S(14);
            btnJoin.SetBounds(pad, yb - S(40), bw, S(40)); yb -= S(40) + S(8);
            btnNew.SetBounds(pad, yb - S(42), bw, S(42)); yb -= S(42) + S(10);
            lstGroups.SetBounds(0, ph, sw, Math.Max(S(40), yb - ph));

            main.SetBounds(sw, 0, W - sw, H);
            int mw = main.Width, mh = main.Height;
            int qpad = S(16), x = qpad, y = S(12), rowH = S(34), gap = S(8);
            foreach (UiButton b in chips)
            {
                int w = b.PreferredWidth();
                if (x + w > mw - qpad && x > qpad) { x = qpad; y += rowH + gap; }
                b.SetBounds(x, y, w, rowH); x += w + gap;
            }
            int qh = y + rowH + S(14);
            quick.SetBounds(0, mh - qh, mw, qh);
            int hh = S(68);
            chatHead.SetBounds(0, 0, mw, hh);
            btnMore.SetBounds(mw - S(52), S(14), S(40), S(40));
            btnCopy.SetBounds(mw - S(96), S(14), S(40), S(40));
            members.SetBounds(Math.Max(S(200), mw - S(104) - S(170)), S(14), S(166), S(40));
            lblName.SetBounds(S(76), S(10), Math.Max(S(80), members.Left - S(76) - S(10)), S(28));
            lblSub.SetBounds(S(76), S(38), Math.Max(S(80), members.Left - S(76) - S(10)), S(20));
            lstMsg.SetBounds(0, hh, mw, Math.Max(S(40), mh - qh - hh));
            empty.SetBounds(0, 0, mw, mh - qh);
            chatHead.Invalidate(); members.Invalidate(); empty.Invalidate();
        }

        ChatGroup Current { get { return lstGroups.SelectedIndex >= 0 && lstGroups.SelectedIndex < Chat.Groups.Count ? Chat.Groups[lstGroups.SelectedIndex] : null; } }

        void OnChanged()
        {
            if (IsDisposed || updating) return;
            updating = true;
            try
            {
                ChatGroup keep = Current;
                lstGroups.BeginUpdate();
                lstGroups.Items.Clear();
                foreach (ChatGroup g in Chat.Groups) lstGroups.Items.Add(g.Name);
                if (keep != null && Chat.Groups.Contains(keep)) lstGroups.SelectedIndex = Chat.Groups.IndexOf(keep);
                else if (Chat.Groups.Count > 0) lstGroups.SelectedIndex = 0;
                lstGroups.EndUpdate();
                SelectGroup();
                RefreshProfile();
                UpdatePill();
                bool can = Chat.Connected && Current != null;
                foreach (UiButton b in chips) b.Enabled = can;
                btnNew.Enabled = btnJoin.Enabled = Chat.Connected;
                lstGroups.Invalidate();
            }
            finally { updating = false; }
        }

        void OnNotice(string text, bool error) { noticeText = text; noticeError = error; noticeAt = DateTime.Now; UpdatePill(); }

        // Событие (ошибка, «ожидание подтверждения») показываем 8 секунд, дальше — состояние соединения
        void UpdatePill()
        {
            if (pill == null || IsDisposed) return;
            Color green = Ui.Online, amber = Color.FromArgb(240, 178, 84), red = Color.FromArgb(240, 110, 90);
            if (!Chat.Configured) { pill.Set(amber, ChatT.T("setup")); return; }
            if (noticeText != null && (DateTime.Now - noticeAt).TotalSeconds <= 8) { pill.Set(noticeError ? red : green, noticeText); return; }
            if (Chat.Connected) pill.Set(green, ChatT.T("connected"));
            else if (!string.IsNullOrEmpty(Chat.LastError)) pill.Set(red, ChatT.T("connecting") + " " + Chat.LastError);
            else pill.Set(amber, ChatT.T("connecting"));
        }

        void SelectGroup()
        {
            ChatGroup g = Current;
            Chat.Active = g;
            if (g != null) g.Unread = 0;
            lstGroups.Invalidate();
            bool has = g != null;
            chatHead.Visible = lstMsg.Visible = quick.Visible = has;
            empty.Visible = !has;
            if (has)
            {
                lblName.Text = g.Name;
                lblSub.Text = string.Format(ChatT.T("code"), g.Code) + "   ·   " + g.Members.Count + " / " + string.Format(ChatT.T("online"), g.OnlineCount);
                members.Invalidate(); chatHead.Invalidate();
            }
            else { lstMsg.Items.Clear(); shownCount = -1; shownCode = null; empty.Invalidate(); return; }
            if (shownCode != g.Code || shownCount != g.Messages.Count) FillMessages(true);
        }

        string DayLabel(DateTime d)
        {
            if (d.Date == DateTime.Today) return ChatT.T("today");
            if (d.Date == DateTime.Today.AddDays(-1)) return ChatT.T("yesterday");
            return d.ToString("d MMMM");
        }

        void FillMessages(bool toEnd)
        {
            ChatGroup g = Current;
            if (g == null || lstMsg == null) return;
            int top = lstMsg.TopIndex;
            lstMsg.BeginUpdate();
            lstMsg.Items.Clear();
            DateTime lastDay = DateTime.MinValue;
            foreach (ChatMsg m in g.Messages)
            {
                if (m.Time.Date != lastDay) { lstMsg.Items.Add(new DaySep { Text = DayLabel(m.Time) }); lastDay = m.Time.Date; }
                lstMsg.Items.Add(m);
            }
            lstMsg.EndUpdate();
            if (toEnd || shownCode != g.Code) { if (lstMsg.Items.Count > 0) lstMsg.TopIndex = lstMsg.Items.Count - 1; }
            else if (top < lstMsg.Items.Count) lstMsg.TopIndex = top;
            shownCode = g.Code; shownCount = g.Messages.Count;
        }

        // ---- список чатов: аватар, название, последнее сообщение, время, непрочитанные
        void DrawGroup(object s, DrawItemEventArgs e)
        {
            Graphics gr = e.Graphics; gr.SmoothingMode = SmoothingMode.AntiAlias;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Ui.Panel)) gr.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= Chat.Groups.Count) return;
            ChatGroup g = Chat.Groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle row = new Rectangle(e.Bounds.X + S(6), e.Bounds.Y + S(2), e.Bounds.Width - S(12), e.Bounds.Height - S(4));
            if (sel) Ui.FillRound(gr, row, S(12), View.Mix(Ui.Panel, Palette.Emerald, 0.95));
            int av = S(52);
            Rectangle ar = new Rectangle(row.X + S(10), row.Y + (row.Height - av) / 2, av, av);
            Avatars.Draw(gr, ar, g.Code, g.Name, null);
            int tx = ar.Right + S(12), right = row.Right - S(12);
            ChatMsg last = g.Messages.Count > 0 ? g.Messages[g.Messages.Count - 1] : null;
            string time = last == null ? "" : (last.Time.Date == DateTime.Today ? last.Time.ToString("HH:mm") : last.Time.ToString("dd.MM"));
            int tw = time.Length == 0 ? 0 : TextRenderer.MeasureText(gr, time, Ui.Small, new Size(200, 30), TextFormatFlags.NoPadding).Width;
            TextRenderer.DrawText(gr, g.Name, Ui.Medium, new Rectangle(tx, row.Y + S(10), right - tx - tw - S(8), S(22)), Palette.Ivory, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (tw > 0) TextRenderer.DrawText(gr, time, Ui.Small, new Point(right - tw, row.Y + S(13)), g.Unread > 0 ? Palette.Gold : Ui.Dim);
            string prev = last == null ? string.Format(ChatT.T("online"), g.OnlineCount) + " / " + g.Members.Count
                                       : (last.From == Chat.UserId ? ChatT.T("you") : last.Nick) + ": " + last.Display;
            int prevRight = g.Unread > 0 ? right - S(34) : right;
            TextRenderer.DrawText(gr, prev, Ui.Body, new Rectangle(tx, row.Y + S(36), prevRight - tx, S(22)), Ui.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            if (g.Unread > 0)
            {
                Rectangle bd = new Rectangle(right - S(26), row.Y + S(36), S(26), S(22));
                Ui.FillRound(gr, bd, S(11), Palette.Gold);
                TextRenderer.DrawText(gr, g.Unread > 99 ? "99+" : g.Unread.ToString(), Ui.Small, bd, Palette.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // ---- шапка чата: аватар группы слева
        void PaintHead(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(40, Palette.Ivory))) g.DrawLine(p, 0, chatHead.Height - 1, chatHead.Width, chatHead.Height - 1);
            ChatGroup grp = Current; if (grp == null) return;
            Avatars.Draw(g, new Rectangle(S(16), S(10), S(48), S(48)), grp.Code, grp.Name, null);
        }

        // ---- участники: стопка круглых аватаров с точкой онлайн, как в Telegram
        void PaintMemberStack(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            ChatGroup grp = Current; if (grp == null) return;
            List<ChatMember> sorted = new List<ChatMember>(grp.Members);
            sorted.Sort(delegate(ChatMember a, ChatMember b) { return a.Online != b.Online ? (a.Online ? -1 : 1) : string.Compare(a.Nick, b.Nick, StringComparison.CurrentCultureIgnoreCase); });
            int av = S(34), step = S(24), n = Math.Min(sorted.Count, 5);
            int total = n == 0 ? 0 : av + (n - 1) * step;
            int x = members.Width - total, y = (members.Height - av) / 2;
            for (int i = n - 1; i >= 0; i--)
            {
                ChatMember m = sorted[i];
                Rectangle r = new Rectangle(x + i * step, y, av, av);
                using (SolidBrush ring = new SolidBrush(Ui.Panel)) g.FillEllipse(ring, r.X - 2, r.Y - 2, av + 4, av + 4);
                Avatars.Draw(g, r, m.UserId, m.Nick, m.Avatar);
                if (!m.Online) using (SolidBrush dim = new SolidBrush(Color.FromArgb(140, Ui.Panel))) g.FillEllipse(dim, r);
                int d = S(10);
                Rectangle dr = new Rectangle(r.Right - d, r.Bottom - d, d, d);
                using (SolidBrush rg = new SolidBrush(Ui.Panel)) g.FillEllipse(rg, dr.X - 2, dr.Y - 2, d + 4, d + 4);
                using (SolidBrush dot = new SolidBrush(m.Online ? Ui.Online : Color.FromArgb(120, 124, 130))) g.FillEllipse(dot, dr);
            }
            if (sorted.Count > n)
            {
                string more = "+" + (sorted.Count - n);
                TextRenderer.DrawText(g, more, Ui.Small, new Rectangle(0, 0, x - S(6), members.Height), Ui.Dim, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // ---- пустое состояние: орнамент и подсказка
        void PaintEmpty(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            int w = empty.Width, h = empty.Height;
            float cx = w / 2f, cy = h / 2f - S(40), r = S(60);
            using (GraphicsPath p = new GraphicsPath())
            {
                Palette.Star8(p, cx, cy, r);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(28, Palette.Emerald))) g.FillPath(b, p);
                using (Pen pen = new Pen(Color.FromArgb(150, Palette.Gold), 1.6f)) g.DrawPath(pen, p);
            }
            using (Pen ring = new Pen(Color.FromArgb(70, Palette.Gold), 1f)) g.DrawEllipse(ring, cx - r * 0.8f, cy - r * 0.8f, r * 1.6f, r * 1.6f);
            TextRenderer.DrawText(g, ChatT.T("noGroup"), Ui.Big, new Rectangle(S(40), (int)cy + (int)r + S(24), w - S(80), S(30)), Palette.Ivory, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
            TextRenderer.DrawText(g, ChatT.T("noGroupHint"), Ui.Body, new Rectangle(S(60), (int)cy + (int)r + S(62), w - S(120), S(50)), Ui.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        }

        // ---- сообщения: пузыри с «хвостиком», аватар у последнего в серии, разделители дней
        int BubbleMaxW() { return Math.Max(S(180), (int)(lstMsg.ClientSize.Width * 0.66)); }
        Size TextSize(Graphics g, string text, int maxW)
        {
            return TextRenderer.MeasureText(g, text, Ui.Body, new Size(maxW, 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
        ChatMsg MsgAt(int i) { return i >= 0 && i < lstMsg.Items.Count ? lstMsg.Items[i] as ChatMsg : null; }
        bool FirstOfRun(int i) { ChatMsg p = MsgAt(i - 1), m = MsgAt(i); return p == null || m == null || p.From != m.From; }
        bool LastOfRun(int i) { ChatMsg n = MsgAt(i + 1), m = MsgAt(i); return n == null || m == null || n.From != m.From; }

        void MeasureMsg(object s, MeasureItemEventArgs e)
        {
            DaySep d = lstMsg.Items[e.Index] as DaySep;
            if (d != null) { e.ItemHeight = S(38); return; }
            ChatMsg m = (ChatMsg)lstMsg.Items[e.Index];
            bool mine = m.From == Chat.UserId;
            Size t = TextSize(e.Graphics, m.Display, BubbleMaxW() - S(28));
            int bubbleH = S(9) + t.Height + S(3) + S(14) + S(7);
            int nameH = (!mine && FirstOfRun(e.Index)) ? S(20) : 0;
            e.ItemHeight = nameH + bubbleH + (LastOfRun(e.Index) ? S(10) : S(3));
        }

        void DrawMsg(object s, DrawItemEventArgs e)
        {
            Graphics gr = e.Graphics; gr.SmoothingMode = SmoothingMode.AntiAlias;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(Ui.Bg)) gr.FillRectangle(b, e.Bounds);
            if (e.Index < 0 || e.Index >= lstMsg.Items.Count) return;
            DaySep d = lstMsg.Items[e.Index] as DaySep;
            if (d != null)
            {
                Size ts = TextRenderer.MeasureText(gr, d.Text, Ui.Small, new Size(400, 40), TextFormatFlags.NoPadding);
                Rectangle pr = new Rectangle(e.Bounds.X + (e.Bounds.Width - ts.Width - S(24)) / 2, e.Bounds.Y + S(8), ts.Width + S(24), S(24));
                Ui.FillRound(gr, pr, S(12), View.Mix(Ui.Bg, Palette.Ivory, 0.12));
                TextRenderer.DrawText(gr, d.Text, Ui.Small, pr, Palette.Ivory, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            ChatMsg m = (ChatMsg)lstMsg.Items[e.Index];
            ChatGroup g = Current;
            bool mine = m.From == Chat.UserId;
            bool first = FirstOfRun(e.Index), last = LastOfRun(e.Index);
            string text = m.Display;
            Size t = TextSize(gr, text, BubbleMaxW() - S(28));
            string time = m.Time.ToString("HH:mm");
            int timeW = TextRenderer.MeasureText(gr, time, Ui.Small, new Size(200, 40), TextFormatFlags.NoPadding).Width;
            int bw = Math.Max(t.Width, timeW + S(4)) + S(26);
            int bh = S(9) + t.Height + S(3) + S(14) + S(7);
            int top = e.Bounds.Y + ((!mine && first) ? S(20) : 0);
            int bx = mine ? e.Bounds.Right - S(18) - bw : e.Bounds.X + S(62);
            Rectangle br = new Rectangle(bx, top, bw, bh);
            Color fill = mine ? View.Mix(Palette.Emerald, Palette.Jade, 0.14) : Ui.Raised;
            int rad = S(14);
            using (GraphicsPath p = Ui.Round(br, rad)) using (SolidBrush fb = new SolidBrush(fill)) gr.FillPath(fb, p);
            if (last)   // «хвостик» у последнего пузыря серии
            {
                Point[] tail = mine
                    ? new[] { new Point(br.Right - S(10), br.Bottom - S(16)), new Point(br.Right + S(7), br.Bottom), new Point(br.Right - S(18), br.Bottom - S(2)) }
                    : new[] { new Point(br.X + S(10), br.Bottom - S(16)), new Point(br.X - S(7), br.Bottom), new Point(br.X + S(18), br.Bottom - S(2)) };
                using (SolidBrush fb = new SolidBrush(fill)) gr.FillPolygon(fb, tail);
            }
            if (!mine)
            {
                if (first) TextRenderer.DrawText(gr, m.Nick, Ui.Medium, new Point(bx + S(4), e.Bounds.Y + S(1)), NickColor(m.From));
                if (last)
                {
                    ChatMember who = null;
                    if (g != null) foreach (ChatMember x in g.Members) if (x.UserId == m.From) who = x;
                    Avatars.Draw(gr, new Rectangle(e.Bounds.X + S(16), br.Bottom - S(38), S(38), S(38)), m.From, m.Nick, who != null ? who.Avatar : null);
                }
            }
            TextRenderer.DrawText(gr, text, Ui.Body, new Rectangle(br.X + S(13), br.Y + S(9), t.Width + S(2), t.Height + S(2)), Palette.Ivory, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(gr, time, Ui.Small, new Point(br.Right - S(13) - timeW, br.Bottom - S(7) - S(13)), View.Mix(Palette.Ivory, fill, 0.5));
        }

        static readonly Color[] nickTints = { Color.FromArgb(240, 150, 110), Color.FromArgb(120, 200, 150), Color.FromArgb(120, 170, 240), Color.FromArgb(220, 140, 200), Color.FromArgb(230, 200, 100), Color.FromArgb(120, 210, 210) };
        static Color NickColor(string userId) { int h = 0; foreach (char c in userId ?? "") h = h * 31 + c; return nickTints[Math.Abs(h) % nickTints.Length]; }
    }
}
