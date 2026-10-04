// NamazBar для macOS — время намаза в строке меню. Порт BarForm из NamazBar.cs:
// те же расчёт, города islom.uz, языки, циферблат, песочные часы, карточка и перерыв.
import AppKit
import SwiftUI
import ServiceManagement

@main
enum Main {
    static func main() {
        let args = CommandLine.arguments
        if let i = args.firstIndex(of: "--preview"), i + 1 < args.count {   // для сборки: картинки состояний
            Palette.registerFonts()
            MainActor.assumeIsolated { Preview.render(to: URL(fileURLWithPath: args[i + 1])) }
            return
        }
        if let i = args.firstIndex(of: "--icon"), i + 1 < args.count {      // для сборки: иконка 1024×1024
            Preview.icon(URL(fileURLWithPath: args[i + 1]))
            return
        }
        let app = NSApplication.shared
        let delegate = AppDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.accessory)   // только значок в строке меню, без Dock
        app.run()
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    let menu = NSMenu()
    var regions: [Region] = []
    var region: Region!
    var today: [Date] = [], tomorrow: [Date] = [], yesterday: [Date] = []
    var computedFor = ""
    var lastPeriod = Int.min
    var alertStart = Date.distantPast, alertUntil = Date.distantPast
    var tick: Timer?, anim: Timer?
    var card: CardPanel?
    var breakLater: (Date, String, String, Int)?
    var state = BarState()
    var lastSync: String?, syncError: String?

    static let breakDefault = [15, 0, 20, 10, 10, 15]   // Бомдод 15, Пешин 20, Аср 10, Шом 10, Хуфтон 15; у восхода нет
    static let breakChoices = [10, 15, 20]
    let alertMinutes = 10.0

