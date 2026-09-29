// Картинка для строки меню: [циферблат] [песочные часы] Далее Магриб через 45 мин
// Порт View.Draw из NamazBar.cs, упрощённый под высоту строки меню macOS (22 pt).
import AppKit

struct BarState {
    var time = "--:--"
    var prefix = ""              // «Далее» в режиме отсчёта
    var name = ""
    var detail = ""              // «прошло 12 мин» / «через 45 мин»
    var countdown = false        // лазурит: отсчёт до следующего
    var sunrise = false          // шафран: период восхода
    var warnSoon = false         // шафран: < 10 мин до следующего
    var progress = 0.0
    var hourglass = false        // < 15 мин до следующего
    var sandLeft = 1.0
    var sandT = 0.0
    var alertPulse = 0.0         // 0..1, только что наступил намаз
    var compact = false          // только циферблат (и песочные часы)
}

enum StatusRenderer {
    static let height: CGFloat = 22
    static let hourglassMin = 15.0

    static func render(_ s: BarState, dark: Bool) -> NSImage {
        let timeFont = Palette.serif(13.5)
        let nameFont = Palette.sans(13, weight: .bold)
        let smallFont = Palette.sans(11)
        let fg = dark ? Palette.ivory : Palette.ink
        let dim = dark ? Palette.rgb(184, 172, 146) : Palette.rgb(112, 98, 76)

        let tw = (s.time as NSString).size(withAttributes: [.font: timeFont]).width
        let pill = CGRect(x: 1, y: 1, width: ceil(tw) + 16, height: height - 2)
        var x = pill.maxX + 5
        var hg = CGRect.zero
        if s.hourglass { hg = CGRect(x: x, y: 2, width: 11, height: height - 4); x = hg.maxX + 5 }
        var parts: [(String, NSFont, NSColor)] = []
        if !s.compact {
            if !s.prefix.isEmpty { parts.append((s.prefix, smallFont, dim)) }
            let nameC = s.alertPulse > 0 ? Palette.mix(fg, Palette.jade, 0.5 + 0.5 * s.alertPulse) : fg
            parts.append((s.name, nameFont, nameC))
            let detC = s.countdown ? (s.warnSoon ? (dark ? Palette.gold : Palette.goldDeep) : fg) : dim
            parts.append((s.detail, smallFont, detC))
        }
        var width = x
        for (i, p) in parts.enumerated() {
            width += (p.0 as NSString).size(withAttributes: [.font: p.1]).width + (i < parts.count - 1 ? 5 : 0)
        }
        if parts.isEmpty { width = x - 4 }
        let size = CGSize(width: ceil(width) + 2, height: height)

        return NSImage(size: size, flipped: true) { _ in
            drawDial(s, pill, timeFont)
            if s.hourglass { drawHourglass(s, hg, dark) }
            var cx = pill.maxX + 5 + (s.hourglass ? 16 : 0)
            for p in parts {
                let attrs: [NSAttributedString.Key: Any] = [.font: p.1, .foregroundColor: p.2]
                let sz = (p.0 as NSString).size(withAttributes: attrs)
                // общая базовая линия: по центру строки меню
                let baseline = height / 2 + nameFont.capHeight / 2
                (p.0 as NSString).draw(at: CGPoint(x: cx, y: baseline - p.1.ascender), withAttributes: attrs)
                cx += sz.width + 5
            }
            return true
        }
    }

