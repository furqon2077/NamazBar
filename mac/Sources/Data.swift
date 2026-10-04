// Города islom.uz, настройки и тексты на 4 языках — как в NamazBar.cs (Store, Lang, Words)
import Foundation

struct Region {
    var id: Int, name: String, nameRu: String, lat: Double, lng: Double, order: Int
}

enum Store {
    static let defaults = UserDefaults.standard
    static let dir: URL = {
        let u = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("NamazBar")
        try? FileManager.default.createDirectory(at: u, withIntermediateDirectories: true)
        return u
    }()
    static var regionsFile: URL { dir.appendingPathComponent("regions.txt") }
    static let regionsURL = URL(string: "https://new.islom.uz/api/v1/regions")!

    static func get(_ k: String, _ def: String) -> String { defaults.string(forKey: k) ?? def }
    static func set(_ k: String, _ v: String) { defaults.set(v, forKey: k) }
    static func int(_ k: String, _ def: Int) -> Int { Int(get(k, "")) ?? def }

    // Встроенный запасной список (снят с islom.uz), если сети нет при первом запуске
    static let builtin =
        "27|Тошкент|1|41.300872|69.241813|Ташкент\n1|Андижон|2|40.786215|72.328205\n4|Бухоро|3|39.772300|64.423324\n" +
        "5|Гулистон|4|40.492550|68.777611|Гулистан\n18|Самарқанд|5|39.650650|66.976460\n15|Наманган|6|41.005175|71.643583\n" +
        "14|Навоий|7|40.102403|65.367579|Навои\n9|Жиззах|8|40.122196|67.873275\n16|Нукус|9|42.471325|59.616820\n" +
        "25|Қарши|10|38.830248|65.778764|Карши\n26|Қўқон|11|40.535509|70.937948\n21|Хива|12|41.389141|60.350174\n" +
        "13|Марғилон|13|40.47111|71.72472|Маргилан\n37|Фарғона|99|40.376952|71.793129\n74|Термиз|99|37.258528|67.308346\n" +
        "78|Урганч|99|41.553823|60.620611|Ургенч"

    static func parse(_ text: String) -> [Region] {
        text.split(separator: "\n").compactMap { line in
            let p = line.trimmingCharacters(in: .whitespaces).split(separator: "|", omittingEmptySubsequences: false).map(String.init)
            guard p.count >= 5, let id = Int(p[0]), let lat = Double(p[3]), let lng = Double(p[4]) else { return nil }
            return Region(id: id, name: p[1], nameRu: p.count > 5 ? p[5] : "", lat: lat, lng: lng, order: Int(p[2]) ?? 999)
        }
    }

    static func loadRegions() -> [Region] {
        if let t = try? String(contentsOf: regionsFile, encoding: .utf8) { let l = parse(t); if !l.isEmpty { return l } }
        return parse(builtin)
    }

    /// Скачивает список городов с islom.uz и сохраняет. completion — в главном потоке.
    static func download(_ completion: @escaping (Result<[Region], Error>) -> Void) {
        var req = URLRequest(url: regionsURL, timeoutInterval: 20)
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.setValue("https://islom.uz", forHTTPHeaderField: "Origin")
        req.setValue("https://islom.uz/", forHTTPHeaderField: "Referer")
        URLSession.shared.dataTask(with: req) { data, _, err in
            var result: Result<[Region], Error>
            if let err = err { result = .failure(err) }
            else {
                var list: [Region] = []
                if let data = data, let obj = try? JSONSerialization.jsonObject(with: data) {
                    var items: [[String: Any]] = []
                    if let a = obj as? [[String: Any]] { items = a }
                    else if let d = obj as? [String: Any] {
                        for v in d.values { if let a = v as? [[String: Any]] { items = a; break } }
                    }
                    func str(_ o: [String: Any], _ k: String) -> String? {
                        if let s = o[k] as? String { return s }
                        if let n = o[k] as? NSNumber { return n.stringValue }
                        return nil
                    }
                    for o in items {
                        if let st = str(o, "status"), st != "1" { continue }
                        guard let id = Int(str(o, "id") ?? ""), let lat = Double(str(o, "latitude") ?? ""),
                              let lng = Double(str(o, "longitude") ?? "") else { continue }
                        let name = (str(o, "name") ?? "").trimmingCharacters(in: .whitespaces)
                        if name.isEmpty { continue }
                        list.append(Region(id: id, name: name, nameRu: (str(o, "name_ru") ?? "").trimmingCharacters(in: .whitespaces),
                                           lat: lat, lng: lng, order: Int(str(o, "order") ?? "") ?? 999))
                    }
                }
                if list.isEmpty { result = .failure(NSError(domain: "NamazBar", code: 1, userInfo: [NSLocalizedDescriptionKey: "empty response"])) }
                else {
                    // главные города (order) вперёд, остальные по алфавиту — как на сайте
                    list.sort { $0.order != $1.order ? $0.order < $1.order : $0.name.localizedCompare($1.name) == .orderedAscending }
                    let text = list.map { "\($0.id)|\($0.name)|\($0.order)|\($0.lat)|\($0.lng)|\($0.nameRu.replacingOccurrences(of: "|", with: " "))" }.joined(separator: "\n")
                    try? text.write(to: regionsFile, atomically: true, encoding: .utf8)
                    result = .success(list)
                }
            }
            DispatchQueue.main.async { completion(result) }
        }.resume()
    }
}