    func applicationDidFinishLaunching(_ n: Notification) {
        Skin.load()
        Palette.registerFonts()
        let li = Lang.codes.firstIndex(of: Store.get("lang", "")) ?? Lang.systemIndex()
        Lang.cur = li
        regions = Store.loadRegions()
        selectRegion(Store.int("regionId", 27))
        menu.delegate = self
        statusItem.menu = menu
        ChatHooks.install()
        ChatHub.shared.prayerStarts = { [weak self] in   // чат стирает историю через 20 минут после каждого намаза (кроме восхода)
            guard let self = self, self.today.count == 6, self.yesterday.count == 6 else { return [] }
            return [0, 2, 3, 4, 5].flatMap { [self.yesterday[$0], self.today[$0]] }
        }
        recalc()
        tick = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.onTick() }
        if Store.get("autostartSet", "") == "" { setAutostart(true); Store.set("autostartSet", "1") }
        // список городов — раз в неделю
        let last = Store.defaults.object(forKey: "lastSync") as? Date
        if last == nil || Date().timeIntervalSince(last!) > 7 * 86400 { sync() }
        else { lastSync = Self.short.string(from: last!) }
    }

    static let short: DateFormatter = { let f = DateFormatter(); f.dateFormat = "dd.MM HH:mm"; return f }()
    static let hm: DateFormatter = { let f = DateFormatter(); f.dateFormat = "HH:mm"; return f }()

    func selectRegion(_ id: Int) {
        region = regions.first { $0.id == id } ?? regions.first { $0.id == 27 } ?? regions[0]
        computedFor = ""
    }

    // ───────── Периоды: -1 = Хуфтон (до Бомдода), 0..5 = индекс в today ─────────
    func recalc() {
        let now = Date()
        let key = Self.short.string(from: now).prefix(5) + "\(region.id)"
        if computedFor != key {
            let d = Calendar.current.startOfDay(for: now)
            today = Astro.compute(d, lat: region.lat, lng: region.lng)
            tomorrow = Astro.compute(Calendar.current.date(byAdding: .day, value: 1, to: d)!, lat: region.lat, lng: region.lng)
            yesterday = Astro.compute(Calendar.current.date(byAdding: .day, value: -1, to: d)!, lat: region.lat, lng: region.lng)
            computedFor = String(key)
        }
        var cur = -1
        for i in 0..<6 where now >= today[i] { cur = i }
        let nameIdx: Int, curTime: Date, nextIdx: Int, nextTime: Date
        if cur < 0 { nameIdx = 5; curTime = yesterday[5]; nextIdx = 0; nextTime = today[0] }
        else if cur == 5 { nameIdx = 5; curTime = today[5]; nextIdx = 0; nextTime = tomorrow[0] }
        else { nameIdx = cur; curTime = today[cur]; nextIdx = cur + 1; nextTime = today[cur + 1] }

        // наступление нового периода → подсветка и карточка (кроме восхода и смены суток после Хуфтона)
        if lastPeriod != Int.min && cur != lastPeriod && !(lastPeriod == 5 && cur == -1) && nameIdx != 1 {
            startAlert(alertMinutes * 60, sound: true)
            let t = Self.hm.string(from: curTime)
            DispatchQueue.main.async { self.onPrayerStart(nameIdx, t, fresh: true) }
        }
        // запуск посреди первых минут намаза — подсветка и карточка без перерыва
        if lastPeriod == Int.min && nameIdx != 1 && now >= curTime && now.timeIntervalSince(curTime) < alertMinutes * 60 {
            startAlert(curTime.addingTimeInterval(alertMinutes * 60).timeIntervalSince(now), sound: false)
            let t = Self.hm.string(from: curTime)
            DispatchQueue.main.async { self.onPrayerStart(nameIdx, t, fresh: false) }
        }
        lastPeriod = cur

        let left = nextTime.timeIntervalSince(now), passed = now.timeIntervalSince(curTime)
        let showCurrent = Double(Store.int("showCurrent", 40))
        let alert = now < alertUntil
        let countdown = passed / 60 >= showCurrent && !alert
        state.countdown = countdown
        state.progress = max(0, min(1, passed / nextTime.timeIntervalSince(curTime)))
        state.warnSoon = left / 60 <= 10
        state.hourglass = left / 60 <= StatusRenderer.hourglassMin && left > 0
        state.sandLeft = left / 60 / StatusRenderer.hourglassMin
        state.compact = Store.get("compact", "0") == "1"
        if countdown {
            state.time = Self.hm.string(from: nextTime)
            state.prefix = Lang.T("nextWord")
            state.name = Lang.names[nextIdx]
            state.detail = String(format: Lang.T("left"), Lang.span(Int(ceil(left / 60))))
            state.sunrise = false
        } else {
            state.time = Self.hm.string(from: curTime)
            state.prefix = ""
            state.name = Lang.names[nameIdx]
            state.detail = passed < 60 ? Lang.T("justNow") : String(format: Lang.T("passed"), Lang.span(Int(passed / 60)))
            state.sunrise = nameIdx == 1
        }
        if (state.hourglass || alert) && anim == nil {
            anim = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in self?.onAnim() }
        }
        redraw()
    }

    func onTick() {
        recalc()
        ChatHub.shared.pruneTick()
        if let b = breakLater, Date() >= b.0 { breakLater = nil; BreakScreen.show(prayer: b.1, time: b.2, minutes: b.3) }
    }

    func onAnim() {
        let now = Date()
        state.sandT = now.timeIntervalSince(Calendar.current.startOfDay(for: now))
        if now < alertUntil {
            let t = now.timeIntervalSince(alertStart)
            let period = t < 60 ? 0.9 : 2.4, amp = t < 60 ? 1.0 : 0.55
            state.alertPulse = amp * (0.5 - 0.5 * cos(2 * Double.pi * t / period))
        } else { state.alertPulse = 0 }
        if !state.hourglass && now >= alertUntil { anim?.invalidate(); anim = nil }
        redraw()
    }

    func redraw() {
        guard let b = statusItem.button else { return }
        let dark = b.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
        b.image = StatusRenderer.render(state, dark: dark)
        b.imagePosition = .imageOnly
        b.toolTip = tooltip()
    }

    func tooltip() -> String {
        var s = Lang.city(region) + " · " + DateFormatter.localizedString(from: Date(), dateStyle: .short, timeStyle: .none) + "\n"
        for i in 0..<6 { s += Lang.names[i] + "  " + Self.hm.string(from: today[i]) + "\n" }
        s += syncError.map { "⚠ " + $0 } ?? (Lang.T("synced") + (lastSync ?? Lang.T("never")))
        return s
    }

    func startAlert(_ duration: TimeInterval, sound: Bool) {
        alertStart = Date(); alertUntil = alertStart.addingTimeInterval(duration)
        if sound && Store.get("sound", "1") == "1" { NSSound(named: "Glass")?.play() }
    }

    // ───────── Карточка и перерыв ─────────
    static func breakMinutes(_ idx: Int) -> Int {
        guard idx >= 0 && idx < 6 && breakDefault[idx] > 0 else { return 0 }
        let m = Store.int("break\(idx)", breakDefault[idx])
        return breakChoices.contains(m) ? m : breakDefault[idx]
    }

    func onPrayerStart(_ idx: Int, _ time: String, fresh: Bool) {
        let brk = fresh ? Self.breakMinutes(idx) : 0
        if Store.get("card", "1") != "1" && brk == 0 { return }
        showCard(Lang.names[idx], time, brk)
    }

    func showCard(_ name: String, _ time: String, _ brk: Int) {
        card?.dismiss()
        let c = CardPanel(prayer: name, time: time, breakMinutes: brk)
        c.onBreak = { BreakScreen.show(prayer: name, time: time, minutes: brk) }
        c.onPostpone = { [weak self] m in self?.breakLater = (Date().addingTimeInterval(Double(m) * 60), name, time, brk) }
        c.show()
        card = c
    }

    // ───────── Меню ─────────
    func menuNeedsUpdate(_ m: NSMenu) { buildMenu() }

    func item(_ title: String, _ action: Selector?, _ checked: Bool = false, _ rep: Any? = nil) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: "")
        i.target = self; i.state = checked ? .on : .off; i.representedObject = rep
        return i
    }

    func withIconTop(_ it: NSMenuItem, _ symbol: String) -> NSMenuItem {
        it.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil); return it
    }

    func buildMenu() {
        menu.removeAllItems()
        // расписание на сегодня
        let head = NSMenuItem(title: Lang.city(region) + " · " + Lang.T("today"), action: nil, keyEquivalent: "")
        head.isEnabled = false; menu.addItem(head)
        for i in 0..<6 {
            let it = NSMenuItem(title: "\(Lang.names[i])\t\(Self.hm.string(from: today[i]))", action: nil, keyEquivalent: "")
            it.isEnabled = false
            it.state = (lastPeriod == i || (lastPeriod == -1 && i == 5)) ? .on : .off
            menu.addItem(it)
        }
        menu.addItem(.separator())
        // «Чат»: при наведении — группа и участники (онлайн сверху), быстрые фразы; окно чата — в самом низу подменю
        let chat = NSMenuItem(title: ChatT.T("menu"), action: nil, keyEquivalent: "")
        chat.image = NSImage(systemSymbolName: "bubble.left.and.bubble.right", accessibilityDescription: nil)
        let chatSub = NSMenu(); fillChatMenu(chatSub); chat.submenu = chatSub
        menu.addItem(chat)
        menu.addItem(.separator())

        func sub(_ title: String, _ symbol: String?) -> NSMenuItem {
            let m = NSMenuItem(title: title, action: nil, keyEquivalent: ""); m.submenu = NSMenu()
            if let n = symbol { m.image = NSImage(systemSymbolName: n, accessibilityDescription: nil) }
            return m
        }
        func withIcon(_ it: NSMenuItem, _ symbol: String) -> NSMenuItem {
            it.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil); return it
        }

        let city = sub(Lang.T("city"), "mappin.and.ellipse")
        let other = NSMenu()
        for r in regions {
            let it = item(Lang.city(r), #selector(pickCity(_:)), r.id == region.id, r.id)
            if r.order < 100 { city.submenu!.addItem(it) } else { other.addItem(it) }
        }
        if other.numberOfItems > 0 {
            city.submenu!.addItem(.separator())
            let o = NSMenuItem(title: Lang.T("other"), action: nil, keyEquivalent: ""); o.submenu = other
            city.submenu!.addItem(o)
        }
        menu.addItem(city)
        let lang = sub(Lang.T("lang"), "globe")
        for (i, t) in Lang.titles.enumerated() { lang.submenu!.addItem(item(t, #selector(pickLang(_:)), i == Lang.cur, i)) }
        menu.addItem(lang)

        // Оформление: цветовая тема, компактный вид
        let look = sub(Lang.T("look"), "paintpalette")
        let skins = sub(Lang.T("skin"), nil)
        for sk in Skin.all {
            let it = item(Skin.name(sk), #selector(pickSkin(_:)), sk.id == Skin.curId, sk.id)
            it.image = Skin.swatch(sk)
            skins.submenu!.addItem(it)
        }
        look.submenu!.addItem(skins)
        look.submenu!.addItem(item(Lang.T("compact"), #selector(toggle(_:)), Store.get("compact", "0") == "1", "compact"))
        menu.addItem(look)

        // Уведомления: звук, карточка, перерыв
        let notif = sub(Lang.T("alerts"), "bell")
        notif.submenu!.addItem(item(Lang.T("sound"), #selector(toggle(_:)), Store.get("sound", "1") == "1", "sound"))
        notif.submenu!.addItem(item(Lang.T("card"), #selector(toggle(_:)), Store.get("card", "1") == "1", "card"))
        let showCur = Store.int("showCurrent", 40)
        let sc = NSMenuItem(title: showCurrentTitle(), action: nil, keyEquivalent: ""); sc.submenu = NSMenu()
        for m in [15, 20, 30, 40, 60] { sc.submenu!.addItem(item(String(format: Lang.T("screen"), m), #selector(pickShowCurrent(_:)), m == showCur, m)) }
        notif.submenu!.addItem(sc)
        let brk = NSMenuItem(title: Lang.T("breakM"), action: nil, keyEquivalent: ""); brk.submenu = NSMenu()
        for p in 0..<6 where Self.breakDefault[p] > 0 {
            let cur = Self.breakMinutes(p)
            let sb = NSMenuItem(title: Lang.names[p] + " — " + String(format: Lang.T("screen"), cur), action: nil, keyEquivalent: "")
            sb.submenu = NSMenu()
            for m in Self.breakChoices { sb.submenu!.addItem(item(String(format: Lang.T("screen"), m), #selector(pickBreak(_:)), m == cur, [p, m])) }
            brk.submenu!.addItem(sb)
        }
        notif.submenu!.addItem(brk)
        notif.submenu!.addItem(.separator())
        notif.submenu!.addItem(item(Lang.T("test"), #selector(testCard)))
        menu.addItem(notif)

        // Настройки: автозапуск, данные islom.uz
        let sys = sub(Lang.T("system"), "gearshape")
        sys.submenu!.addItem(item(Lang.T("autorun"), #selector(toggleAutostart), SMAppService.mainApp.status == .enabled))
        sys.submenu!.addItem(.separator())
        sys.submenu!.addItem(withIcon(item(Lang.T("sync"), #selector(syncNow)), "arrow.triangle.2.circlepath"))
        sys.submenu!.addItem(withIcon(item(Lang.T("open"), #selector(openSite)), "link"))
        menu.addItem(sys)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: Lang.T("exit"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
    }

    func showCurrentTitle() -> String {
        ["Joriy namozni ko'rsatish", "Жорий намозни кўрсатиш", "Показывать текущий намаз", "Show current prayer for"][Lang.cur]
    }

    @objc func pickCity(_ s: NSMenuItem) { if let id = s.representedObject as? Int { Store.set("regionId", "\(id)"); selectRegion(id); lastPeriod = Int.min; recalc() } }
    @objc func pickLang(_ s: NSMenuItem) { if let i = s.representedObject as? Int { Lang.cur = i; Store.set("lang", Lang.codes[i]); ChatWindow.reopen(); recalc() } }
    @objc func openChat() { ChatWindow.show() }
    @objc func pickSkin(_ s: NSMenuItem) {
        guard let id = s.representedObject as? String else { return }
        Skin.apply(id); Store.set("skin", id)
        ChatWindow.reopen()   // SwiftUI-окно чата перерисовать в новых цветах
        recalc()
    }
    @objc func pickShowCurrent(_ s: NSMenuItem) { if let m = s.representedObject as? Int { Store.set("showCurrent", "\(m)"); recalc() } }
    @objc func pickBreak(_ s: NSMenuItem) { if let a = s.representedObject as? [Int] { Store.set("break\(a[0])", "\(a[1])") } }
    @objc func toggle(_ s: NSMenuItem) {
        guard let k = s.representedObject as? String else { return }
        let def = k == "compact" ? "0" : "1"
        Store.set(k, Store.get(k, def) == "1" ? "0" : "1")
        if k == "sound" && Store.get(k, "1") == "1" { NSSound(named: "Glass")?.play() }
        recalc()
    }
    @objc func testCard() { startAlert(20, sound: true); showCard(state.name, state.time, 0); recalc() }
    @objc func openSite() { NSWorkspace.shared.open(URL(string: "https://islom.uz/taqvim")!) }
    @objc func syncNow() { sync() }
    @objc func toggleAutostart() { setAutostart(SMAppService.mainApp.status != .enabled) }

    func setAutostart(_ on: Bool) {
        do { if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() } }
        catch { NSLog("NamazBar autostart: \(error)") }
    }

    func sync() {
        Store.download { [weak self] result in
            guard let self = self else { return }
            switch result {
            case .success(let list):
                self.regions = list
                self.selectRegion(self.region?.id ?? 27)
                self.syncError = nil
                Store.defaults.set(Date(), forKey: "lastSync")
                self.lastSync = Self.short.string(from: Date())
            case .failure(let e):
                self.syncError = Lang.T("syncFail") + e.localizedDescription
            }
            self.recalc()
        }
    }
}

// ───────── Картинки состояний для проверки на сборочном сервере (--preview <папка>) ─────────
enum Preview {
    /// Иконка: изумрудная плитка в золотой рамке, октаграмма и полумесяц (как у Windows-версии)
    static func icon(_ url: URL) {
        let n: CGFloat = 1024
        let img = NSImage(size: CGSize(width: n, height: n), flipped: false) { _ in
            let r = CGRect(x: n * 0.1, y: n * 0.1, width: n * 0.8, height: n * 0.8)   // поля по сетке значков macOS
            let tile = NSBezierPath(roundedRect: r, xRadius: n * 0.18, yRadius: n * 0.18)
            NSGradient(starting: Palette.mix(Palette.emerald, .white, 0.12), ending: Palette.emeraldDark)!.draw(in: tile, angle: -90)
            Palette.gold.setStroke(); tile.lineWidth = n * 0.022; tile.stroke()
            let c = CGPoint(x: n / 2, y: n / 2)
            let star = Palette.star8(c, n * 0.3)
            Palette.gold.withAlphaComponent(0.8).setStroke(); star.lineWidth = n * 0.016; star.stroke()
            // полумесяц: золотой круг минус сдвинутый круг
            let d = n * 0.34
            let moon = NSBezierPath(ovalIn: CGRect(x: c.x - d * 0.56, y: c.y - d / 2, width: d, height: d))
            let cut = NSBezierPath(ovalIn: CGRect(x: c.x - d * 0.56 + d * 0.30, y: c.y - d / 2 + d * 0.08, width: d * 0.9, height: d * 0.9))
            NSGraphicsContext.saveGraphicsState()
            let clip = NSBezierPath(rect: CGRect(x: 0, y: 0, width: n, height: n)); clip.append(cut.reversed); clip.addClip()
            Palette.gold.setFill(); moon.fill()
            NSGraphicsContext.restoreGraphicsState()
            return true
        }
        save(img, url, scale: 1)
    }

    @MainActor static func render(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        Lang.cur = 2
        let n = Lang.names
        func st(_ time: String, _ name: String, _ det: String, countdown: Bool, sand: Double = 0, pulse: Double = 0, compact: Bool = false) -> BarState {
            var s = BarState()
            s.time = time; s.name = name; s.detail = det; s.countdown = countdown; s.prefix = countdown ? Lang.T("nextWord") : ""
            s.progress = 0.6; s.hourglass = sand > 0; s.sandLeft = sand; s.sandT = 0.4; s.alertPulse = pulse
            s.warnSoon = sand > 0 && sand < 0.67; s.compact = compact
            return s
        }
        let states = [st("16:27", n[3], "прошло 12 мин", countdown: false),
                      st("18:18", n[4], "через 52 мин", countdown: true),
                      st("18:18", n[4], "через 12 мин", countdown: true, sand: 0.8),
                      st("18:18", n[4], "через 4 мин", countdown: true, sand: 0.27),
                      st("18:18", n[4], "через 1 мин", countdown: true, sand: 0.07),
                      st("04:56", n[0], "только что", countdown: false, pulse: 0.8),
                      st("18:18", n[4], "", countdown: true, sand: 0.5, compact: true)]
        // строка меню: тёмная и светлая, в 2x
        let rowH: CGFloat = 30, W: CGFloat = 420
        for dark in [true, false] {
            let img = NSImage(size: CGSize(width: W, height: rowH * CGFloat(states.count)), flipped: true) { _ in
                (dark ? NSColor(white: 0.12, alpha: 1) : NSColor(white: 0.93, alpha: 1)).setFill()
                NSBezierPath(rect: CGRect(x: 0, y: 0, width: W, height: rowH * CGFloat(states.count))).fill()
                for (i, s) in states.enumerated() {
                    StatusRenderer.render(s, dark: dark).draw(in: CGRect(origin: CGPoint(x: 8, y: CGFloat(i) * rowH + 4),
                                                                          size: StatusRenderer.render(s, dark: dark).size))
                }
                return true
            }
            save(img, dir.appendingPathComponent(dark ? "menubar-dark.png" : "menubar-light.png"), scale: 2)
        }
        let cardModel = CardModel(); cardModel.secondsLeft = 45
        savePNG(ImageRenderer(content: CardView(time: "18:18", title: String(format: Lang.T("title"), n[4]), breakMinutes: 10, model: cardModel)),
                dir.appendingPathComponent("card.png"))
        // сверка расчёта с Windows-версией: Ташкент, 26.09.2026 → 04:56 06:14 12:14 16:27 18:18 19:32
        var cal = Calendar(identifier: .gregorian); cal.timeZone = TimeZone(identifier: "Asia/Tashkent")!
        let day = cal.date(from: DateComponents(year: 2026, month: 9, day: 26))!
        let f = DateFormatter(); f.dateFormat = "HH:mm"; f.timeZone = cal.timeZone
        let times = Astro.compute(day, lat: 41.300872, lng: 69.241813).map { f.string(from: $0) }.joined(separator: " ")
        try? (times + "\n").write(to: dir.appendingPathComponent("times.txt"), atomically: true, encoding: .utf8)
        let bm = BreakModel()
        savePNG(ImageRenderer(content: BreakView(prayer: n[4], time: "18:18", until: Date().addingTimeInterval(575), model: bm).frame(width: 1440, height: 900)),
                dir.appendingPathComponent("break.png"))
    }

    static func save(_ img: NSImage, _ url: URL, scale: CGFloat) {
        let w = Int(img.size.width * scale), h = Int(img.size.height * scale)
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: w, pixelsHigh: h, bitsPerSample: 8, samplesPerPixel: 4,
                                         hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return }
        rep.size = img.size
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        img.draw(in: CGRect(origin: .zero, size: img.size))
        NSGraphicsContext.restoreGraphicsState()
        try? rep.representation(using: .png, properties: [:])?.write(to: url)
    }

    @MainActor static func savePNG<V: View>(_ r: ImageRenderer<V>, _ url: URL) {
        r.scale = 2
        guard let cg = r.cgImage else { return }
        try? NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])?.write(to: url)
    }
}
