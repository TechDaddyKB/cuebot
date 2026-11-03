import Foundation

/// A small SVG reader for icons such as the Iconify sets deck buttons name (`mdi:microphone`): `<path>`, `<circle>`,
/// `<ellipse>`, `<rect>`, `<line>`, `<polyline>`, `<polygon>` and `<g>` (whose fill and stroke settings are inherited).
/// Every shape becomes absolute move/line/curve segments in viewBox units, so the app can draw it in one color at any
/// size. Colors are ignored on purpose: the deck says which color the icon has. Gradients, masks, text and
/// transforms other than none are not supported (such icons fall back to the title).
public struct SVGIcon: Hashable, Sendable {
    public enum Segment: Hashable, Sendable {
        case move(Point)
        case line(Point)
        case cubic(Point, Point, Point)
        case quad(Point, Point)
        case close
    }

    public struct Point: Hashable, Sendable {
        public var x: Double, y: Double
        public init(_ x: Double, _ y: Double) { (self.x, self.y) = (x, y) }
    }

    /// One shape: filled, stroked, or both.
    public struct Shape: Hashable, Sendable {
        public var segments: [Segment]
        public var fill: Bool
        public var evenOdd: Bool
        public var strokeWidth: Double?
        public var roundCaps: Bool
        public var roundJoins: Bool
    }

    public var minX = 0.0, minY = 0.0, width = 24.0, height = 24.0
    public var shapes: [Shape] = []

    public init?(svg: String) {
        let reader = Reader()
        let parser = XMLParser(data: Data(svg.utf8))
        parser.delegate = reader
        guard parser.parse(), let box = reader.viewBox, box.2 > 0, box.3 > 0, !reader.shapes.isEmpty else { return nil }
        (minX, minY, width, height) = box
        shapes = reader.shapes
    }

    // MARK: XML

    private final class Reader: NSObject, XMLParserDelegate {
        struct Style {
            var fill: String? = nil
            var stroke: String? = nil
            var strokeWidth: Double? = nil
            var fillRule: String? = nil
            var lineCap: String? = nil
            var lineJoin: String? = nil
        }

        var viewBox: (Double, Double, Double, Double)?
        var shapes: [Shape] = []
        private var styles: [Style] = [Style()]
        private var unsupportedDepth = 0

        func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?,
                    attributes a: [String: String] = [:]) {
            if unsupportedDepth > 0 || ["defs", "mask", "clipPath", "linearGradient", "radialGradient", "text", "style"].contains(name) {
                unsupportedDepth += 1
                return
            }
            var style = styles.last!
            if let v = a["fill"] { style.fill = v }
            if let v = a["stroke"] { style.stroke = v }
            if let v = a["stroke-width"].flatMap(Double.init) { style.strokeWidth = v }
            if let v = a["fill-rule"] { style.fillRule = v }
            if let v = a["stroke-linecap"] { style.lineCap = v }
            if let v = a["stroke-linejoin"] { style.lineJoin = v }
            styles.append(style)
            func n(_ key: String) -> Double { Double(a[key] ?? "") ?? 0 }
            var segments: [Segment]?
            switch name {
            case "svg":
                if let box = a["viewBox"].map(PathData.numbers), box.count == 4 { viewBox = (box[0], box[1], box[2], box[3]) }
                else if let w = Double(a["width"] ?? ""), let h = Double(a["height"] ?? "") { viewBox = (0, 0, w, h) }
            case "path": segments = PathData.parse(a["d"] ?? "")
            case "circle": segments = Self.ellipse(cx: n("cx"), cy: n("cy"), rx: n("r"), ry: n("r"))
            case "ellipse": segments = Self.ellipse(cx: n("cx"), cy: n("cy"), rx: n("rx"), ry: n("ry"))
            case "rect": segments = Self.rect(x: n("x"), y: n("y"), w: n("width"), h: n("height"), rx: a["rx"].flatMap(Double.init) ?? a["ry"].flatMap(Double.init) ?? 0)
            case "line": segments = [.move(Point(n("x1"), n("y1"))), .line(Point(n("x2"), n("y2")))]
            case "polyline", "polygon":
                let v = PathData.numbers(a["points"] ?? "")
                var points: [Segment] = stride(from: 0, to: v.count - 1, by: 2).map { .line(Point(v[$0], v[$0 + 1])) }
                if case .line(let first)? = points.first { points[0] = .move(first) }
                if name == "polygon", !points.isEmpty { points.append(.close) }
                segments = points
            default: break
            }
            guard let segments, !segments.isEmpty else { return }
            let fill = (style.fill ?? "currentColor") != "none"   // SVG fills black when nothing is said
            let stroke = style.stroke.map { $0 != "none" } ?? false
            guard fill || stroke else { return }
            shapes.append(Shape(segments: segments, fill: fill, evenOdd: style.fillRule == "evenodd",
                                strokeWidth: stroke ? (style.strokeWidth ?? 1) : nil,
                                roundCaps: style.lineCap == "round", roundJoins: style.lineJoin == "round"))
        }