// ───────── Языки: 0 = O'zbekcha (lotin), 1 = Ўзбекча (кирилл), 2 = Русский, 3 = English ─────────
enum Lang {
    static var cur = 2
    static let titles = ["O'zbekcha (lotin)", "Ўзбекча (кирилл)", "Русский", "English"]
    static let codes = ["uz-latn", "uz-cyrl", "ru", "en"]
    static let cultures = ["uz-Latn-UZ", "uz-Cyrl-UZ", "ru-RU", "en-US"]

    static let allNames = [
        ["Bomdod", "Quyosh", "Peshin", "Asr", "Shom", "Xufton"],
        ["Бомдод", "Қуёш", "Пешин", "Аср", "Шом", "Хуфтон"],
        ["Фаджр", "Восход", "Зухр", "Аср", "Магриб", "Иша"],
        ["Fajr", "Sunrise", "Dhuhr", "Asr", "Maghrib", "Isha"]]
    static var names: [String] { allNames[cur] }

    static let t: [String: [String]] = [
        "justNow":  ["hozirgina", "ҳозиргина", "только что", "just now"],
        "passed":   ["%@ o'tdi", "%@ ўтди", "прошло %@", "%@ ago"],
        "left":     ["yana %@", "яна %@", "через %@", "after %@"],
        "min":      ["daq", "дақ", "мин", "min"],
        "hour":     ["soat", "соат", "ч", "h"],
        "nextWord": ["Keyingi", "Кейинги", "Далее", "Next"],
        "today":    ["Bugun", "Бугун", "Сегодня", "Today"],
        "city":     ["Shahar", "Шаҳар", "Город", "City"],
        "look":     ["Ko'rinish", "Кўриниш", "Оформление", "Appearance"],
        "skin":     ["Rang mavzusi", "Ранг мавзуси", "Цветовая тема", "Color theme"],
        "alerts":   ["Bildirishnomalar", "Билдиришномалар", "Уведомления", "Notifications"],
        "system":   ["Sozlamalar", "Созламалар", "Настройки", "Settings"],
        "other":    ["Boshqa shaharlar", "Бошқа шаҳарлар", "Другие города", "Other cities"],
        "lang":     ["Til / Язык / Language", "Тил / Язык / Language", "Язык / Til / Language", "Language / Til / Язык"],
        "sync":     ["islom.uz dan yangilash", "islom.uz дан янгилаш", "Обновить с islom.uz", "Update from islom.uz"],
        "open":     ["islom.uz ni ochish", "islom.uz ни очиш", "Открыть islom.uz", "Open islom.uz"],
        "sound":    ["Namoz vaqti kirganda ovoz", "Намоз вақти кирганда овоз", "Звук при наступлении намаза", "Sound when prayer time begins"],
        "card":     ["Namoz vaqti kirganda xabar", "Намоз вақти кирганда хабар", "Карточка при наступлении намаза", "Card when prayer time begins"],
        "compact":  ["Ixcham ko'rinish (faqat vaqt)", "Ихчам кўриниш (фақат вақт)", "Компактно (только время)", "Compact (time only)"],
        "test":     ["Xabarni ko'rsatish (sinov)", "Хабарни кўрсатиш (синов)", "Показать карточку (тест)", "Show card (test)"],
        "autorun":  ["Mac bilan birga ishga tushirish", "Mac билан бирга ишга тушириш", "Запускать вместе с Mac", "Open at login"],
        "exit":     ["Chiqish", "Чиқиш", "Выход", "Quit"],
        "synced":   ["islom.uz bilan sinxronlash: ", "islom.uz билан синхронлаш: ", "Синхронизация с islom.uz: ", "Synced with islom.uz: "],
        "never":    ["hech qachon", "ҳеч қачон", "никогда", "never"],
        "syncFail": ["Yangilab bo'lmadi: ", "Янгилаб бўлмади: ", "Не удалось обновить: ", "Update failed: "],
        // карточка и перерыв (Words в Windows-версии)
        "title":    ["%@ namozi vaqti kirdi", "%@ намози вақти кирди", "Наступило время намаза %@", "It's time for %@"],
        "ayah":     ["«Albatta, namoz mo'minlarga vaqtlari belgilangan farz bo'lgandir»",
                     "«Албатта, намоз мўминларга вақтлари белгиланган фарз бўлгандир»",
                     "«Воистину, намаз предписан верующим в строго определённое время»",
                     "“Indeed, prayer has been decreed upon the believers at specified times”"],
        "ayahRef":  ["Niso surasi, 103-oyat", "Нисо сураси, 103-оят", "Сура ан-Ниса, аят 103", "Surah An-Nisa, 4:103"],
        "hadith":   ["«Allohga eng suyukli amal — o'z vaqtida o'qilgan namoz»",
                     "«Аллоҳга энг суюкли амал — ўз вақтида ўқилган намоз»",
                     "«Самое любимое Аллахом деяние — намаз, совершённый в своё время»",
                     "“The deed most beloved to Allah is prayer offered at its proper time”"],
        "hadRef":   ["Buxoriy, Muslim", "Бухорий, Муслим", "аль-Бухари, Муслим", "Bukhari, Muslim"],
        "breakIn":  ["%2$d daqiqalik tanaffus %1$d s dan keyin", "%2$d дақиқалик танаффус %1$d с дан кейин", "Перерыв %2$d мин через %1$d с", "%2$d-min break in %1$d s"],
        "in5":      ["5 daqiqadan keyin", "5 дақиқадан кейин", "Через 5 мин", "In 5 min"],
        "in10":     ["10 daqiqadan keyin", "10 дақиқадан кейин", "Через 10 мин", "In 10 min"],
        "holdSkip": ["Chiqish uchun bosib turing", "Чиқиш учун босиб туринг", "Удерживайте, чтобы выйти", "Hold to exit"],
        "breakT":   ["Namoz vaqti", "Намоз вақти", "Время намаза", "Prayer time"],
        "breakM":   ["Namoz tanaffusi", "Намоз танаффуси", "Перерыв на намаз", "Prayer break"],
        "screen":   ["%d daqiqa", "%d дақиқа", "%d мин", "%d min"],
    ]
    static func T(_ key: String) -> String { t[key]?[cur] ?? key }

