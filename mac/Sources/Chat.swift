// Групповой чат для тех, кто рядом — клиент для macOS. Порт windows/Chat.cs.
// Протокол: ../../docs/protocol.md (WebSocket-реле server/). Сервер — единственное место с логикой.
import AppKit
import SwiftUI
import UniformTypeIdentifiers

// ───────── Модель ─────────
struct ChatMember: Identifiable {
    var userId: String, nick: String, avatar: String?, online: Bool
    var id: String { userId }
}

struct ChatMsg: Identifiable {
    var id: String, code: String, from: String, nick: String, kind: String, preset: String?, text: String, time: Date
    var display: String { kind == "preset" ? ChatT.preset(preset, text) : text }
}

struct ChatGroup: Identifiable {
    var code: String, name: String
    var members: [ChatMember] = []
    var messages: [ChatMsg] = []
    var unread = 0
    var id: String { code }
    var onlineCount: Int { members.filter { $0.online }.count }
}

struct JoinReq { var requestId: String, code: String, groupName: String, from: ChatMember }

// ───────── Тексты (порядок как в Lang: uz-lotin, uz-kirill, ru, en) ─────────
enum ChatT {
    static let t: [String: [String]] = [
        "menu":      ["Namoz do'stlari (chat)…", "Намоз дўстлари (чат)…", "Чат для совместного намаза…", "Prayer group chat…"],
        "title":     ["NamazBar — chat", "NamazBar — чат", "NamazBar — чат", "NamazBar — chat"],
        "nick":      ["Taxallus", "Тахаллус", "Ник", "Nickname"],
        "server":    ["Server manzili (wss://…)", "Сервер манзили (wss://…)", "Адрес сервера (wss://…)", "Server address (wss://…)"],
        "save":      ["Saqlash", "Сақлаш", "Сохранить", "Save"],
        "groups":    ["Guruhlar", "Гуруҳлар", "Группы", "Groups"],
        "create":    ["Yaratish", "Яратиш", "Создать", "Create"],
        "join":      ["Qo'shilish", "Қўшилиш", "Войти", "Join"],
        "leave":     ["Chiqish", "Чиқиш", "Выйти", "Leave"],
        "newName":   ["Guruh nomi (masalan: Ofis)", "Гуруҳ номи (масалан: Офис)", "Название группы (например: Офис)", "Group name (e.g. Office)"],
        "enterCode": ["Guruh kodi", "Гуруҳ коди", "Код группы", "Group code"],
        "connected": ["Ulangan", "Уланган", "Подключено", "Connected"],
        "connecting": ["Ulanmoqda…", "Уланмоқда…", "Подключение…", "Connecting…"],
        "setup":     ["Taxallus va server manzilini kiriting", "Тахаллус ва сервер манзилини киритинг", "Укажите ник и адрес сервера", "Set a nickname and the server address"],
        "reqText":   ["%@ «%@» guruhiga qo'shilmoqchi", "%@ «%@» гуруҳига қўшилмоқчи", "%@ хочет вступить в «%@»", "%@ wants to join “%@”"],
        "allow":     ["Qabul qilish", "Қабул қилиш", "Принять", "Allow"],
        "deny":      ["Rad etish", "Рад этиш", "Отклонить", "Deny"],
        "auto":      ["Tanaffus boshlanganda «Birga o'qiymiz» yuborish", "Танаффус бошланганда «Бирга ўқиймиз» юбориш", "При начале перерыва отправлять «Давайте вместе»", "Send “Let's do namaz together” when the break starts"],
        "sound":     ["Ovoz", "Овоз", "Звук", "Sound"],
        "waiting":   ["Tasdiqlash kutilmoqda: %@", "Тасдиқлаш кутилмоқда: %@", "Ожидание подтверждения: %@", "Waiting for approval: %@"],
        "denied":    ["So'rov rad etildi", "Сўров рад этилди", "Запрос отклонён", "Request declined"],
        "joined":    ["Siz «%@» guruhidasiz", "Сиз «%@» гуруҳидасиз", "Вы в группе «%@»", "You are in “%@”"],
        "code":      ["Kod: %@", "Код: %@", "Код: %@", "Code: %@"],
        "copy":      ["Kodni nusxalash", "Кодни нусхалаш", "Копировать код", "Copy code"],
        "noGroup":   ["Guruh yarating yoki kod bilan qo'shiling", "Гуруҳ яратинг ёки код билан қўшилинг", "Создайте группу или войдите по коду", "Create a group or join with a code"],
        "online":    ["%d onlayn", "%d онлайн", "%d онлайн", "%d online"],
        "pick":      ["Rasm tanlash", "Расм танлаш", "Выбрать фото", "Choose picture"],
        "you":       ["Siz", "Сиз", "Вы", "You"],
        "send":      ["Xabar yuborish", "Хабар юбориш", "Отправить сообщение", "Send a message"],
        "openWin":   ["Chat oynasini ochish…", "Чат ойнасини очиш…", "Открыть окно чата…", "Open chat window…"],
        "ok":        ["OK", "OK", "OK", "OK"],
        "cancel":    ["Bekor qilish", "Бекор қилиш", "Отмена", "Cancel"],
    ]
    static func T(_ k: String) -> String { t[k]?[Lang.cur] ?? k }

