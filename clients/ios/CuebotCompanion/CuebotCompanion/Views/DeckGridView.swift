import CuebotKit
import SwiftUI

/// The deck's grid: `columns × rows` cells with the deck's gap, as large as fit (cells stay square-ish so the layout
/// matches the desktop deck). Empty cells are left blank.
struct DeckGridView: View {
    let view: DeckView
    var streamMode = false

    var body: some View {
        GeometryReader { geometry in
            let layout = DeckLayout(size: geometry.size, rows: view.rows, columns: view.columns, gap: CGFloat(view.gap), padding: streamMode ? 8 : 16)
            ZStack(alignment: .topLeading) {
                ForEach(view.items.filter { $0.row < view.rows && $0.column < view.columns }) { item in
                    DeckButton(item: item, size: layout.cell)
                        .position(layout.center(row: item.row, column: item.column))
                }
            }
            .frame(width: geometry.size.width, height: geometry.size.height)
        }
    }
}

/// Where the cells go: pure geometry, so it can be checked at any screen size.
struct DeckLayout: Equatable {
    let cell: CGSize
    let origin: CGPoint
    let gap: CGFloat

    init(size: CGSize, rows: Int, columns: Int, gap: CGFloat, padding: CGFloat) {
        let rows = CGFloat(max(1, rows)), columns = CGFloat(max(1, columns))
        let width = max(0, (size.width - 2 * padding - gap * (columns - 1)) / columns)
        let height = max(0, (size.height - 2 * padding - gap * (rows - 1)) / rows)
        let side = min(width, height * 1.25)   // a little wider than tall at most
        let cellHeight = min(height, side)
        cell = CGSize(width: side, height: cellHeight)
        self.gap = gap
        let usedWidth = side * columns + gap * (columns - 1)
        let usedHeight = cellHeight * rows + gap * (rows - 1)
        origin = CGPoint(x: (size.width - usedWidth) / 2, y: (size.height - usedHeight) / 2)
    }

    func center(row: Int, column: Int) -> CGPoint {
        CGPoint(x: origin.x + CGFloat(column) * (cell.width + gap) + cell.width / 2,
                y: origin.y + CGFloat(row) * (cell.height + gap) + cell.height / 2)
    }
}

struct DeckButton: View {
    @Environment(DeckStore.self) private var deck
    @Environment(ConnectionStore.self) private var connection
    let item: DeckItem
    let size: CGSize
    @State private var pressed = false
    @State private var flash: Color?
    @State private var feedback: SensoryFeedback?
    @State private var feedbackTrigger = 0

    var body: some View {
        let background = Color(cuebot: item.background, fallback: .cuebotRaised)
        let titleColor = Color(cuebot: item.titleColor, fallback: .primary)
        Button {
            Task {
                let result = await deck.press(item)
                feedback = result == .ok ? .impact(weight: .light) : .impact(weight: .heavy)
                feedbackTrigger += 1
                if result == .failed {
                    withAnimation(.easeOut(duration: 0.15)) { flash = .cuebotBad }
                    withAnimation(.easeIn(duration: 0.5).delay(0.3)) { flash = nil }
                }
            }
        } label: {
            ZStack(alignment: .topTrailing) {
                RoundedRectangle(cornerRadius: min(size.width, size.height) * 0.14)
                    .fill(background)
                    .overlay {
                        if !item.backgroundImage.isEmpty {
                            DeckRemoteImage(link: item.backgroundImage, base: connection.endpoint?.httpBase, fill: true)
                                .clipShape(RoundedRectangle(cornerRadius: min(size.width, size.height) * 0.14))
                        }
                    }
                    .overlay {
                        if let flash { RoundedRectangle(cornerRadius: min(size.width, size.height) * 0.14).fill(flash.opacity(0.55)) }
                    }
                VStack(spacing: 4) {
                    if !item.iconUrl.isEmpty {
                        DeckRemoteImage(link: item.iconUrl, base: connection.endpoint?.httpBase)
                            .frame(width: min(size.width, size.height) * 0.38, height: min(size.width, size.height) * 0.38)
                    } else if !item.icon.isEmpty {
                        DeckIconView(name: item.icon, color: Color(cuebot: item.iconColor, fallback: titleColor),
                                     size: min(size.width, size.height) * (item.title.isEmpty ? 0.5 : 0.34))
                    }
                    if !item.title.isEmpty {
                        Text(item.title)
                            .font(.system(size: max(10, min(size.width, size.height) * 0.15), weight: .semibold))
                            .foregroundStyle(titleColor)
                            .multilineTextAlignment(.center)
                            .lineLimit(3)
                            .minimumScaleFactor(0.5)
                    }
                }
                .padding(6)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                if !item.value.isEmpty {
                    Text(item.value)
                        .font(.system(size: max(9, min(size.width, size.height) * 0.12), weight: .bold).monospacedDigit())
                        .padding(.horizontal, 5)
                        .padding(.vertical, 2)
                        .background(.black.opacity(0.45), in: Capsule())
                        .foregroundStyle(.white)
                        .padding(5)
                }
            }
            .frame(width: size.width, height: size.height)
            .scaleEffect(pressed ? 0.92 : 1)
        }
        .buttonStyle(PressRecorder(pressed: $pressed))
        .sensoryFeedback(trigger: feedbackTrigger) { _, _ in feedback }
        .accessibilityLabel(item.accessibilityName)
        .accessibilityValue(item.value)
        .accessibilityIdentifier("deck-button-\(item.id)")
    }
}