    // Циферблат: изумруд — идёт намаз, лазурит — отсчёт, шафран — восход или < 10 мин; двойная золотая рамка, ромбы
    static func drawDial(_ s: BarState, _ r: CGRect, _ font: NSFont) {
        let base: NSColor = s.countdown ? (s.warnSoon ? Palette.saffron : Palette.lapis) : (s.sunrise ? Palette.saffron : Palette.emerald)
        let hi: NSColor = s.countdown ? (s.warnSoon ? Palette.rgb(240, 178, 84) : Palette.rgb(84, 118, 184))
                                      : (s.sunrise ? Palette.rgb(240, 178, 84) : Palette.jade)
        let p = s.alertPulse
        if p > 0 {   // ореол при наступлении намаза
            let halo = NSBezierPath(roundedRect: r.insetBy(dx: -1, dy: -1), xRadius: 6, yRadius: 6)
            hi.withAlphaComponent(0.25 + 0.45 * p).setFill(); halo.fill()
        }
        let path = NSBezierPath(roundedRect: r, xRadius: 4.5, yRadius: 4.5)
        let grad = NSGradient(starting: Palette.mix(Palette.mix(base, .white, 0.10), hi, p),
                              ending: Palette.mix(Palette.mix(base, .black, 0.28), hi, p * 0.7))!
        grad.draw(in: path, angle: 90)   // картинка перевёрнута (y вниз): светлее сверху
        NSGraphicsContext.saveGraphicsState()
        path.addClip()
        Palette.girih(r, step: r.height * 0.62, color: Palette.gold.withAlphaComponent(0.14), width: 0.5)
        NSGraphicsContext.restoreGraphicsState()
        Palette.gold.setStroke(); path.lineWidth = 1; path.stroke()
        let inner = NSBezierPath(roundedRect: r.insetBy(dx: 2, dy: 2), xRadius: 3, yRadius: 3)
        Palette.gold.withAlphaComponent(0.45).setStroke(); inner.lineWidth = 0.6; inner.stroke()
        for mx in [r.minX + 2, r.maxX - 2] {   // ромбы по краям
            let d: CGFloat = 1.8, my = r.midY
            let dia = NSBezierPath()
            dia.move(to: CGPoint(x: mx, y: my - d * 1.6)); dia.line(to: CGPoint(x: mx + d, y: my))
            dia.line(to: CGPoint(x: mx, y: my + d * 1.6)); dia.line(to: CGPoint(x: mx - d, y: my)); dia.close()
            Palette.gold.setFill(); dia.fill()
        }
        // прогресс периода — тонкая золотая черта у нижнего края
        let bx = r.minX + 6, bw = r.width - 12, by = r.maxY - 3.6
        Palette.ivory.withAlphaComponent(0.2).setFill(); NSBezierPath(rect: CGRect(x: bx, y: by, width: bw, height: 1)).fill()
        (s.warnSoon ? Palette.rgb(250, 200, 120) : Palette.gold).setFill()
        NSBezierPath(rect: CGRect(x: bx, y: by, width: bw * CGFloat(max(0, min(1, s.progress))), height: 1)).fill()
        // время
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: Palette.rgb(250, 238, 204)]
        let sz = (s.time as NSString).size(withAttributes: attrs)
        let shadow: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: NSColor.black.withAlphaComponent(0.45)]
        let pt = CGPoint(x: r.midX - sz.width / 2, y: r.midY - sz.height / 2 - 0.5)
        (s.time as NSString).draw(at: CGPoint(x: pt.x, y: pt.y + 0.8), withAttributes: shadow)
        (s.time as NSString).draw(at: pt, withAttributes: attrs)
    }

    // Песочные часы: песок сверху убывает вместе с оставшимся временем, струйка течёт;
    // < 5 мин — песок краснеет, < 3 мин — часы мягко светятся
    static func drawHourglass(_ s: BarState, _ r: CGRect, _ dark: Bool) {
        let left = max(0, min(1, s.sandLeft)), mins = left * hourglassMin
        let sand = mins < 5 ? Palette.mix(Palette.terracotta, Palette.saffron, CGFloat(mins / 5))
                            : Palette.mix(Palette.saffron, Palette.gold, CGFloat((mins - 5) / 10))
        let capH: CGFloat = 1.8
        let gx = r.minX + r.width * 0.14, gw = r.width * 0.72
        let gTop = r.minY + capH, gBot = r.maxY - capH, cx = r.midX, cy = (gTop + gBot) / 2
        let neck: CGFloat = 0.6
        if mins < 3 {
            let beat = 0.5 - 0.5 * cos(2 * Double.pi * s.sandT / (0.8 + mins * 0.4))
            sand.withAlphaComponent(CGFloat(0.12 + 0.3 * beat)).setFill()
            NSBezierPath(roundedRect: r.insetBy(dx: -2, dy: -1), xRadius: 3, yRadius: 3).fill()
        }
        let glass = NSBezierPath()
        glass.move(to: CGPoint(x: gx, y: gTop))
        glass.curve(to: CGPoint(x: cx - neck, y: cy), controlPoint1: CGPoint(x: gx, y: cy - (cy - gTop) * 0.35), controlPoint2: CGPoint(x: cx - neck * 2, y: cy - neck * 2))
        glass.curve(to: CGPoint(x: gx, y: gBot), controlPoint1: CGPoint(x: cx - neck * 2, y: cy + neck * 2), controlPoint2: CGPoint(x: gx, y: cy + (gBot - cy) * 0.35))
        glass.line(to: CGPoint(x: gx + gw, y: gBot))
        glass.curve(to: CGPoint(x: cx + neck, y: cy), controlPoint1: CGPoint(x: gx + gw, y: cy + (gBot - cy) * 0.35), controlPoint2: CGPoint(x: cx + neck * 2, y: cy + neck * 2))
        glass.curve(to: CGPoint(x: gx + gw, y: gTop), controlPoint1: CGPoint(x: cx + neck * 2, y: cy - neck * 2), controlPoint2: CGPoint(x: gx + gw, y: cy - (cy - gTop) * 0.35))
        glass.close()
        Palette.ivory.withAlphaComponent(dark ? 0.14 : 0.3).setFill(); glass.fill()
        NSGraphicsContext.saveGraphicsState()
        glass.addClip()
        sand.setFill()
        let topH = (cy - gTop) * 0.82 * CGFloat(left)
        if topH > 0.3 { NSBezierPath(rect: CGRect(x: gx, y: cy - topH, width: gw, height: topH)).fill() }
        let botH = (gBot - cy) * 0.82 * CGFloat(1 - left) + 0.6
        let sy = gBot - botH
        let heap = NSBezierPath()
        heap.move(to: CGPoint(x: gx, y: gBot)); heap.line(to: CGPoint(x: gx, y: sy + botH * 0.35))
        heap.curve(to: CGPoint(x: gx + gw, y: sy + botH * 0.35), controlPoint1: CGPoint(x: cx - gw * 0.2, y: sy), controlPoint2: CGPoint(x: cx + gw * 0.2, y: sy))
        heap.line(to: CGPoint(x: gx + gw, y: gBot)); heap.close(); heap.fill()
        if left > 0.002 {   // струйка и падающие песчинки
            let stream = NSBezierPath(); stream.move(to: CGPoint(x: cx, y: cy)); stream.line(to: CGPoint(x: cx, y: sy + 0.6))
            sand.withAlphaComponent(0.8).setStroke(); stream.lineWidth = 0.5; stream.stroke()
            for k in 0..<3 {
                let ph = (s.sandT * 1.6 + Double(k) / 3).truncatingRemainder(dividingBy: 1)
                let py = cy + (sy - cy) * CGFloat(ph)
                NSBezierPath(ovalIn: CGRect(x: cx - 0.6, y: py - 0.6, width: 1.2, height: 1.2)).fill()
            }
        }
        NSGraphicsContext.restoreGraphicsState()
        Palette.gold.withAlphaComponent(0.8).setStroke(); glass.lineWidth = 0.6; glass.stroke()
        Palette.gold.setFill()
        NSBezierPath(rect: CGRect(x: r.minX, y: r.minY, width: r.width, height: capH)).fill()
        NSBezierPath(rect: CGRect(x: r.minX, y: r.maxY - capH, width: r.width, height: capH)).fill()
        Palette.goldDeep.setStroke()
        for px in [r.minX + 0.6, r.maxX - 0.6] {
            let post = NSBezierPath(); post.move(to: CGPoint(x: px, y: r.minY + capH)); post.line(to: CGPoint(x: px, y: r.maxY - capH))
            post.lineWidth = 0.7; post.stroke()
        }
    }
}