    static let presetIds = ["together", "coming", "wait", "where", "ready", "done"]
    static let presets: [String: [String]] = [
        "together": ["Birga namoz o'qiymiz", "Бирга намоз ўқиймиз", "Давайте совершим намаз вместе", "Let's do namaz together"],
        "coming":   ["Namoz uchun oldingizga kelyapman", "Намоз учун олдингизга келяпман", "Иду к вам совершить намаз", "I'm coming to you to perform namaz"],
        "wait":     ["Meni 5 daqiqa kuting", "Мени 5 дақиқа кутинг", "Подождите меня 5 минут", "Wait for me, 5 minutes"],
        "where":    ["Qayerda o'qiymiz?", "Қаерда ўқиймиз?", "Где совершаем намаз?", "Where are we praying?"],
        "ready":    ["Men tayyorman", "Мен тайёрман", "Я готов", "I'm ready"],
        "done":     ["Men o'qib bo'ldim", "Мен ўқиб бўлдим", "Я уже совершил намаз", "I have already prayed"],
    ]
    static func preset(_ id: String?, _ fallback: String) -> String {
        guard let id = id, let v = presets[id] else { return fallback }
        return v[Lang.cur]
    }
}

// ───────── Сервис чата: WebSocket с переподключением + состояние ─────────
final class ChatHub: ObservableObject {
    static let shared = ChatHub()

    @Published var groups: [ChatGroup] = []
    @Published var connected = false
    @Published var status = ""
    @Published var statusError = false
    @Published var nick = ""
    @Published var server = ""
    @Published var avatar: String?
    var windowVisible = false
    var activeCode: String?

    var onIncoming: ((ChatGroup, ChatMsg) -> Void)?
    var onJoinRequest: ((JoinReq) -> Void)?
    var onSettled: ((String) -> Void)?
    var onNotice: ((String, Bool) -> Void)?

    private(set) var userId = ""
    private var task: URLSessionWebSocketTask?
    private var generation = 0
    private var backoff = 1.0
    private var pingTimer: Timer?
    private var tokens: [String: String] = [:], names: [String: String] = [:]
    private var autoSent: [String: Date] = [:]
    private var lastLine: String?, lastLineAt = Date.distantPast

    var configured: Bool { !server.isEmpty && !nick.isEmpty }
    private var avatarFile: URL { Store.dir.appendingPathComponent("avatar.txt") }

    func start() {
        server = Self.normalizeServer(Store.get("chatServer", ""))
        nick = Store.get("chatNick", "").trimmingCharacters(in: .whitespaces)
        userId = Store.get("chatUserId", "")
        if userId.count < 16 {
            userId = (UUID().uuidString + UUID().uuidString).replacingOccurrences(of: "-", with: "")
            Store.set("chatUserId", userId)
        }
        avatar = (try? String(contentsOf: avatarFile, encoding: .utf8))?.trimmingCharacters(in: .whitespacesAndNewlines)
        if avatar?.isEmpty == true { avatar = nil }
        tokens = [:]; names = [:]
        for part in Store.get("chatGroups", "").split(separator: ";") {
            let p = part.split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            if p.count == 3 { tokens[p[0]] = p[2]; names[p[0]] = p[1].removingPercentEncoding ?? p[1] }
        }
        connect()
    }

    /// «example.onrender.com», «https://…» и «wss://…» без пути приводим к рабочему wss://…/ws
    static func normalizeServer(_ raw: String) -> String {
        var s = raw.trimmingCharacters(in: .whitespaces)
        if s.isEmpty { return s }
        let lower = s.lowercased()
        if lower.hasPrefix("https://") { s = "wss://" + s.dropFirst(8) }
        else if lower.hasPrefix("http://") { s = "ws://" + s.dropFirst(7) }
        else if !s.contains("://") { s = "wss://" + s }
        if let u = URL(string: s), (u.path.isEmpty || u.path == "/"), u.query == nil {
            while s.hasSuffix("/") { s.removeLast() }
            s += "/ws"
        }
        return s
    }

    func configure(server: String, nick: String) {
        let s = Self.normalizeServer(server), n = nick.trimmingCharacters(in: .whitespaces)
        let changed = s != self.server
        Store.set("chatServer", s); Store.set("chatNick", n)
        self.server = s; self.nick = n
        if changed || task == nil { connect() } else { send(["type": "updateProfile", "nick": n, "avatar": json(avatar)]) }
    }

    func setAvatar(_ dataUrl: String?) {
        avatar = dataUrl
        try? (dataUrl ?? "").write(to: avatarFile, atomically: true, encoding: .utf8)
        if connected { send(["type": "updateProfile", "nick": nick, "avatar": json(dataUrl)]) }
    }

    private func json(_ s: String?) -> Any { if let s = s { return s } else { return NSNull() } }

    // ---- соединение ----
    private func connect() {
        disconnect()
        guard configured else { setStatus(ChatT.T("setup"), true, notify: false); return }
        guard let url = URL(string: server), url.scheme == "ws" || url.scheme == "wss" else {
            setStatus("Server URL must start with ws:// or wss://", true); return
        }
        generation += 1
        let gen = generation
        setStatus(ChatT.T("connecting"), false, notify: false)
        let t = URLSession.shared.webSocketTask(with: url)
        t.maximumMessageSize = 4 * 1024 * 1024   // аватары всей группы приходят в одном welcome
        task = t
        t.resume()
        var groupsMsg: [[String: Any]] = []
        for (code, token) in tokens { groupsMsg.append(["code": code, "name": names[code] ?? code, "token": token]) }
        rawSend(t, ["type": "hello", "userId": userId, "nick": nick, "avatar": json(avatar), "groups": groupsMsg])
        receive(t, gen)
        pingTimer?.invalidate()
        pingTimer = Timer.scheduledTimer(withTimeInterval: 25, repeats: true) { [weak self] _ in self?.send(["type": "ping"]) }
    }