        func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
            if unsupportedDepth > 0 {
                unsupportedDepth -= 1
                return
            }
            if styles.count > 1 { styles.removeLast() }
        }

        static func ellipse(cx: Double, cy: Double, rx: Double, ry: Double) -> [Segment] {
            guard rx > 0, ry > 0 else { return [] }
            let k = 0.5522847498   // control point distance for a quarter circle
            return [
                .move(Point(cx + rx, cy)),
                .cubic(Point(cx + rx, cy + ry * k), Point(cx + rx * k, cy + ry), Point(cx, cy + ry)),
                .cubic(Point(cx - rx * k, cy + ry), Point(cx - rx, cy + ry * k), Point(cx - rx, cy)),
                .cubic(Point(cx - rx, cy - ry * k), Point(cx - rx * k, cy - ry), Point(cx, cy - ry)),
                .cubic(Point(cx + rx * k, cy - ry), Point(cx + rx, cy - ry * k), Point(cx + rx, cy)),
                .close,
            ]
        }

        static func rect(x: Double, y: Double, w: Double, h: Double, rx: Double) -> [Segment] {
            guard w > 0, h > 0 else { return [] }
            let r = min(rx, w / 2, h / 2)
            guard r > 0 else {
                return [.move(Point(x, y)), .line(Point(x + w, y)), .line(Point(x + w, y + h)), .line(Point(x, y + h)), .close]
            }
            let k = r * 0.5522847498
            return [
                .move(Point(x + r, y)), .line(Point(x + w - r, y)),
                .cubic(Point(x + w - r + k, y), Point(x + w, y + r - k), Point(x + w, y + r)), .line(Point(x + w, y + h - r)),
                .cubic(Point(x + w, y + h - r + k), Point(x + w - r + k, y + h), Point(x + w - r, y + h)), .line(Point(x + r, y + h)),
                .cubic(Point(x + r - k, y + h), Point(x, y + h - r + k), Point(x, y + h - r)), .line(Point(x, y + r)),
                .cubic(Point(x, y + r - k), Point(x + r - k, y), Point(x + r, y)), .close,
            ]
        }
    }
}

/// SVG path data (the `d` attribute): every command, relative or absolute, made absolute; arcs become cubic curves.
enum PathData {
    /// Numbers in a list such as a viewBox or polygon points (commas, spaces, signs and "1.5.5" all separate).
    static func numbers(_ text: String) -> [Double] {
        var scanner = Scanner(chars: Array(text.utf8))
        var result: [Double] = []
        while let n = scanner.number() { result.append(n) }
        return result
    }

