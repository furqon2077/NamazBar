// Палитра исламского искусства и орнамент — те же цвета, что в Windows-версии (Palette в NamazBar.cs)
import AppKit
import SwiftUI

enum Palette {
    static func rgb(_ r: Int, _ g: Int, _ b: Int, _ a: CGFloat = 1) -> NSColor {
        NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: a)
    }
    static let emerald = rgb(18, 94, 68)
    static let emeraldDark = rgb(10, 52, 40)
    static let jade = rgb(70, 184, 140)
    static let lapis = rgb(30, 56, 110)
    static let lapisDark = rgb(14, 26, 54)
    static let gold = rgb(222, 186, 98)
    static let goldDeep = rgb(160, 118, 34)
    static let ivory = rgb(246, 238, 218)
    static let ink = rgb(40, 32, 20)
    static let saffron = rgb(214, 132, 38)
    static let terracotta = rgb(196, 88, 50)

    static func mix(_ a: NSColor, _ b: NSColor, _ t: CGFloat) -> NSColor {
        let t = max(0, min(1, t))
        let x = a.usingColorSpace(.sRGB)!, y = b.usingColorSpace(.sRGB)!
        return NSColor(srgbRed: x.redComponent + (y.redComponent - x.redComponent) * t,
                       green: x.greenComponent + (y.greenComponent - x.greenComponent) * t,
                       blue: x.blueComponent + (y.blueComponent - x.blueComponent) * t,
                       alpha: x.alphaComponent + (y.alphaComponent - x.alphaComponent) * t)
    }

    // Антиква для цифр и заголовков (Palatino есть в любой macOS), остальное — Google Sans из пакета
    static func serif(_ size: CGFloat, bold: Bool = true, italic: Bool = false) -> NSFont {
        let name = bold ? "Palatino-Bold" : (italic ? "Palatino-Italic" : "Palatino-Roman")
        return NSFont(name: name, size: size) ?? NSFont(name: "Georgia", size: size) ?? .systemFont(ofSize: size, weight: bold ? .bold : .regular)
    }
    static func sans(_ size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let name = weight == .bold ? "NB Sans Bold" : weight == .medium ? "NB Sans Medium" : "NB Sans"
        return NSFont(name: name, size: size) ?? .systemFont(ofSize: size, weight: weight)
    }

    /// Регистрирует шрифты Google Sans из Resources (переименованы в «NB Sans», как в Windows-версии)
    static func registerFonts() {
        guard let dir = Bundle.main.resourceURL?.appendingPathComponent("fonts"),
              let files = try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil) else { return }
        for f in files where f.pathExtension.lowercased() == "ttf" {
            CTFontManagerRegisterFontsForURL(f as CFURL, .process, nil)
        }
    }

    /// Восьмиконечная звезда (руб-эль-хизб): два квадрата, повёрнутых на 45°
    static func star8(_ c: CGPoint, _ r: CGFloat) -> NSBezierPath {
        let p = NSBezierPath()
        for s in 0..<2 {
            for k in 0..<4 {
                let a = Double.pi / 4 * Double(s) + Double.pi / 2 * Double(k)
                let pt = CGPoint(x: c.x + r * CGFloat(cos(a)), y: c.y + r * CGFloat(sin(a)))
                if k == 0 { p.move(to: pt) } else { p.line(to: pt) }
            }
            p.close()
        }
        return p
    }

    /// Звёздная решётка (гирих) — едва заметный фон
    static func girih(_ rect: CGRect, step: CGFloat, color: NSColor, width: CGFloat) {
        let p = NSBezierPath()
        var y = rect.minY + step / 2
        while y < rect.maxY + step / 2 {
            var x = rect.minX + step / 2
            while x < rect.maxX + step / 2 { p.append(star8(CGPoint(x: x, y: y), step * 0.34)); x += step }
            y += step
        }
        p.lineWidth = width
        color.setStroke(); p.stroke()
    }
}

// Те же цвета для SwiftUI (карточка и экран перерыва)
extension Color {
    init(_ ns: NSColor) { self.init(nsColor: ns) }
    static let pEmerald = Color(Palette.emerald), pEmeraldDark = Color(Palette.emeraldDark), pJade = Color(Palette.jade)
    static let pLapisDark = Color(Palette.lapisDark), pGold = Color(Palette.gold), pIvory = Color(Palette.ivory)
}

/// Восьмиконечная звезда для SwiftUI
struct Star8: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        let c = CGPoint(x: rect.midX, y: rect.midY), r = min(rect.width, rect.height) / 2
        for s in 0..<2 {
            for k in 0..<4 {
                let a = Double.pi / 4 * Double(s) + Double.pi / 2 * Double(k)
                let pt = CGPoint(x: c.x + r * CGFloat(cos(a)), y: c.y + r * CGFloat(sin(a)))
                if k == 0 { p.move(to: pt) } else { p.addLine(to: pt) }
            }
            p.closeSubpath()
        }
        return p
    }
}

/// Звёздная решётка фоном для SwiftUI
struct Girih: View {
    var step: CGFloat
    var color: Color
    var body: some View {
        Canvas { ctx, size in
            var p = Path()
            var y = step / 2
            while y < size.height + step / 2 {
                var x = step / 2
                while x < size.width + step / 2 {
                    p.addPath(Star8().path(in: CGRect(x: x - step * 0.34, y: y - step * 0.34, width: step * 0.68, height: step * 0.68)))
                    x += step
                }
                y += step
            }
            ctx.stroke(p, with: .color(color), lineWidth: 1)
        }
    }
}

/// Золотой разделитель: линия со звездой посередине
struct GoldDivider: View {
    var body: some View {
        HStack(spacing: 8) {
            Rectangle().fill(Color.pGold.opacity(0.6)).frame(height: 1)
            Star8().fill(Color.pGold).frame(width: 10, height: 10)
            Rectangle().fill(Color.pGold.opacity(0.6)).frame(height: 1)
        }
    }
}

extension Font {
    /// SwiftUI-шрифт из NSFont (Palatino, NB Sans)
    static func ns(_ f: NSFont) -> Font { Font(f as CTFont) }
}