    private func disconnect() {
        generation += 1
        pingTimer?.invalidate(); pingTimer = nil
        task?.cancel(with: .goingAway, reason: nil); task = nil
        connected = false
    }

    private func receive(_ t: URLSessionWebSocketTask, _ gen: Int) {
        t.receive { [weak self] result in
            DispatchQueue.main.async {
                guard let self = self, gen == self.generation else { return }
                switch result {
                case .failure(let err):
                    self.connected = false
                    let code = (t.response as? HTTPURLResponse)?.statusCode
                    self.scheduleReconnect(gen, why: Self.friendly(err, code))
                case .success(let msg):
                    var text: String?
                    switch msg {
                    case .string(let s): text = s
                    case .data(let d): text = String(data: d, encoding: .utf8)
                    @unknown default: break
                    }
                    if let text = text, let data = text.data(using: .utf8),
                       let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { self.handle(obj) }
                    self.receive(t, gen)
                }
            }
        }
    }

    // бесплатные хостинги «засыпают» и просыпаются до минуты — экспоненциальная пауза до 30 с
    /// Причина, понятная пользователю (что проверить), вместо «Подключение…» без конца
    static func friendly(_ err: Error, _ http: Int?) -> String {
        if let c = http {
            switch c {
            case 404: return "nothing answers at this address (404) - copy the exact URL from your hosting dashboard"
            case 400: return "the server answered but not at this path (400) - the address must end with /ws"
            case 401, 403: return "access denied (\(c))"
            case 502, 503, 504: return "the server is starting up (free hosting sleeps) - retrying"
            default: return "server answered HTTP \(c)"
            }
        }
        let ns = err as NSError
        if ns.domain == NSURLErrorDomain {
            switch ns.code {
            case NSURLErrorTimedOut: return "no answer yet - free hosting can need up to a minute to wake up, retrying"
            case NSURLErrorCannotFindHost, NSURLErrorDNSLookupFailed: return "address not found (DNS) - check the server address"
            case NSURLErrorNotConnectedToInternet: return "no internet connection"
            case NSURLErrorSecureConnectionFailed, NSURLErrorServerCertificateUntrusted: return "secure connection failed (TLS)"
            default: break
            }
        }
        return ns.localizedDescription
    }

    private func scheduleReconnect(_ gen: Int, why: String) {
        let delay = backoff
        backoff = min(backoff * 2, 30)
        setStatus(ChatT.T("connecting") + " " + why, true, notify: false)
        DispatchQueue.main.asyncAfter(deadline: .now() + delay) { [weak self] in
            guard let self = self, gen == self.generation else { return }
            self.connect()
        }
    }

    private func rawSend(_ t: URLSessionWebSocketTask, _ obj: [String: Any]) {
        guard let d = try? JSONSerialization.data(withJSONObject: obj), let s = String(data: d, encoding: .utf8) else { return }
        t.send(.string(s)) { _ in }
    }
    private func send(_ obj: [String: Any]) { if let t = task { rawSend(t, obj) } }

    // ---- команды ----
    func createGroup(_ name: String) { send(["type": "createGroup", "name": name]) }
    func requestJoin(_ code: String) { send(["type": "joinRequest", "code": code.trimmingCharacters(in: .whitespaces).uppercased()]) }
    func decide(_ requestId: String, _ approve: Bool) { send(["type": "decide", "requestId": requestId, "approve": approve]) }
    func leave(_ code: String) { send(["type": "leave", "code": code]) }
    func sendPreset(_ code: String, _ preset: String) { send(["type": "send", "code": code, "kind": "preset", "preset": preset]) }

    /// Перерыв на намаз начался (экран блокируется): позвать группу, если включено
    func onBreakStart() {
        guard connected, Store.get("chatAuto", "0") == "1" else { return }
        for g in groups {
            if let last = autoSent[g.code], Date().timeIntervalSince(last) < 600 { continue }
            autoSent[g.code] = Date()
            sendPreset(g.code, "together")
        }
    }

    /// Последняя входящая реплика для экрана перерыва: живёт 10 минут
    func recentLine() -> String? {
        guard let l = lastLine, Date().timeIntervalSince(lastLineAt) < 600 else { return nil }
        return l
    }

    func markRead(_ code: String) {
        if let i = groups.firstIndex(where: { $0.code == code }), groups[i].unread != 0 { groups[i].unread = 0 }
    }

    // ---- входящие ----
    private func setStatus(_ text: String, _ error: Bool, notify: Bool = true) { status = text; statusError = error; if notify { onNotice?(text, error) } }

    private func member(_ m: [String: Any]) -> ChatMember {
        ChatMember(userId: m["userId"] as? String ?? "", nick: m["nick"] as? String ?? "?", avatar: m["avatar"] as? String, online: m["online"] as? Bool ?? false)
    }

