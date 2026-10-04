// Карточка при наступлении намаза и экран «Перерыв на намаз» — как в Windows-версии (PrayerCard, BreakForm)
import AppKit
import SwiftUI

// ───────── Карточка ─────────
// Изумрудно-лазуритовая карточка в золотой рамке с аятом и хадисом. Если назначен перерыв —
// отсчёт 60 с и кнопки «Через 5 мин» / «Через 10 мин»; отменить перерыв нельзя.
final class CardModel: ObservableObject {
    @Published var secondsLeft = 60
}

struct CardView: View {
    let time: String, title: String, breakMinutes: Int
    @ObservedObject var model: CardModel
    var onPostpone: (Int) -> Void = { _ in }
    var onClose: () -> Void = {}

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 12) {
                Text(time).font(Font.ns(Palette.serif(17)))
                    .foregroundColor(Color(nsColor: Palette.rgb(250, 238, 204)))
                    .padding(.horizontal, 10).padding(.vertical, 5)
                    .background(LinearGradient(colors: [Color(nsColor: Palette.mix(Palette.emerald, .white, 0.1)), Color(nsColor: Palette.mix(Palette.emerald, .black, 0.3))],
                                               startPoint: .top, endPoint: .bottom))
                    .clipShape(RoundedRectangle(cornerRadius: 6))
                    .overlay(RoundedRectangle(cornerRadius: 6).stroke(Color.pGold, lineWidth: 1))
                Text(title).font(Font.ns(Palette.serif(17))).foregroundColor(.pIvory).lineLimit(1).fixedSize()
                Spacer(minLength: 8)
                if breakMinutes == 0 {
                    Button(action: onClose) { Image(systemName: "xmark").foregroundColor(.pIvory.opacity(0.7)) }.buttonStyle(.plain)
                }
            }
            GoldDivider()
            quote(Lang.T("ayah"), Lang.T("ayahRef"))
            quote(Lang.T("hadith"), Lang.T("hadRef"))
            if breakMinutes > 0 {
                Text(String(format: Lang.T("breakIn"), model.secondsLeft, breakMinutes))
                    .font(Font.ns(Palette.sans(13, weight: .medium))).foregroundColor(.pGold).padding(.top, 4)
                HStack(spacing: 8) {
                    Spacer()
                    pill(Lang.T("in5")) { onPostpone(5) }
                    pill(Lang.T("in10")) { onPostpone(10) }
                }
            }
        }
        .padding(18)
        .frame(width: 440)
        .background(ZStack {
            LinearGradient(colors: [.pEmeraldDark, .pLapisDark], startPoint: .top, endPoint: .bottom)
            Girih(step: 46, color: Color.pGold.opacity(0.07))
        })
        .clipShape(RoundedRectangle(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).stroke(Color.pGold.opacity(0.85), lineWidth: 1.4))
        .overlay(RoundedRectangle(cornerRadius: 11).stroke(Color.pGold.opacity(0.35), lineWidth: 0.8).padding(4))
    }

    func quote(_ text: String, _ ref: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(text).font(Font.ns(Palette.serif(14, bold: false, italic: true))).foregroundColor(.pIvory).fixedSize(horizontal: false, vertical: true)
            Text(ref).font(Font.ns(Palette.sans(11))).foregroundColor(.pGold.opacity(0.8))
        }
    }

    func pill(_ title: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title).font(Font.ns(Palette.sans(12.5, weight: .medium))).foregroundColor(.pIvory)
                .padding(.horizontal, 14).padding(.vertical, 7)
                .background(Capsule().fill(Color.pEmerald.opacity(0.3)))
                .overlay(Capsule().stroke(Color.pGold, lineWidth: 1.1))
        }.buttonStyle(.plain)
    }
}

/// Окно карточки: без рамки, поверх всех окон, в правом верхнем углу под строкой меню
final class CardPanel: NSPanel {
    private var timer: Timer?
    private let model = CardModel()
    private let breakAt: Date
    var onBreak: () -> Void = {}
    var onPostpone: (Int) -> Void = { _ in }

    init(prayer: String, time: String, breakMinutes: Int) {
        breakAt = Date().addingTimeInterval(60)
        super.init(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        isOpaque = false; backgroundColor = .clear; hasShadow = true
        level = .statusBar; collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        let view = CardView(time: time, title: String(format: Lang.T("title"), prayer), breakMinutes: breakMinutes, model: model,
                            onPostpone: { [weak self] m in self?.onPostpone(m); self?.dismiss() },
                            onClose: { [weak self] in self?.dismiss() })
        let host = NSHostingView(rootView: view)
        host.frame.size = host.fittingSize
        contentView = host
        setContentSize(host.fittingSize)
        if let scr = NSScreen.main?.visibleFrame {
            setFrameOrigin(CGPoint(x: scr.maxX - frame.width - 14, y: scr.maxY - frame.height - 10))
        }
        timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: true) { [weak self] _ in
            guard let self = self else { return }
            let left = Int(ceil(self.breakAt.timeIntervalSinceNow))
            self.model.secondsLeft = max(0, left)
            if breakMinutes > 0 && left <= 0 { self.onBreak(); self.dismiss() }
            if breakMinutes == 0 && left <= -120 { self.dismiss() }   // без перерыва — сама закрывается через 3 минуты
        }
    }

    func show() { alphaValue = 0; orderFrontRegardless(); NSAnimationContext.runAnimationGroup { $0.duration = 0.25; animator().alphaValue = 1 } }
    func dismiss() {
        timer?.invalidate(); timer = nil
        NSAnimationContext.runAnimationGroup({ $0.duration = 0.2; animator().alphaValue = 0 }, completionHandler: { self.orderOut(nil) })
    }
}