    static func parse(_ d: String) -> [SVGIcon.Segment] {
        typealias P = SVGIcon.Point
        var s = Scanner(chars: Array(d.utf8))
        var out: [SVGIcon.Segment] = []
        var current = P(0, 0), start = P(0, 0)
        var lastControl: P?          // for S/s (cubic) reflection
        var lastQuadControl: P?      // for T/t reflection
        var command: UInt8 = 0
        while true {
            s.skipSeparators()
            guard let c = s.peek() else { break }
            if (c >= 65 && c <= 90) || (c >= 97 && c <= 122) {
                command = c
                s.advance()
            } else if command == 0 {
                return out   // data before any command: invalid
            }
            let relative = command >= 97
            func pt(_ x: Double, _ y: Double) -> P { relative ? P(current.x + x, current.y + y) : P(x, y) }
            var cubicControl: P?, quadControl: P?
            switch command | 0x20 {   // lowercase
            case UInt8(ascii: "m"):
                guard let x = s.number(), let y = s.number() else { return out }
                current = pt(x, y)
                start = current
                out.append(.move(current))
                command = relative ? UInt8(ascii: "l") : UInt8(ascii: "L")   // further pairs are line-tos
            case UInt8(ascii: "l"):
                guard let x = s.number(), let y = s.number() else { return out }
                current = pt(x, y)
                out.append(.line(current))
            case UInt8(ascii: "h"):
                guard let x = s.number() else { return out }
                current = P(relative ? current.x + x : x, current.y)
                out.append(.line(current))
            case UInt8(ascii: "v"):
                guard let y = s.number() else { return out }
                current = P(current.x, relative ? current.y + y : y)
                out.append(.line(current))
            case UInt8(ascii: "c"):
                guard let x1 = s.number(), let y1 = s.number(), let x2 = s.number(), let y2 = s.number(),
                      let x = s.number(), let y = s.number() else { return out }
                let c1 = pt(x1, y1), c2 = pt(x2, y2), end = pt(x, y)
                out.append(.cubic(c1, c2, end))
                cubicControl = c2
                current = end
            case UInt8(ascii: "s"):
                guard let x2 = s.number(), let y2 = s.number(), let x = s.number(), let y = s.number() else { return out }
                let c1 = lastControl.map { P(2 * current.x - $0.x, 2 * current.y - $0.y) } ?? current
                let c2 = pt(x2, y2), end = pt(x, y)
                out.append(.cubic(c1, c2, end))
                cubicControl = c2
                current = end
            case UInt8(ascii: "q"):
                guard let x1 = s.number(), let y1 = s.number(), let x = s.number(), let y = s.number() else { return out }
                let c = pt(x1, y1), end = pt(x, y)
                out.append(.quad(c, end))
                quadControl = c
                current = end
            case UInt8(ascii: "t"):
                guard let x = s.number(), let y = s.number() else { return out }
                let c = lastQuadControl.map { P(2 * current.x - $0.x, 2 * current.y - $0.y) } ?? current
                let end = pt(x, y)
                out.append(.quad(c, end))
                quadControl = c
                current = end
            case UInt8(ascii: "a"):
                guard let rx = s.number(), let ry = s.number(), let rotation = s.number(),
                      let large = s.flag(), let sweep = s.flag(), let x = s.number(), let y = s.number() else { return out }
                let end = pt(x, y)
                out += arc(from: current, to: end, rx: rx, ry: ry, rotation: rotation, large: large, sweep: sweep)
                current = end
            case UInt8(ascii: "z"):
                out.append(.close)
                current = start
            default:
                return out   // unknown command
            }
            lastControl = cubicControl
            lastQuadControl = quadControl
        }
        return out
    }