    private func message(_ m: [String: Any], defaultCode: String? = nil) -> ChatMsg {
        let ts = (m["ts"] as? NSNumber)?.doubleValue ?? 0
        return ChatMsg(id: m["id"] as? String ?? UUID().uuidString, code: m["code"] as? String ?? defaultCode ?? "",
                       from: m["from"] as? String ?? "", nick: m["nick"] as? String ?? "?", kind: m["kind"] as? String ?? "text",
                       preset: m["preset"] as? String, text: m["text"] as? String ?? "",
                       time: ts > 0 ? Date(timeIntervalSince1970: ts / 1000) : Date())
    }

    private func apply(_ g: [String: Any]) -> String {
        let code = g["code"] as? String ?? ""
        let name = g["name"] as? String ?? code
        names[code] = name
        var grp = groups.first(where: { $0.code == code }) ?? ChatGroup(code: code, name: name)
        grp.name = name
        grp.members = (g["members"] as? [[String: Any]] ?? []).map { member($0) }
        if grp.messages.isEmpty { grp.messages = (g["history"] as? [[String: Any]] ?? []).map { message($0, defaultCode: code) } }
        if let i = groups.firstIndex(where: { $0.code == code }) { groups[i] = grp } else { groups.append(grp) }
        return code
    }

    private func saveGroups() {
        let s = tokens.map { code, token in
            code + "|" + ((names[code] ?? code).addingPercentEncoding(withAllowedCharacters: .alphanumerics) ?? code) + "|" + token
        }.joined(separator: ";")
        Store.set("chatGroups", s)
    }

    private func handle(_ d: [String: Any]) {
        switch d["type"] as? String ?? "" {
        case "welcome":
            connected = true; backoff = 1
            setStatus(ChatT.T("connected"), false, notify: false)
            let keep = (d["groups"] as? [[String: Any]] ?? []).map { apply($0) }
            groups.removeAll { !keep.contains($0.code) }
            for c in Array(tokens.keys) where !keep.contains(c) { tokens[c] = nil; names[c] = nil }
            saveGroups()
        case "groupCreated", "joined":
            guard let g = d["group"] as? [String: Any] else { break }
            let code = apply(g)
            tokens[code] = d["token"] as? String ?? ""
            saveGroups()
            let name = g["name"] as? String ?? code
            setStatus(String(format: ChatT.T("joined"), name) + "  —  " + String(format: ChatT.T("code"), code), false)
        case "members":
            if let code = d["code"] as? String, let i = groups.firstIndex(where: { $0.code == code }) {
                groups[i].members = (d["members"] as? [[String: Any]] ?? []).map { member($0) }
            }
        case "message":
            let m = message(d)
            guard let i = groups.firstIndex(where: { $0.code == m.code }) else { break }
            groups[i].messages.append(m)
            if groups[i].messages.count > 200 { groups[i].messages.removeFirst() }
            if m.from != userId {
                lastLine = "\(m.nick) · \(groups[i].name): \(m.display)"; lastLineAt = Date()
                let watching = windowVisible && activeCode == m.code
                if !watching { groups[i].unread += 1 }
                onIncoming?(groups[i], m)
            }
        case "joinRequest":
            guard let f = d["from"] as? [String: Any] else { break }
            let code = d["code"] as? String ?? ""
            let gname = groups.first(where: { $0.code == code })?.name ?? code
            onJoinRequest?(JoinReq(requestId: d["requestId"] as? String ?? "", code: code, groupName: gname, from: member(f)))
        case "joinSettled": onSettled?(d["requestId"] as? String ?? "")
        case "joinPending": setStatus(String(format: ChatT.T("waiting"), d["name"] as? String ?? ""), false)
        case "joinDenied": setStatus(ChatT.T("denied"), true)
        case "left":
            if let code = d["code"] as? String {
                groups.removeAll { $0.code == code }; tokens[code] = nil; names[code] = nil; saveGroups()
                if activeCode == code { activeCode = nil }
            }
        case "error": setStatus(d["message"] as? String ?? "error", true)
        default: break
        }
    }
}

// ───────── Аватары ─────────
enum Avatars {
    private static var cache: [String: NSImage] = [:]
    static let tints: [NSColor] = [Palette.emerald, Palette.lapis, Palette.goldDeep, Palette.terracotta, Palette.saffron, Palette.rgb(90, 70, 130)]

    static func image(_ dataUrl: String?) -> NSImage? {
        guard let u = dataUrl, let comma = u.firstIndex(of: ",") else { return nil }
        if let c = cache[u] { return c }
        guard let data = Data(base64Encoded: String(u[u.index(after: comma)...])), let img = NSImage(data: data) else { return nil }
        if cache.count > 200 { cache.removeAll() }
        cache[u] = img
        return img
    }

    static func tint(_ userId: String) -> Color {
        var h = 0
        for c in userId.unicodeScalars { h = (h &* 31 &+ Int(c.value)) & 0x7fffffff }
        return Color(nsColor: tints[h % tints.count])
    }