    static func span(_ mins: Int) -> String {
        if mins < 60 { return "\(mins) \(T("min"))" }
        return "\(mins / 60) \(T("hour")) \(String(format: "%02d", mins % 60)) \(T("min"))"
    }

    // Названия городов на islom.uz — кириллицей; для латиницы/английского транслитерируем
    static func city(_ r: Region) -> String {
        if cur == 1 { return r.name }
        if cur == 2 { return r.nameRu.isEmpty ? r.name : r.nameRu }
        return toLatin(r.name)
    }

    static let map: [Character: String] = [
        "а": "a", "б": "b", "в": "v", "г": "g", "д": "d", "е": "e", "ё": "yo", "ж": "j", "з": "z", "и": "i",
        "й": "y", "к": "k", "л": "l", "м": "m", "н": "n", "о": "o", "п": "p", "р": "r", "с": "s", "т": "t",
        "у": "u", "ф": "f", "х": "x", "ц": "ts", "ч": "ch", "ш": "sh", "щ": "sh", "ъ": "'", "ь": "", "ы": "i",
        "э": "e", "ю": "yu", "я": "ya", "ў": "o'", "қ": "q", "ғ": "g'", "ҳ": "h"]
    static func toLatin(_ s: String) -> String {
        var out = ""
        let chars = Array(s)
        for (i, c) in chars.enumerated() {
            let lc = Character(c.lowercased())
            var v: String
            if lc == "е" && (i == 0 || "аеёиоуэюяўъь ".contains(Character(chars[i - 1].lowercased()))) { v = "ye" }
            else if let m = map[lc] { v = m }
            else { out.append(c); continue }
            if c != lc && !v.isEmpty { v = v.prefix(1).uppercased() + v.dropFirst() }
            out += v
        }
        return out
    }

    static func systemIndex() -> Int {
        let l = Locale.preferredLanguages.first ?? "en"
        if l.hasPrefix("uz") { return l.contains("Cyrl") ? 1 : 0 }
        if l.hasPrefix("ru") { return 2 }
        return 3
    }
}
