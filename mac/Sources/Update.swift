// Обновление: проверка новых релизов на GitHub, «что нового», загрузка .dmg и открытие образа
import AppKit

enum Updater {
    static let repo = "furqon2077/NamazBar"
    static var latest = "", notes = ""
    static var pageURL: URL?, assetURL: URL?
    static var available = false, checking = false
    static var onChange: () -> Void = {}
    static var timer: Timer?

    static var current: String {
        let v = (Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String) ?? "dev"
        return v.hasPrefix("__") ? "dev" : v
    }
    static var autoOn: Bool {
        get { UserDefaults.standard.object(forKey: "updAuto") as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: "updAuto") }
    }

    static let t: [String: [String]] = [
        "version":  ["Versiya %@", "Версия %@", "Версия %@", "Version %@"],
        "avail":    ["%@ ga yangilash", "%@ га янгилаш", "Обновить до %@", "Update to %@"],
        "check":    ["Yangilanishni tekshirish", "Янгиланишни текшириш", "Проверить обновления", "Check for updates"],
        "checking": ["Tekshirilmoqda…", "Текширилмоқда…", "Проверка…", "Checking…"],
        "auto":     ["Avtomatik tekshirish", "Автоматик текшириш", "Проверять автоматически", "Check automatically"],
        "latest":   ["NamazBar %@ - eng yangi versiya", "NamazBar %@ - энг янги версия", "У вас последняя версия: NamazBar %@", "NamazBar %@ is the latest version"],
        "nonet":    ["GitHub bilan aloqa yo'q", "GitHub билан алоқа йўқ", "Не удалось проверить обновления", "Could not check for updates"],
        "title":    ["Yangi versiya mavjud: %@", "Янги версия мавжуд: %@", "Доступна новая версия: %@", "A new version is available: %@"],
        "have":     ["Sizda %@ o'rnatilgan.\n\nNima yangi:\n", "Сизда %@ ўрнатилган.\n\nНима янги:\n", "У вас установлена %@.\n\nЧто нового:\n", "You have %@.\n\nWhat's new:\n"],
        "download": ["Yuklab olish", "Юклаб олиш", "Скачать", "Download"],
        "later":    ["Keyinroq", "Кейинроқ", "Позже", "Later"],
        "loading":  ["Yuklanmoqda…", "Юкланмоқда…", "Загрузка…", "Downloading…"],
        "opened":   ["Disk obrazi ochildi: NamazBar'ni Programmalar papkasiga torting", "Диск образи очилди: NamazBar'ни Программалар папкасига торинг", "Образ открыт: перетащите NamazBar в папку «Программы»", "Disk image opened: drag NamazBar to Applications"],
        "fail":     ["Yuklab bo'lmadi: %@", "Юклаб бўлмади: %@", "Не удалось скачать: %@", "Download failed: %@"],
    ]
    static func T(_ k: String) -> String { t[k]?[Lang.cur] ?? k }

    /// "1.5.13" → [1,5,13]; "dev" → [0]
    static func parts(_ s: String) -> [Int] {
        let p = s.trimmingCharacters(in: CharacterSet(charactersIn: "vV")).split(separator: ".").map { Int($0) ?? 0 }
        return p.isEmpty ? [0] : p
    }
    static func isNewer(_ a: String, than b: String) -> Bool {
        let x = parts(a), y = parts(b)
        for i in 0..<max(x.count, y.count) {
            let u = i < x.count ? x[i] : 0, v = i < y.count ? y[i] : 0
            if u != v { return u > v }
        }
        return false
    }

    /// Проверка при запуске (через 20 секунд) и затем раз в час: реально ходим на GitHub не чаще раза в 6 часов
    static func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 20, repeats: false) { _ in
            tick()
            timer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { _ in tick() }
        }
    }
    static func tick() {
        let last = UserDefaults.standard.double(forKey: "updLast")
        if autoOn && Date().timeIntervalSince1970 - last >= 6 * 3600 { check(manual: false) }
    }

    static func check(manual: Bool) {
        if checking { return }
        checking = true; onChange()
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!)
        req.setValue("NamazBar/\(current)", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 15
        URLSession.shared.dataTask(with: req) { data, _, error in
            DispatchQueue.main.async { apply(data, error, manual) }
        }.resume()
    }

    static func apply(_ data: Data?, _ error: Error?, _ manual: Bool) {
        checking = false
        guard error == nil, let data = data,
              let d = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = d["tag_name"] as? String else {
            onChange()
            if manual { ChatToast.info(T("nonet")) }
            return
        }
        UserDefaults.standard.set(Date().timeIntervalSince1970, forKey: "updLast")
        latest = tag.trimmingCharacters(in: CharacterSet(charactersIn: "vV"))
        notes = (d["body"] as? String) ?? ""
        pageURL = (d["html_url"] as? String).flatMap { URL(string: $0) }
        assetURL = nil
        for a in (d["assets"] as? [[String: Any]]) ?? [] {
            if let n = a["name"] as? String, n.hasSuffix(".dmg"), let u = a["browser_download_url"] as? String { assetURL = URL(string: u) }
        }
        available = isNewer(latest, than: current) && assetURL != nil
        onChange()
        if available {
            let seen = UserDefaults.standard.string(forKey: "updSeen") == latest
            if manual || (!seen && current != "dev") {
                UserDefaults.standard.set(latest, forKey: "updSeen")
                showDialog()
            }
        } else if manual {
            ChatToast.info(String(format: T("latest"), current))
        }
    }

    /// Заметки релиза: раздел на языке программы (## ru / ## en), иначе весь текст без служебных строк GitHub
    static func notesText() -> String {
        let codes = ["uz", "uz", "ru", "en"]
        var sections: [String: [String]] = [:], all: [String] = []
        var cur: String?
        for raw in notes.replacingOccurrences(of: "\r", with: "").components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            let low = line.lowercased()
            if low == "## ru" || low == "## en" || low == "## uz" { cur = String(low.dropFirst(3)); sections[cur!] = []; continue }
            if low.hasPrefix("**full changelog**") { cur = nil; continue }
            if low.hasPrefix("## what") { continue }
            if let c = cur { sections[c, default: []].append(line) } else { all.append(line) }
        }
        let pick = sections[codes[Lang.cur]] ?? sections["en"] ?? all
        var text = pick.joined(separator: "\n")
        text = text.replacingOccurrences(of: #"(?m)^\s*[\*\-]\s+"#, with: "• ", options: .regularExpression)
        text = text.replacingOccurrences(of: #"\s+by @\S+ in https?://\S+"#, with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: #"\n{3,}"#, with: "\n\n", options: .regularExpression)
        text = text.trimmingCharacters(in: .whitespacesAndNewlines)
        return text.isEmpty ? (pageURL?.absoluteString ?? "") : text
    }

    static func showDialog() {
        NSApp.activate(ignoringOtherApps: true)
        let alert = NSAlert()
        alert.messageText = String(format: T("title"), latest)
        alert.informativeText = String(format: T("have"), current)
        let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: 380, height: 150))
        scroll.hasVerticalScroller = true; scroll.borderType = .bezelBorder
        let tv = NSTextView(frame: scroll.bounds)
        tv.isEditable = false; tv.string = notesText(); tv.font = .systemFont(ofSize: 13)
        tv.textContainerInset = NSSize(width: 6, height: 6)
        scroll.documentView = tv
        alert.accessoryView = scroll
        alert.addButton(withTitle: T("download"))
        alert.addButton(withTitle: T("later"))
        if alert.runModal() == .alertFirstButtonReturn { download() }
    }

    /// .dmg скачивается в «Загрузки» и открывается: остаётся перетащить NamazBar в «Программы»
    static func download() {
        guard let url = assetURL else { return }
        ChatToast.info(T("loading"))
        URLSession.shared.downloadTask(with: url) { tmp, _, error in
            DispatchQueue.main.async {
                guard let tmp = tmp, error == nil else {
                    ChatToast.info(String(format: T("fail"), error?.localizedDescription ?? "?")); return
                }
                let dir = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first ?? FileManager.default.temporaryDirectory
                let dest = dir.appendingPathComponent("NamazBar-\(latest).dmg")
                try? FileManager.default.removeItem(at: dest)
                do { try FileManager.default.moveItem(at: tmp, to: dest) }
                catch { ChatToast.info(String(format: T("fail"), error.localizedDescription)); return }
                NSWorkspace.shared.open(dest)
                ChatToast.info(T("opened"))
            }
        }.resume()
    }
}