    /// Выбранная картинка -> квадрат 96×96 JPEG в data URL (лимит сервера 32 КБ)
    static func fromFile(_ url: URL) -> String? {
        guard let src = NSImage(contentsOf: url), let rep = src.representations.first else { return nil }
        let w = CGFloat(rep.pixelsWide > 0 ? rep.pixelsWide : Int(src.size.width)), h = CGFloat(rep.pixelsHigh > 0 ? rep.pixelsHigh : Int(src.size.height))
        let side = min(w, h)
        let crop = CGRect(x: (w - side) / 2, y: (h - side) / 2, width: side, height: side)
        for size in [96, 64] {
            let n = CGFloat(size)
            guard let bmp = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
                                             hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
            NSGraphicsContext.saveGraphicsState()
            NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: bmp)
            NSColor.white.setFill(); NSBezierPath(rect: CGRect(x: 0, y: 0, width: n, height: n)).fill()
            // NSImage.size в пунктах — переводим обрезку из пикселей
            let k = src.size.width / w
            src.draw(in: CGRect(x: 0, y: 0, width: n, height: n),
                     from: CGRect(x: crop.minX * k, y: crop.minY * k, width: crop.width * k, height: crop.height * k),
                     operation: .copy, fraction: 1)
            NSGraphicsContext.restoreGraphicsState()
            if let jpg = bmp.representation(using: .jpeg, properties: [.compressionFactor: 0.85]) {
                let s = "data:image/jpeg;base64," + jpg.base64EncodedString()
                if s.count < 30000 { return s }
            }
        }
        return nil
    }
}

struct AvatarView: View {
    let userId: String, nick: String, avatar: String?
    var size: CGFloat = 36
    var body: some View {
        Group {
            if let img = Avatars.image(avatar) {
                Image(nsImage: img).resizable().scaledToFill()
            } else {
                ZStack {
                    Avatars.tint(userId)
                    Text(String(nick.prefix(1)).uppercased()).font(.system(size: size * 0.42, weight: .bold)).foregroundColor(.white)
                }
            }
        }
        .frame(width: size, height: size).clipShape(Circle())
        .overlay(Circle().stroke(Color.black.opacity(0.25), lineWidth: 1))
    }
}

// ───────── Чат прямо в меню ─────────
/// Строка участника: круглый аватар, ник, справа точка (зелёная — онлайн, серая — нет)
struct MemberRow: View {
    let member: ChatMember
    var body: some View {
        HStack(spacing: 10) {
            AvatarView(userId: member.userId, nick: member.nick, avatar: member.avatar, size: 22)
                .opacity(member.online ? 1 : 0.45)
            Text(member.nick).font(.system(size: 13)).foregroundColor(.primary).opacity(member.online ? 1 : 0.5).lineLimit(1)
            Spacer(minLength: 6)
            Circle().fill(member.online ? Color.green : Color.gray).frame(width: 9, height: 9)
        }
        .padding(.horizontal, 14)
        .frame(width: 250, height: 30)
    }
}

