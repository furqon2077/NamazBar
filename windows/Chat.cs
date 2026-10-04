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

    // ───────────────────────── Окно чата ─────────────────────────
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

        readonly float k;
        Panel header, left, right;
        Label lblStatus, lblNick, lblServer;
        TextBox tbNick, tbServer;
        Button btnSave, btnCreate, btnJoin, btnLeave, btnCopy;
        PictureBox pbAvatar;
        ListBox lstGroups, lstMsg;
        Panel strip;
        FlowLayoutPanel presets;
        CheckBox cbAuto, cbSound;
        readonly List<Button> presetBtns = new List<Button>();
        Font fName, fText, fSmall, fBold;
        string statusText = ""; bool statusError;
        DateTime noticeAt = DateTime.MinValue;   // момент последнего сообщения-события; через 8 с показываем состояние соединения
        System.Windows.Forms.Timer statusTimer;
        int lastWidth;

        int S(int v) { return (int)Math.Round(v * k); }

        ChatForm()
        {
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;
            AutoScaleMode = AutoScaleMode.None;
            Text = ChatT.T("title");
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(780), S(560)); MinimumSize = new Size(S(640), S(460));
            BackColor = Palette.Ivory; ForeColor = Palette.Ink;
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            fText = Fonts.Get("NB Sans", 10f, "Segoe UI", FontStyle.Regular);
            fName = Fonts.Get("NB Sans Medium", 10f, "Segoe UI Semibold", FontStyle.Regular);
            fBold = Fonts.Get("NB Sans Bold", 10f, "Segoe UI", FontStyle.Bold);
            fSmall = Fonts.Get("NB Sans", 8.5f, "Segoe UI", FontStyle.Regular);
            Font = fText;
            Build();
            Chat.Changed += OnChanged; Chat.Notice += OnNotice;
            statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            statusTimer.Tick += delegate { RefreshStatus(); };
            statusTimer.Start();
            FormClosed += delegate { statusTimer.Stop(); Chat.Changed -= OnChanged; Chat.Notice -= OnNotice; Chat.Active = null; };
            Load += delegate { if (Chat.Groups.Count > 0) lstGroups.SelectedIndex = 0; OnChanged(); };
        }

        static Button Flat(string text, Color back, Color fore)
        {
            Button b = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = back, ForeColor = fore, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderSize = 0;
            return b;
        }

        void Build()
        {
            // шапка: аватар, ник, сервер
            header = new Panel { Dock = DockStyle.Top, Height = S(92), BackColor = Palette.EmeraldDark };
            pbAvatar = new PictureBox { Bounds = new Rectangle(S(14), S(14), S(60), S(60)), Cursor = Cursors.Hand };
            pbAvatar.Paint += delegate(object o, PaintEventArgs e) { Avatars.Draw(e.Graphics, new Rectangle(0, 0, pbAvatar.Width - 1, pbAvatar.Height - 1), Chat.UserId, Chat.Nick, Chat.Avatar); };
            pbAvatar.Click += delegate { PickAvatar(); };
            lblNick = new Label { Text = ChatT.T("nick"), ForeColor = Palette.Gold, Font = fSmall, AutoSize = true, Location = new Point(S(88), S(10)) };
            tbNick = new TextBox { Text = Chat.Nick ?? "", Bounds = new Rectangle(S(88), S(28), S(180), S(26)), MaxLength = 24 };
            lblServer = new Label { Text = ChatT.T("server"), ForeColor = Palette.Gold, Font = fSmall, AutoSize = true, Location = new Point(S(284), S(10)) };
            tbServer = new TextBox { Text = Chat.Server ?? "", Bounds = new Rectangle(S(284), S(28), S(300), S(26)) };
            btnSave = Flat(ChatT.T("save"), Palette.Gold, Palette.Ink);
            btnSave.Bounds = new Rectangle(S(596), S(26), S(100), S(30));
            btnSave.Click += delegate { noticeAt = DateTime.MinValue; Chat.Configure(tbServer.Text, tbNick.Text); tbServer.Text = Chat.Server ?? ""; };
            lblStatus = new Label { ForeColor = Palette.Ivory, Font = fSmall, AutoSize = false, Bounds = new Rectangle(S(88), S(62), S(600), S(22)) };
            header.Controls.AddRange(new Control[] { pbAvatar, lblNick, tbNick, lblServer, tbServer, btnSave, lblStatus });

            // слева: группы
            left = new Panel { Dock = DockStyle.Left, Width = S(220), BackColor = Color.FromArgb(236, 226, 200), Padding = new Padding(S(8)) };
            Panel leftBottom = new Panel { Dock = DockStyle.Bottom, Height = S(136) };
            btnCreate = Flat(ChatT.T("create"), Palette.Emerald, Palette.Ivory);
            btnJoin = Flat(ChatT.T("join"), Palette.Emerald, Palette.Ivory);
            btnLeave = Flat(ChatT.T("leave"), Palette.Terracotta, Color.White);
            btnCreate.Bounds = new Rectangle(0, S(6), S(64), S(30));
            btnJoin.Bounds = new Rectangle(S(68), S(6), S(70), S(30));
            btnLeave.Bounds = new Rectangle(S(142), S(6), S(62), S(30));
            btnCreate.Click += delegate { string n = Dlg.Ask(this, ChatT.T("create"), ChatT.T("newName"), ""); if (!string.IsNullOrEmpty(n)) Chat.CreateGroup(n); };
            btnJoin.Click += delegate { string c = Dlg.Ask(this, ChatT.T("join"), ChatT.T("enterCode"), ""); if (!string.IsNullOrEmpty(c)) Chat.RequestJoin(c); };
            btnLeave.Click += delegate { ChatGroup g = Current; if (g != null) Chat.Leave(g.Code); };
            cbAuto = new CheckBox { Text = ChatT.T("auto"), Checked = Store.Get("chatAuto", "0") == "1", Bounds = new Rectangle(0, S(44), S(204), S(52)), Font = fSmall };
            cbAuto.CheckedChanged += delegate { Store.Settings["chatAuto"] = cbAuto.Checked ? "1" : "0"; Store.Save(); };
            cbSound = new CheckBox { Text = ChatT.T("sound"), Checked = Store.Get("chatSound", "1") != "0", Bounds = new Rectangle(0, S(100), S(204), S(24)), Font = fSmall };
            cbSound.CheckedChanged += delegate { Store.Settings["chatSound"] = cbSound.Checked ? "1" : "0"; Store.Save(); };
            leftBottom.Controls.AddRange(new Control[] { btnCreate, btnJoin, btnLeave, cbAuto, cbSound });
            lstGroups = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = S(46), BorderStyle = BorderStyle.None, BackColor = left.BackColor, IntegralHeight = false };
            lstGroups.DrawItem += DrawGroup;
            lstGroups.SelectedIndexChanged += delegate { SelectGroup(); };
            left.Controls.Add(lstGroups); left.Controls.Add(leftBottom);

            // справа: участники, сообщения, шаблоны
            right = new Panel { Dock = DockStyle.Fill };
            strip = new Panel { Dock = DockStyle.Top, Height = S(78), BackColor = Color.FromArgb(246, 238, 218) };
            strip.Paint += PaintStrip;
            btnCopy = Flat(ChatT.T("copy"), Palette.Emerald, Palette.Ivory);
            btnCopy.Size = new Size(S(130), S(26)); btnCopy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            btnCopy.Click += delegate { ChatGroup g = Current; if (g != null) { try { Clipboard.SetText(g.Code); } catch { } } };
            strip.Controls.Add(btnCopy);
            strip.Resize += delegate { btnCopy.Location = new Point(strip.Width - btnCopy.Width - S(10), S(8)); };
            presets = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(6)), BackColor = Color.FromArgb(236, 226, 200), WrapContents = true };
            foreach (string id in ChatT.PresetIds)
            {
                string pid = id;
                Button b = Flat(ChatT.Preset(id, id), Palette.Emerald, Palette.Ivory);
                b.AutoSize = true; b.Padding = new Padding(S(8), S(4), S(8), S(4)); b.Margin = new Padding(S(4));
                b.Font = fName; b.Cursor = Cursors.Hand;
                b.Click += delegate { ChatGroup g = Current; if (g != null) Chat.SendPreset(g.Code, pid); };
                presets.Controls.Add(b); presetBtns.Add(b);
            }
            lstMsg = new ListBox { Dock = DockStyle.Fill, DrawMode = DrawMode.OwnerDrawVariable, BorderStyle = BorderStyle.None, BackColor = Palette.Ivory, IntegralHeight = false };
            lstMsg.MeasureItem += MeasureMsg; lstMsg.DrawItem += DrawMsg;
            lstMsg.Resize += delegate { if (lstMsg.ClientSize.Width != lastWidth) { lastWidth = lstMsg.ClientSize.Width; FillMessages(false); } };
            right.Controls.Add(lstMsg); right.Controls.Add(strip); right.Controls.Add(presets);
            lstMsg.BringToFront();

            Controls.Add(right); Controls.Add(left); Controls.Add(header);
            right.BringToFront();
        }

        ChatGroup Current { get { return lstGroups.SelectedIndex >= 0 && lstGroups.SelectedIndex < Chat.Groups.Count ? Chat.Groups[lstGroups.SelectedIndex] : null; } }

        void PickAvatar()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif|*.*|*.*";
                d.Title = ChatT.T("pick");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { Chat.SetAvatar(Avatars.FromFile(d.FileName)); }
                catch (Exception ex) { Store.Log("avatar: " + ex.Message); MessageBox.Show(this, ChatT.T("bad"), Text); }
            }
        }

        bool updating;
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
                RefreshStatus();
                pbAvatar.Invalidate();
                bool can = Chat.Connected && Current != null;
                foreach (Button b in presetBtns) b.Enabled = can;
                btnCreate.Enabled = btnJoin.Enabled = Chat.Connected;
                btnLeave.Enabled = can; btnCopy.Visible = Current != null;
            }
            finally { updating = false; }
        }

        void OnNotice(string text, bool error) { SetStatus(text, error); noticeAt = DateTime.Now; }

        // Событие (ошибка, «ожидание подтверждения») показываем 8 секунд, дальше — состояние соединения
        void RefreshStatus()
        {
            if (IsDisposed) return;
            if (!Chat.Configured) { SetStatus(ChatT.T("setup"), true); return; }
            if ((DateTime.Now - noticeAt).TotalSeconds > 8)
            {
                if (Chat.Connected) SetStatus(ChatT.T("connected"), false);
                else if (!string.IsNullOrEmpty(Chat.LastError)) SetStatus(ChatT.T("connecting") + " " + Chat.LastError, true);
                else SetStatus(ChatT.T("connecting"), false);
            }
        }
        void SetStatus(string text, bool error)
        {
            statusText = text; statusError = error;
            lblStatus.Text = text; lblStatus.ForeColor = error ? Color.FromArgb(255, 170, 140) : Palette.Ivory;
        }

        int shownCount = -1; string shownCode;
        void SelectGroup()
        {
            ChatGroup g = Current;
            Chat.Active = g;
            if (g != null) g.Unread = 0;
            lstGroups.Invalidate(); strip.Invalidate();
            btnCopy.Visible = g != null;
            if (g == null) { lstMsg.Items.Clear(); shownCount = -1; shownCode = null; return; }
            if (shownCode != g.Code || shownCount != g.Messages.Count) FillMessages(true);
        }

        void FillMessages(bool toEnd)
        {
            ChatGroup g = Current;
            if (g == null) return;
            int top = lstMsg.TopIndex;
            lstMsg.BeginUpdate();
            lstMsg.Items.Clear();
            foreach (ChatMsg m in g.Messages) lstMsg.Items.Add(m);
            lstMsg.EndUpdate();
            if (toEnd || shownCode != g.Code) { if (lstMsg.Items.Count > 0) lstMsg.TopIndex = lstMsg.Items.Count - 1; }
            else if (top < lstMsg.Items.Count) lstMsg.TopIndex = top;
            shownCode = g.Code; shownCount = g.Messages.Count;
        }

        void DrawGroup(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Chat.Groups.Count) return;
            ChatGroup g = Chat.Groups[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Graphics gr = e.Graphics;
            using (SolidBrush b = new SolidBrush(sel ? Palette.Emerald : lstGroups.BackColor)) gr.FillRectangle(b, e.Bounds);
            Color fg = sel ? Palette.Ivory : Palette.Ink;
            TextRenderer.DrawText(gr, g.Name, fBold, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(6), e.Bounds.Width - S(50), S(20)), fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(gr, string.Format(ChatT.T("online"), g.OnlineCount) + " / " + g.Members.Count, fSmall,
                new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(25), e.Bounds.Width - S(20), S(16)), sel ? Palette.Gold : Color.FromArgb(120, 100, 60), TextFormatFlags.NoPadding);
            if (g.Unread > 0)
            {
                Rectangle bd = new Rectangle(e.Bounds.Right - S(34), e.Bounds.Y + S(8), S(24), S(20));
                gr.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(Palette.Terracotta)) gr.FillEllipse(b, bd);
                TextRenderer.DrawText(gr, g.Unread > 99 ? "99+" : g.Unread.ToString(), fSmall, bd, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        void PaintStrip(object s, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            ChatGroup grp = Current;
            if (grp == null)
            {
                TextRenderer.DrawText(g, ChatT.T("noGroup"), fName, new Rectangle(S(14), 0, strip.Width - S(28), strip.Height), Palette.GoldDeep, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            TextRenderer.DrawText(g, grp.Name + "   " + string.Format(ChatT.T("code"), grp.Code), fBold, new Point(S(12), S(8)), Palette.Emerald);
            int x = S(12), av = S(34);
            foreach (ChatMember m in grp.Members)
            {
                if (x + av > strip.Width - S(8)) break;
                Rectangle r = new Rectangle(x, S(34), av, av);
                Avatars.Draw(g, r, m.UserId, m.Nick, m.Avatar);
                if (!m.Online) using (SolidBrush dim = new SolidBrush(Color.FromArgb(150, Palette.Ivory))) g.FillEllipse(dim, r);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush dot = new SolidBrush(m.Online ? Palette.Jade : Color.Gray)) g.FillEllipse(dot, r.Right - S(10), r.Bottom - S(10), S(10), S(10));
                x += av + S(8);
            }
        }

        int TextWidth() { return Math.Max(S(120), lstMsg.ClientSize.Width - S(70) - S(16)); }

        void MeasureMsg(object s, MeasureItemEventArgs e)
        {
            ChatMsg m = (ChatMsg)lstMsg.Items[e.Index];
            Size sz = TextRenderer.MeasureText(e.Graphics, m.Display, fText, new Size(TextWidth(), 10000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            e.ItemHeight = Math.Max(S(52), S(26) + sz.Height + S(12));
        }

        void DrawMsg(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lstMsg.Items.Count) return;
            ChatMsg m = (ChatMsg)lstMsg.Items[e.Index];
            ChatGroup g = Current;
            bool mine = m.From == Chat.UserId;
            Graphics gr = e.Graphics;
            gr.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            using (SolidBrush b = new SolidBrush(mine ? Color.FromArgb(226, 240, 230) : Palette.Ivory)) gr.FillRectangle(b, e.Bounds);
            ChatMember who = null;
            if (g != null) foreach (ChatMember x in g.Members) if (x.UserId == m.From) who = x;
            Avatars.Draw(gr, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(8), S(36), S(36)), m.From, m.Nick, who != null ? who.Avatar : (mine ? Chat.Avatar : null));
            int x0 = e.Bounds.X + S(56);
            string nick = mine ? ChatT.T("you") : m.Nick;
            TextRenderer.DrawText(gr, nick, fBold, new Point(x0, e.Bounds.Y + S(6)), mine ? Palette.Emerald : Palette.Lapis);
            Size nw = TextRenderer.MeasureText(gr, nick, fBold);
            TextRenderer.DrawText(gr, m.Time.ToString("HH:mm"), fSmall, new Point(x0 + nw.Width + S(6), e.Bounds.Y + S(9)), Color.FromArgb(140, 120, 80));
            TextRenderer.DrawText(gr, m.Display, fText, new Rectangle(x0, e.Bounds.Y + S(26), TextWidth(), e.Bounds.Height - S(26)), Palette.Ink, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}
