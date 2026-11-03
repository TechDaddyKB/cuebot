import SwiftUI

/// Cuebot's palette (the desktop app's Styles/Cuebot.axaml) lives in the asset catalog as colour sets (CuebotGround,
/// CuebotCard, CuebotRaised, CuebotLine, CuebotText, CuebotMuted, CuebotGood/Bad/Warn and the platform colours), each
/// with a light value that matches the iOS system look and a dark value from the desktop app; Xcode generates
/// `Color.cuebotGround` and friends from them.
extension Color {
    /// A platform's brand colour, or the muted grey for others.
    static func platform(_ name: String) -> Color {
        switch name.lowercased() {
        case "twitch": .cuebotTwitch
        case "youtube": .cuebotYouTube
        case "kick": .cuebotKick
        case "rumble": .cuebotRumble
        default: .cuebotMuted
        }
    }
}

/// The Appearance setting: follow the system, or always dark or light.
enum AppAppearance: String, CaseIterable, Identifiable {
    case system, dark, light

    static let storageKey = "appearance"

    var id: String { rawValue }

    var label: String {
        switch self {
        case .system: "System"
        case .dark: "Dark"
        case .light: "Light"
        }
    }

    /// nil follows the system.
    var colorScheme: ColorScheme? {
        switch self {
        case .system: nil
        case .dark: .dark
        case .light: .light
        }
    }
}

extension View {
    /// Cuebot's ground behind a screen, and its bars in the same colour.
    func cuebotScreen() -> some View {
        background(Color.cuebotGround.ignoresSafeArea())
            .toolbarBackground(Color.cuebotGround, for: .navigationBar)
            .toolbarBackground(Color.cuebotGround, for: .tabBar)
    }
}