extension AppDelegate {
    /// Подменю «Чат»: группы с участниками (онлайн сверху, офлайн снизу), быстрые фразы, управление
    func fillChatMenu(_ m: NSMenu) {
        m.removeAllItems()
        let hub = ChatHub.shared
        if !hub.configured {
            m.addItem(item(ChatT.T("setup"), #selector(openChat)))
            return
        }
        if !hub.connected {
            let st = NSMenuItem(title: ChatT.T("connecting"), action: nil, keyEquivalent: ""); st.isEnabled = false
            m.addItem(st)
        }
        for g in hub.groups {
            let head = NSMenuItem(title: "", action: nil, keyEquivalent: "")
            head.attributedTitle = NSAttributedString(string: "\(g.name)  ·  \(g.onlineCount)/\(g.members.count)",
                                                      attributes: [.font: NSFont.boldSystemFont(ofSize: 12), .foregroundColor: NSColor.secondaryLabelColor])
            head.isEnabled = false
            m.addItem(head)
            let sorted = g.members.sorted { a, b in
                a.online != b.online ? a.online : a.nick.localizedCaseInsensitiveCompare(b.nick) == .orderedAscending
            }
            for mem in sorted {
                let it = NSMenuItem()
                let host = NSHostingView(rootView: MemberRow(member: mem))
                host.frame = NSRect(x: 0, y: 0, width: 250, height: 30)
                it.view = host
                m.addItem(it)
            }
        }
        if !hub.groups.isEmpty { m.addItem(.separator()) }
        if hub.connected {
            for g in hub.groups {
                let title = hub.groups.count > 1 ? ChatT.T("send") + " → " + g.name : ChatT.T("send")
                let send = NSMenuItem(title: title, action: nil, keyEquivalent: ""); send.submenu = NSMenu()
                for id in ChatT.presetIds {
                    send.submenu!.addItem(item(ChatT.preset(id, id), #selector(sendPresetFromMenu(_:)), false, [g.code, id]))
                }
                m.addItem(send)
            }
            m.addItem(item(ChatT.T("create") + "…", #selector(chatCreateGroup)))
            m.addItem(item(ChatT.T("join") + "…", #selector(chatJoinGroup)))
            m.addItem(.separator())
        }
        m.addItem(item(ChatT.T("openWin"), #selector(openChat)))
    }

    @objc func sendPresetFromMenu(_ s: NSMenuItem) {
        if let a = s.representedObject as? [String], a.count == 2 { ChatHub.shared.sendPreset(a[0], a[1]) }
    }
    @objc func chatCreateGroup() { if let n = promptText(ChatT.T("create"), ChatT.T("newName")) { ChatHub.shared.createGroup(n) } }
    @objc func chatJoinGroup() { if let c = promptText(ChatT.T("join"), ChatT.T("enterCode")) { ChatHub.shared.requestJoin(c) } }

    func promptText(_ title: String, _ label: String) -> String? {
        let a = NSAlert()
        a.messageText = title; a.informativeText = label
        let tf = NSTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        a.accessoryView = tf
        a.addButton(withTitle: ChatT.T("ok")); a.addButton(withTitle: ChatT.T("cancel"))
        NSApp.activate(ignoringOtherApps: true)
        a.window.initialFirstResponder = tf
        guard a.runModal() == .alertFirstButtonReturn else { return nil }
        let v = tf.stringValue.trimmingCharacters(in: .whitespaces)
        return v.isEmpty ? nil : v
    }
}

// ───────── Окно чата ─────────
struct PromptSheet: View {
    let title: String, label: String
    var onDone: (String?) -> Void
    @State private var text = ""
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text(title).font(.headline)
            Text(label).font(.subheadline)
            TextField("", text: $text).textFieldStyle(.roundedBorder).frame(width: 300).onSubmit { onDone(text) }
            HStack {
                Spacer()
                Button(ChatT.T("cancel")) { onDone(nil) }.keyboardShortcut(.cancelAction)
                Button(ChatT.T("ok")) { onDone(text) }.keyboardShortcut(.defaultAction)
            }
        }
        .padding(18)
    }
}

enum ChatPrompt: Identifiable {
    case create, join
    var id: Int { self == .create ? 0 : 1 }
}

struct ChatView: View {
    @ObservedObject var hub: ChatHub
    @State private var selected: String?
    @State private var nickField = ""
    @State private var serverField = ""
    @State private var prompt: ChatPrompt?
    @State private var auto = Store.get("chatAuto", "0") == "1"
    @State private var sound = Store.get("chatSound", "1") != "0"

    var current: ChatGroup? { hub.groups.first { $0.code == selected } }

    var body: some View {
        VStack(spacing: 0) {
            header
            HStack(spacing: 0) {
                sidebar.frame(width: 220)
                Divider()
                main
            }
        }
        .frame(minWidth: 700, minHeight: 480)
        .background(Color.pIvory)
        .onAppear {
            nickField = hub.nick; serverField = hub.server
            if selected == nil { selected = hub.groups.first?.code }
            hub.activeCode = selected
        }
        .onChange(of: selected) { c in hub.activeCode = c; if let c = c { hub.markRead(c) } }
        .onChange(of: hub.groups.count) { _ in if current == nil { selected = hub.groups.first?.code } }
        .sheet(item: $prompt) { p in
            PromptSheet(title: ChatT.T(p == .create ? "create" : "join"), label: ChatT.T(p == .create ? "newName" : "enterCode")) { value in
                prompt = nil
                guard let v = value?.trimmingCharacters(in: .whitespaces), !v.isEmpty else { return }
                if p == .create { hub.createGroup(v) } else { hub.requestJoin(v) }
            }
        }
    }

    var header: some View {
        HStack(alignment: .center, spacing: 14) {
            Button(action: pickAvatar) { AvatarView(userId: hub.userId, nick: hub.nick, avatar: hub.avatar, size: 60) }
                .buttonStyle(.plain).help(ChatT.T("pick"))
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .bottom, spacing: 10) {
                    field(ChatT.T("nick"), $nickField, width: 170)
                    field(ChatT.T("server"), $serverField, width: 300)
                    Button(ChatT.T("save")) { hub.configure(server: serverField, nick: nickField); serverField = hub.server }
                        .keyboardShortcut(.defaultAction)
                }
                Text(hub.configured ? hub.status : ChatT.T("setup"))
                    .font(Font.ns(Palette.sans(11)))
                    .foregroundColor(hub.statusError || !hub.configured ? Color(nsColor: Palette.rgb(255, 170, 140)) : .pIvory)
            }
            Spacer()
        }
        .padding(14)
        .background(Color.pEmeraldDark)
    }

    func field(_ title: String, _ binding: Binding<String>, width: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(Font.ns(Palette.sans(10))).foregroundColor(.pGold)
            TextField("", text: binding).textFieldStyle(.roundedBorder).frame(width: width)
        }
    }

    var sidebar: some View {
        VStack(spacing: 8) {
            List(selection: $selected) {
                ForEach(hub.groups) { g in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text(g.name).font(Font.ns(Palette.sans(13, weight: .bold))).lineLimit(1)
                            Text(String(format: ChatT.T("online"), g.onlineCount) + " / \(g.members.count)").font(Font.ns(Palette.sans(11))).opacity(0.7)
                        }
                        Spacer()
                        if g.unread > 0 {
                            Text(g.unread > 99 ? "99+" : "\(g.unread)").font(.caption.bold()).foregroundColor(.white)
                                .padding(.horizontal, 7).padding(.vertical, 2).background(Capsule().fill(Color(nsColor: Palette.terracotta)))
                        }
                    }
                    .tag(g.code)
                }
            }
            .listStyle(.sidebar)
            HStack(spacing: 6) {
                Button(ChatT.T("create")) { prompt = .create }.disabled(!hub.connected)
                Button(ChatT.T("join")) { prompt = .join }.disabled(!hub.connected)
                Button(ChatT.T("leave")) { if let c = current { hub.leave(c.code) } }.disabled(current == nil || !hub.connected)
            }
            Toggle(ChatT.T("auto"), isOn: $auto).font(Font.ns(Palette.sans(11)))
                .onChange(of: auto) { Store.set("chatAuto", $0 ? "1" : "0") }
            Toggle(ChatT.T("sound"), isOn: $sound).font(Font.ns(Palette.sans(11)))
                .onChange(of: sound) { Store.set("chatSound", $0 ? "1" : "0") }
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(8)
        .background(Color(nsColor: Palette.rgb(236, 226, 200)))
    }

    var main: some View {
        VStack(spacing: 0) {
            groupStrip
            Divider()
            messages
            Divider()
            presets
        }
    }

    var groupStrip: some View {
        VStack(alignment: .leading, spacing: 6) {
            if let g = current {
                HStack {
                    Text(g.name + "   " + String(format: ChatT.T("code"), g.code)).font(Font.ns(Palette.sans(14, weight: .bold)))
                        .foregroundColor(.pEmerald)
                    Spacer()
                    Button(ChatT.T("copy")) {
                        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(g.code, forType: .string)
                    }
                }
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 8) {
                        ForEach(g.members) { m in
                            AvatarView(userId: m.userId, nick: m.nick, avatar: m.avatar, size: 34)
                                .opacity(m.online ? 1 : 0.4)
                                .overlay(Circle().fill(m.online ? Color.pJade : Color.gray).frame(width: 10, height: 10)
                                            .offset(x: 12, y: 12))
                                .help(m.nick)
                        }
                    }
                }
            } else {
                Text(ChatT.T("noGroup")).font(Font.ns(Palette.sans(13, weight: .medium))).foregroundColor(Color(nsColor: Palette.goldDeep))
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .frame(height: 92)
        .background(Color.pIvory)
    }

    var messages: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    ForEach(current?.messages ?? []) { m in
                        let mine = m.from == hub.userId
                        let who = current?.members.first { $0.userId == m.from }
                        HStack(alignment: .top, spacing: 10) {
                            AvatarView(userId: m.from, nick: m.nick, avatar: who?.avatar ?? (mine ? hub.avatar : nil), size: 36)
                            VStack(alignment: .leading, spacing: 2) {
                                HStack(spacing: 6) {
                                    Text(mine ? ChatT.T("you") : m.nick).font(Font.ns(Palette.sans(13, weight: .bold)))
                                        .foregroundColor(mine ? .pEmerald : Color(nsColor: Palette.lapis))
                                    Text(Self.hm.string(from: m.time)).font(Font.ns(Palette.sans(11))).foregroundColor(.gray)
                                }
                                Text(m.display).font(Font.ns(Palette.sans(13))).foregroundColor(Color(nsColor: Palette.ink))
                                    .fixedSize(horizontal: false, vertical: true)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 12).padding(.vertical, 8)
                        .background(mine ? Color(nsColor: Palette.rgb(226, 240, 230)) : Color.clear)
                        .id(m.id)
                    }
                }
            }
            .onChange(of: current?.messages.count ?? 0) { _ in
                if let last = current?.messages.last { withAnimation { proxy.scrollTo(last.id, anchor: .bottom) } }
            }
            .onChange(of: selected) { _ in
                if let last = current?.messages.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    var presets: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 190), spacing: 8)], spacing: 8) {
            ForEach(ChatT.presetIds, id: \.self) { id in
                Button(action: { if let c = current { hub.sendPreset(c.code, id) } }) {
                    Text(ChatT.preset(id, id)).font(Font.ns(Palette.sans(12.5, weight: .medium))).foregroundColor(.pIvory)
                        .padding(.horizontal, 10).padding(.vertical, 7).frame(maxWidth: .infinity)
                        .background(RoundedRectangle(cornerRadius: 6).fill(Color.pEmerald.opacity((current != nil && hub.connected) ? 1 : 0.4)))
                }
                .buttonStyle(.plain).disabled(current == nil || !hub.connected)
            }
        }
        .padding(10)
        .background(Color(nsColor: Palette.rgb(236, 226, 200)))
    }

    static let hm: DateFormatter = { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f }()

    func pickAvatar() {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.image]; p.allowsMultipleSelection = false; p.message = ChatT.T("pick")
        if p.runModal() == .OK, let url = p.url { hub.setAvatar(Avatars.fromFile(url)) }
    }
}