// ───────── Экран перерыва ─────────
// Полноэкранное окно поверх всего на N минут на каждом мониторе. Это не блокировка системы:
// выйти можно, удерживая кнопку или Esc 3 секунды (на экстренный случай).
final class BreakModel: ObservableObject {
    @Published var now = Date()
    @Published var holdStart: Date?
}

struct BreakView: View {
    let prayer: String, time: String, until: Date
    @ObservedObject var model: BreakModel
    var onHold: (Bool) -> Void = { _ in }

    var body: some View {
        let left = max(0, Int(until.timeIntervalSince(model.now)))
        GeometryReader { geo in
            ZStack {
                LinearGradient(colors: [.pEmeraldDark, .pLapisDark], startPoint: .top, endPoint: .bottom)
                Girih(step: 96, color: Color.pGold.opacity(0.06))
                VStack(spacing: 22) {
                    Spacer()
                    Text("\(Lang.T("breakT")) · \(prayer) · \(time)").font(Font.ns(Palette.serif(34))).foregroundColor(.pGold)
                    ZStack {
                        Star8().fill(Color.pEmerald.opacity(0.18))
                        Star8().stroke(Color.pGold.opacity(0.5), lineWidth: 1.4)
                        Circle().stroke(Color.pGold.opacity(0.3), lineWidth: 1).padding(40)
                        Text(String(format: "%02d:%02d", left / 60, left % 60))
                            .font(Font.ns(Palette.serif(min(110, geo.size.height * 0.11)))).foregroundColor(.pIvory).monospacedDigit()
                    }
                    .frame(width: min(360, geo.size.height * 0.36), height: min(360, geo.size.height * 0.36))
                    GoldDivider().frame(width: 440)
                    VStack(spacing: 4) {
                        Text(Lang.T("ayah")).font(Font.ns(Palette.serif(20, bold: false, italic: true))).foregroundColor(.pIvory)
                        Text(Lang.T("ayahRef")).font(Font.ns(Palette.sans(13))).foregroundColor(.pGold.opacity(0.85))
                    }
                    VStack(spacing: 4) {
                        Text(Lang.T("hadith")).font(Font.ns(Palette.serif(20, bold: false, italic: true))).foregroundColor(.pIvory)
                        Text(Lang.T("hadRef")).font(Font.ns(Palette.sans(13))).foregroundColor(.pGold.opacity(0.85))
                    }
                    .frame(maxWidth: 900)
                    Spacer()
                    // последнее сообщение из группового чата — видно и во время перерыва
                    if let line = ChatHub.shared.recentLine() {
                        Text(line).font(Font.ns(Palette.sans(15))).foregroundColor(.pGold).lineLimit(2).frame(maxWidth: 800)
                    }
                    holdButton.padding(.bottom, 50)
                }
                .multilineTextAlignment(.center)
                .padding(.horizontal, 40)
            }
        }
    }

    var holdButton: some View {
        let k = model.holdStart.map { min(1, model.now.timeIntervalSince($0) / 3) } ?? 0
        return Text(Lang.T("holdSkip")).font(Font.ns(Palette.sans(13))).foregroundColor(.pIvory.opacity(0.8))
            .padding(.horizontal, 26).padding(.vertical, 12)
            .background(GeometryReader { g in
                Capsule().fill(Color.pGold.opacity(0.35)).frame(width: g.size.width * CGFloat(k))
            }.clipShape(Capsule()))
            .overlay(Capsule().stroke(Color.pGold.opacity(0.55), lineWidth: 1.2))
            .gesture(DragGesture(minimumDistance: 0).onChanged { _ in onHold(true) }.onEnded { _ in onHold(false) })
    }
}

final class BreakWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
    var onEsc: (Bool) -> Void = { _ in }
    override func keyDown(with e: NSEvent) { if e.keyCode == 53 { onEsc(true) } }
    override func keyUp(with e: NSEvent) { if e.keyCode == 53 { onEsc(false) } }
}

enum BreakScreen {
    private static var windows: [BreakWindow] = []
    private static var timer: Timer?
    private static let model = BreakModel()

    static func show(prayer: String, time: String, minutes: Int) {
        if !windows.isEmpty { return }
        ChatHub.shared.onBreakStart()   // позвать группу на совместный намаз (если включено в чате)
        let until = Date().addingTimeInterval(Double(minutes) * 60)
        model.holdStart = nil
        let hold: (Bool) -> Void = { down in
            if down { if model.holdStart == nil { model.holdStart = Date() } } else { model.holdStart = nil }
        }
        for scr in NSScreen.screens {
            let w = BreakWindow(contentRect: scr.frame, styleMask: [.borderless], backing: .buffered, defer: false)
            w.level = .screenSaver
            w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary]
            w.isOpaque = true; w.backgroundColor = Palette.emeraldDark
            w.contentView = NSHostingView(rootView: BreakView(prayer: prayer, time: time, until: until, model: model, onHold: hold))
            w.onEsc = hold
            w.setFrame(scr.frame, display: true)
            w.makeKeyAndOrderFront(nil)
            windows.append(w)
        }
        NSApp.activate(ignoringOtherApps: true)
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { _ in
            model.now = Date()
            if model.now >= until { close(); return }
            if let h = model.holdStart, model.now.timeIntervalSince(h) >= 3 { close(); return }
            if !NSApp.isActive { NSApp.activate(ignoringOtherApps: true); windows.first?.makeKeyAndOrderFront(nil) }
        }
    }

    static func close() {
        timer?.invalidate(); timer = nil
        windows.forEach { $0.orderOut(nil) }
        windows.removeAll()
    }
}