/// Tracks the finger on a button for the pressed animation.
private struct PressRecorder: ButtonStyle {
    @Binding var pressed: Bool

    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .onChange(of: configuration.isPressed) { _, isPressed in
                withAnimation(.spring(duration: 0.15)) { pressed = isPressed }
            }
    }
}

/// Stand-ins while an Iconify icon loads (or when it cannot be fetched or drawn): common Material Design names map
/// to SF Symbols; others show the title only.
enum DeckIcons {
    private static let map: [String: String] = [
        "microphone": "mic.fill", "microphone-off": "mic.slash.fill", "volume-high": "speaker.wave.3.fill",
        "volume-off": "speaker.slash.fill", "music": "music.note", "play": "play.fill", "pause": "pause.fill",
        "stop": "stop.fill", "skip-next": "forward.end.fill", "skip-previous": "backward.end.fill", "camera": "camera.fill",
        "video": "video.fill", "video-off": "video.slash.fill", "record": "record.circle", "record-rec": "record.circle",
        "broadcast": "dot.radiowaves.left.and.right", "access-point": "dot.radiowaves.left.and.right", "chat": "bubble.left.fill",
        "message": "message.fill", "heart": "heart.fill", "star": "star.fill", "bell": "bell.fill", "timer": "timer",
        "timer-outline": "timer", "clock": "clock.fill", "lightbulb": "lightbulb.fill", "lightbulb-on": "lightbulb.max.fill",
        "gamepad-variant": "gamecontroller.fill", "controller": "gamecontroller.fill", "monitor": "display", "eye": "eye.fill",
        "eye-off": "eye.slash.fill", "account": "person.fill", "account-group": "person.3.fill", "gift": "gift.fill",
        "cash": "dollarsign.circle.fill", "currency-usd": "dollarsign.circle.fill", "trophy": "trophy.fill", "fire": "flame.fill",
        "lightning-bolt": "bolt.fill", "flash": "bolt.fill", "home": "house.fill", "cog": "gearshape.fill", "check": "checkmark",
        "close": "xmark", "plus": "plus", "minus": "minus", "arrow-left": "arrow.left", "arrow-right": "arrow.right",
        "refresh": "arrow.clockwise", "twitch": "tv", "youtube": "play.rectangle.fill", "discord": "bubble.left.and.bubble.right.fill",
        "coffee": "cup.and.saucer.fill", "skull": "exclamationmark.triangle.fill", "emoticon-happy": "face.smiling",
    ]

    static func symbol(for icon: String) -> String? {
        guard !icon.isEmpty else { return nil }
        let name = icon.split(separator: ":").last.map(String.init) ?? icon
        return map[name] ?? map[name.replacingOccurrences(of: "-outline", with: "")]
    }
}