final class ChatWindow: NSWindow, NSWindowDelegate {
    static var shared: ChatWindow?

    static func show() {
        if let w = shared {
            w.makeKeyAndOrderFront(nil); NSApp.activate(ignoringOtherApps: true); return
        }
        let w = ChatWindow(contentRect: NSRect(x: 0, y: 0, width: 860, height: 600),
                           styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.title = ChatT.T("title")
        w.contentView = NSHostingView(rootView: ChatView(hub: ChatHub.shared))
        w.delegate = w
        w.isReleasedWhenClosed = false
        w.center()
        shared = w
        ChatHub.shared.windowVisible = true
        w.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }
    /// Перерисовать на новом языке
    static func reopen() {
        guard let w = shared else { return }
        w.close(); shared = nil; show()
    }

    func windowDidBecomeKey(_ n: Notification) { ChatHub.shared.windowVisible = true; if let c = ChatHub.shared.activeCode { ChatHub.shared.markRead(c) } }
    func windowDidResignKey(_ n: Notification) { ChatHub.shared.windowVisible = false }
    func windowWillClose(_ n: Notification) {
        ChatHub.shared.windowVisible = false; ChatHub.shared.activeCode = nil
        ChatWindow.shared = nil
    }
}

// ───────── Всплывающие уведомления (сообщение / запрос на вступление) ─────────
struct ToastView: View {
    let member: ChatMember?, line1: String, line2: String
    var onAllow: (() -> Void)?, onDeny: (() -> Void)?
    var onClick: () -> Void = {}
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .top, spacing: 10) {
                if let m = member { AvatarView(userId: m.userId, nick: m.nick, avatar: m.avatar, size: 44) }
                VStack(alignment: .leading, spacing: 3) {
                    Text(line1).font(Font.ns(Palette.sans(13, weight: .bold))).foregroundColor(.pGold).lineLimit(2)
                    if !line2.isEmpty { Text(line2).font(Font.ns(Palette.sans(13))).foregroundColor(.pIvory).lineLimit(3) }
                }
                Spacer(minLength: 0)
            }
            if let allow = onAllow, let deny = onDeny {
                HStack(spacing: 8) {
                    Button(action: allow) { Text(ChatT.T("allow")).font(Font.ns(Palette.sans(12, weight: .medium))).foregroundColor(Color(nsColor: Palette.ink))
                        .padding(.horizontal, 14).padding(.vertical, 6).background(Capsule().fill(Color.pGold)) }.buttonStyle(.plain)
                    Button(action: deny) { Text(ChatT.T("deny")).font(Font.ns(Palette.sans(12, weight: .medium))).foregroundColor(.pIvory)
                        .padding(.horizontal, 14).padding(.vertical, 6).background(Capsule().fill(Color.pEmerald)) }.buttonStyle(.plain)
                }
            }
        }
        .padding(14).frame(width: 340, alignment: .leading)
        .background(Color.pEmeraldDark)
        .clipShape(RoundedRectangle(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).stroke(Color.pGold.opacity(0.85), lineWidth: 1.4))
        .onTapGesture { if onAllow == nil { onClick() } }
    }
}

