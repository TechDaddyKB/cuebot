import CuebotKit
import SwiftUI

/// Draws an `SVGIcon` in one color, scaled to fit and centered.
struct SVGIconView: View {
    let icon: SVGIcon
    let color: Color

    var body: some View {
        Canvas { context, size in
            let scale = min(size.width / icon.width, size.height / icon.height)
            let dx = (size.width - icon.width * scale) / 2 - icon.minX * scale
            let dy = (size.height - icon.height * scale) / 2 - icon.minY * scale
            func p(_ point: SVGIcon.Point) -> CGPoint { CGPoint(x: point.x * scale + dx, y: point.y * scale + dy) }
            for shape in icon.shapes {
                var path = Path()
                for segment in shape.segments {
                    switch segment {
                    case .move(let a): path.move(to: p(a))
                    case .line(let a): path.addLine(to: p(a))
                    case .cubic(let a, let b, let c): path.addCurve(to: p(c), control1: p(a), control2: p(b))
                    case .quad(let a, let b): path.addQuadCurve(to: p(b), control: p(a))
                    case .close: path.closeSubpath()
                    }
                }
                if shape.fill { context.fill(path, with: .color(color), style: FillStyle(eoFill: shape.evenOdd)) }
                if let width = shape.strokeWidth {
                    context.stroke(path, with: .color(color), style: StrokeStyle(lineWidth: width * scale,
                                                                                  lineCap: shape.roundCaps ? .round : .butt,
                                                                                  lineJoin: shape.roundJoins ? .round : .miter))
                }
            }
        }
        .accessibilityHidden(true)
    }
}

/// A deck button's icon: the Iconify icon when it can be drawn, an SF Symbol stand-in while it loads or when it
/// cannot, or nothing.
struct DeckIconView: View {
    let name: String
    let color: Color
    let size: CGFloat
    @State private var icon: SVGIcon?

    var body: some View {
        Group {
            if let icon {
                SVGIconView(icon: icon, color: color).frame(width: size, height: size)
            } else if let symbol = DeckIcons.symbol(for: name) {
                Image(systemName: symbol).font(.system(size: size * 0.85)).foregroundStyle(color)
            }
        }
        .task(id: name) { icon = await IconCache.shared.icon(name) }
    }
}

/// An image Cuebot serves (a button's iconUrl or backgroundImage), relative links resolved against its HTTP server.
struct DeckRemoteImage: View {
    let link: String
    let base: URL?
    var fill = false

    var body: some View {
        if let url = DeckImages.url(link, base: base) {
            AsyncImage(url: url) { phase in
                if let image = phase.image {
                    image.resizable().aspectRatio(contentMode: fill ? .fill : .fit)
                }
            }
        }
    }
}

enum DeckImages {
    /// Absolute http(s) links as they are; "/path" and "path" against Cuebot's HTTP server; anything else nil.
    static func url(_ link: String, base: URL?) -> URL? {
        let text = link.trimmingCharacters(in: .whitespaces)
        guard !text.isEmpty else { return nil }
        if let url = URL(string: text), let scheme = url.scheme?.lowercased() { return scheme == "http" || scheme == "https" ? url : nil }
        guard let base else { return nil }
        return URL(string: text, relativeTo: base)?.absoluteURL
    }
}
