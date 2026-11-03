import Foundation

/// A color from Cuebot's CSS-style strings ("#fff", "#16161c", "#16161cff", "rgb(1,2,3)"), as 0...1 components.
public struct HexColor: Hashable, Sendable {
    public var red: Double, green: Double, blue: Double, alpha: Double

    public init(red: Double, green: Double, blue: Double, alpha: Double = 1) {
        (self.red, self.green, self.blue, self.alpha) = (red, green, blue, alpha)
    }

    /// True for colors where white text reads better than black (relative luminance below 0.4).
    public var isDark: Bool {
        func linear(_ c: Double) -> Double { c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4) }
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue) < 0.4
    }

    public init?(_ text: String) {
        let s = text.trimmingCharacters(in: .whitespaces).lowercased()
        if s.hasPrefix("rgb") {
            let numbers = s.drop { $0 != "(" }.dropFirst().prefix { $0 != ")" }
                .split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
            guard numbers.count >= 3 else { return nil }
            self.init(red: numbers[0] / 255, green: numbers[1] / 255, blue: numbers[2] / 255,
                      alpha: numbers.count > 3 ? numbers[3] : 1)
            return
        }
        guard s.hasPrefix("#") else { return nil }
        var hex = String(s.dropFirst())
        if hex.count == 3 || hex.count == 4 { hex = hex.map { "\($0)\($0)" }.joined() }
        guard hex.count == 6 || hex.count == 8, let value = UInt64(hex, radix: 16) else { return nil }
        let hasAlpha = hex.count == 8
        let rgb = hasAlpha ? value >> 8 : value
        self.init(red: Double(rgb >> 16 & 0xff) / 255, green: Double(rgb >> 8 & 0xff) / 255, blue: Double(rgb & 0xff) / 255,
                  alpha: hasAlpha ? Double(value & 0xff) / 255 : 1)
    }
}