final class ChatToast: NSPanel {
    static var open: [ChatToast] = []
    var requestId: String?
    private var timer: Timer?

    static func present(_ t: ChatToast, seconds: Double) {
        open.append(t); restack()
        t.alphaValue = 0; t.orderFrontRegardless()
        NSAnimationContext.runAnimationGroup { $0.duration = 0.2; t.animator().alphaValue = 1 }
        if seconds > 0 { t.timer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { [weak t] _ in t?.dismiss() } }
    }
    static func restack() {
        guard let scr = NSScreen.main?.visibleFrame else { return }
        var y = scr.minY + 12
        for t in open { t.setFrameOrigin(CGPoint(x: scr.maxX - t.frame.width - 14, y: y)); y += t.frame.height + 8 }
    }

    init(_ view: ToastView) {
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false; backgroundColor = .clear; hasShadow = true
        level = .statusBar; collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let host = NSHostingView(rootView: view)
        host.frame.size = host.fittingSize
        contentView = host
        setContentSize(host.fittingSize)
    }

    func dismiss() {
        timer?.invalidate(); timer = nil
        ChatToast.open.removeAll { $0 === self }
        ChatToast.restack()
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.2; animator().alphaValue = 0 }, completionHandler: { self.orderOut(nil) })
    }

    static func message(_ g: ChatGroup, _ m: ChatMsg) {
        let who = g.members.first { $0.userId == m.from } ?? ChatMember(userId: m.from, nick: m.nick, avatar: nil, online: true)
        var toast: ChatToast!
        toast = ChatToast(ToastView(member: who, line1: "\(m.nick) · \(g.name)", line2: m.display,
                                    onClick: { ChatWindow.show(); toast.dismiss() }))
        present(toast, seconds: 10)
    }

    static func request(_ r: JoinReq) {
        var toast: ChatToast!
        let line = String(format: ChatT.T("reqText"), r.from.nick, r.groupName)
        toast = ChatToast(ToastView(member: r.from, line1: line, line2: "",
                                    onAllow: { ChatHub.shared.decide(r.requestId, true); toast.dismiss() },
                                    onDeny: { ChatHub.shared.decide(r.requestId, false); toast.dismiss() }))
        toast.requestId = r.requestId
        present(toast, seconds: 0)
    }

    static func info(_ text: String) {
        let toast = ChatToast(ToastView(member: nil, line1: "NamazBar", line2: text))
        present(toast, seconds: 6)
    }

    static func settled(_ requestId: String) {
        for t in open where t.requestId == requestId { t.dismiss() }
    }
}

/// Связка с приложением: уведомления о сообщениях и запросах на вступление
enum ChatHooks {
    static func install() {
        let hub = ChatHub.shared
        hub.onIncoming = { g, m in
            if !(hub.windowVisible && hub.activeCode == g.code) { ChatToast.message(g, m) }
            beep()
        }
        hub.onJoinRequest = { r in ChatToast.request(r); beep() }
        hub.onSettled = { id in ChatToast.settled(id) }
        hub.onNotice = { text, _ in if ChatWindow.shared == nil { ChatToast.info(text) } }
        hub.start()
    }
    static func beep() { if Store.get("chatSound", "1") != "0" { NSSound(named: "Glass")?.play() } }
}