    /// An elliptical arc (SVG endpoint form) as cubic Béziers, per the SVG spec's conversion to center form.
    static func arc(from p0: SVGIcon.Point, to p1: SVGIcon.Point, rx rxIn: Double, ry ryIn: Double, rotation: Double,
                    large: Bool, sweep: Bool) -> [SVGIcon.Segment] {
        typealias P = SVGIcon.Point
        if p0 == p1 { return [] }
        var rx = abs(rxIn), ry = abs(ryIn)
        if rx == 0 || ry == 0 { return [.line(p1)] }
        let phi = rotation * .pi / 180, cosPhi = cos(phi), sinPhi = sin(phi)
        let dx = (p0.x - p1.x) / 2, dy = (p0.y - p1.y) / 2
        let x1p = cosPhi * dx + sinPhi * dy, y1p = -sinPhi * dx + cosPhi * dy
        let lambda = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry)
        if lambda > 1 {
            rx *= lambda.squareRoot()
            ry *= lambda.squareRoot()
        }
        let num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        let den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        var coef = (max(0, num) / den).squareRoot()
        if large == sweep { coef = -coef }
        let cxp = coef * rx * y1p / ry, cyp = -coef * ry * x1p / rx
        let cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2
        let cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2
        func angle(_ ux: Double, _ uy: Double, _ vx: Double, _ vy: Double) -> Double {
            let a = atan2(ux * vy - uy * vx, ux * vx + uy * vy)
            return a
        }
        let theta1 = angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry)
        var delta = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
        if !sweep && delta > 0 { delta -= 2 * .pi } else if sweep && delta < 0 { delta += 2 * .pi }
        let pieces = max(1, Int((abs(delta) / (.pi / 2)).rounded(.up)))
        let step = delta / Double(pieces)
        let t = 4.0 / 3.0 * tan(step / 4)
        func point(_ angle: Double) -> P {
            let ex = rx * cos(angle), ey = ry * sin(angle)
            return P(cx + cosPhi * ex - sinPhi * ey, cy + sinPhi * ex + cosPhi * ey)
        }
        func derivative(_ angle: Double) -> P {
            let ex = -rx * sin(angle), ey = ry * cos(angle)
            return P(cosPhi * ex - sinPhi * ey, sinPhi * ex + cosPhi * ey)
        }
        var segments: [SVGIcon.Segment] = []
        var a = theta1
        for i in 0..<pieces {
            let b = a + step
            let s = point(a), d1 = derivative(a), d2 = derivative(b)
            let e = i == pieces - 1 ? p1 : point(b)
            segments.append(.cubic(P(s.x + t * d1.x, s.y + t * d1.y), P(e.x - t * d2.x, e.y - t * d2.y), e))
            a = b
        }
        return segments
    }

    /// Reads numbers and flags out of path data the way SVG allows: "1-2" is two numbers, ".5.5" is two numbers,
    /// and arc flags may be written without separators ("a1 1 0 011 1").
    struct Scanner {
        let chars: [UInt8]
        var i = 0

        init(chars: [UInt8]) { self.chars = chars }

        func peek() -> UInt8? { i < chars.count ? chars[i] : nil }
        mutating func advance() { i += 1 }

        mutating func skipSeparators() {
            while i < chars.count, chars[i] == 32 || chars[i] == 44 || chars[i] == 9 || chars[i] == 10 || chars[i] == 13 { i += 1 }
        }

        mutating func flag() -> Bool? {
            skipSeparators()
            guard let c = peek(), c == 48 || c == 49 else { return nil }
            advance()
            return c == 49
        }

        mutating func number() -> Double? {
            skipSeparators()
            let begin = i
            if let c = peek(), c == 43 || c == 45 { advance() }
            var digits = false, dot = false
            while let c = peek() {
                if c >= 48 && c <= 57 { digits = true; advance() }
                else if c == 46 && !dot { dot = true; advance() }
                else { break }
            }
            if digits, let c = peek(), c == 101 || c == 69 {   // exponent
                let mark = i
                advance()
                if let s = peek(), s == 43 || s == 45 { advance() }
                var expDigits = false
                while let d = peek(), d >= 48 && d <= 57 { expDigits = true; advance() }
                if !expDigits { i = mark }
            }
            guard digits else {
                i = begin
                return nil
            }
            return Double(String(decoding: chars[begin..<i], as: UTF8.self))
        }
    }
}

/// Iconify icon names as decks use them ("mdi:microphone") and where the SVG comes from.
public enum Iconify {
    /// `https://api.iconify.design/<prefix>/<name>.svg`, or nil when `icon` is not a "prefix:name" Iconify name.
    public static func url(for icon: String) -> URL? {
        let parts = icon.trimmingCharacters(in: .whitespaces).split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2, parts.allSatisfy({ !$0.isEmpty && $0.allSatisfy { $0.isLetter || $0.isNumber || $0 == "-" } }) else { return nil }
        return URL(string: "https://api.iconify.design/\(parts[0].lowercased())/\(parts[1].lowercased()).svg")
    }
}
