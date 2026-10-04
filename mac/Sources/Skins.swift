// Цветовые темы (скины) — как windows/Skins.cs. Скин подменяет цвета Palette; всё, что рисуется после apply, берёт новые цвета.
import AppKit

struct SkinDef {
    let id: String
    let title: [String]   // uz-lotin, uz-kirill, ru, en
    let emerald, emeraldDark, jade, lapis, lapisHi, lapisDark, gold, goldDeep, ivory, ink: NSColor
}

enum Skin {
    private static func c(_ r: Int, _ g: Int, _ b: Int) -> NSColor { Palette.rgb(r, g, b) }

    static let all: [SkinDef] = [
        SkinDef(id: "emerald", title: ["Zumrad (yashil)", "Зумрад (яшил)", "Изумруд (зелёный)", "Emerald (green)"],
                emerald: c(18, 94, 68), emeraldDark: c(10, 52, 40), jade: c(70, 184, 140), lapis: c(16, 82, 90), lapisHi: c(72, 178, 168), lapisDark: c(8, 38, 46),
                gold: c(222, 186, 98), goldDeep: c(160, 118, 34), ivory: c(246, 238, 218), ink: c(40, 32, 20)),
        SkinDef(id: "ruby", title: ["Yoqut (qizil)", "Ёқут (қизил)", "Рубин (красный)", "Ruby (red)"],
                emerald: c(150, 28, 44), emeraldDark: c(68, 12, 22), jade: c(240, 106, 112), lapis: c(96, 30, 56), lapisHi: c(206, 96, 120), lapisDark: c(40, 12, 26),
                gold: c(232, 190, 108), goldDeep: c(168, 118, 40), ivory: c(250, 238, 230), ink: c(44, 24, 22)),
        SkinDef(id: "sapphire", title: ["Safir (ko'k)", "Сафир (кўк)", "Сапфир (синий)", "Sapphire (blue)"],
                emerald: c(24, 78, 150), emeraldDark: c(10, 30, 72), jade: c(92, 170, 240), lapis: c(40, 52, 130), lapisHi: c(100, 126, 214), lapisDark: c(10, 16, 52),
                gold: c(226, 196, 112), goldDeep: c(160, 124, 44), ivory: c(238, 244, 252), ink: c(24, 30, 44)),
        SkinDef(id: "sunset", title: ["Quyosh botishi (to'q sariq, sariq)", "Қуёш ботиши (тўқ сариқ, сариқ)", "Закат (оранжевый, жёлтый)", "Sunset (orange, yellow)"],
                emerald: c(190, 86, 18), emeraldDark: c(92, 36, 8), jade: c(255, 168, 60), lapis: c(150, 60, 24), lapisHi: c(240, 150, 70), lapisDark: c(60, 22, 8),
                gold: c(255, 214, 72), goldDeep: c(196, 140, 20), ivory: c(255, 244, 222), ink: c(52, 30, 12)),
        SkinDef(id: "onyx", title: ["Oniks (qora)", "Оникс (қора)", "Оникс (чёрный)", "Onyx (black)"],
                emerald: c(36, 36, 40), emeraldDark: c(12, 12, 14), jade: c(200, 200, 208), lapis: c(52, 52, 60), lapisHi: c(138, 138, 150), lapisDark: c(6, 6, 8),
                gold: c(214, 176, 92), goldDeep: c(150, 112, 36), ivory: c(240, 238, 232), ink: c(30, 30, 32)),
        SkinDef(id: "pearl", title: ["Injui (oq, kulrang)", "Инжуи (оқ, кулранг)", "Жемчуг (белый, серый)", "Pearl (white, grey)"],
                emerald: c(84, 92, 106), emeraldDark: c(44, 50, 60), jade: c(190, 200, 214), lapis: c(110, 118, 132), lapisHi: c(178, 186, 200), lapisDark: c(30, 34, 42),
                gold: c(240, 242, 246), goldDeep: c(150, 156, 168), ivory: c(250, 250, 252), ink: c(34, 38, 46)),
    ]

    static var curId = "emerald"

    static func find(_ id: String) -> SkinDef { all.first { $0.id == id } ?? all[0] }

    static func apply(_ id: String) {
        let s = find(id)
        curId = s.id
        Palette.emerald = s.emerald; Palette.emeraldDark = s.emeraldDark; Palette.jade = s.jade
        Palette.lapis = s.lapis; Palette.lapisHi = s.lapisHi; Palette.lapisDark = s.lapisDark; Palette.gold = s.gold
        Palette.goldDeep = s.goldDeep; Palette.ivory = s.ivory; Palette.ink = s.ink
    }

    static func load() { apply(Store.get("skin", "emerald")) }
    static func name(_ s: SkinDef) -> String { s.title[Lang.cur] }

    /// Кружок-образец цветов скина для меню: основной цвет, золотое кольцо, светлый акцент в центре
    static func swatch(_ s: SkinDef, size: CGFloat = 16) -> NSImage {
        let img = NSImage(size: CGSize(width: size, height: size), flipped: false) { _ in
            let r = CGRect(x: 1, y: 1, width: size - 2, height: size - 2)
            s.emerald.setFill(); NSBezierPath(ovalIn: r).fill()
            s.gold.setStroke(); let ring = NSBezierPath(ovalIn: r.insetBy(dx: 0.8, dy: 0.8)); ring.lineWidth = 1.6; ring.stroke()
            s.jade.setFill(); NSBezierPath(ovalIn: r.insetBy(dx: size * 0.3, dy: size * 0.3)).fill()
            return true
        }
        return img
    }
}
