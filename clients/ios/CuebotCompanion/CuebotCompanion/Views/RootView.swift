import CuebotKit
import SwiftUI

struct RootView: View {
    @Environment(PairedCuebots.self) private var paired
    @Environment(ConnectionStore.self) private var connection
    @Environment(DeckStore.self) private var deck
    @Environment(DashboardStore.self) private var dashboard
    @Environment(ChatStore.self) private var chat
    @Environment(ProfileStore.self) private var profiles
    @Environment(QuickActionsStore.self) private var quickActions
    @Environment(\.scenePhase) private var scenePhase
    @State private var tab = UITestSupport.initialTab ?? .deck
    @State private var menu = SideMenu()
    @AppStorage(SideMenu.pinnedKey) private var keepMenuOpen = false
    @Environment(\.horizontalSizeClass) private var sizeClass

    enum Tab: Hashable, CaseIterable {
        case deck, dashboard, chat, mods, settings

        var title: String {
            switch self {
            case .deck: "Deck"
            case .dashboard: "Dashboard"
            case .chat: "Chat"
            case .mods: "Mods"
            case .settings: "Settings"
            }
        }

        var symbol: String {
            switch self {
            case .deck: "square.grid.3x3.fill"
            case .dashboard: "gauge.with.dots.needle.50percent"
            case .chat: "bubble.left.and.bubble.right"
            case .mods: "person.2"
            case .settings: "gearshape"
            }
        }
    }

    /// "Keep menu open" pins the menu beside the page where there is room for it (iPad); phones use the hamburger.
    private var pinned: Bool { keepMenuOpen && sizeClass == .regular && !menu.hidden }

    var body: some View {
        HStack(spacing: 0) {
            if pinned {
                SideMenuList(selection: $tab).frame(width: 260)
                Divider().ignoresSafeArea()
            }
            section
                .environment(\.sideMenuPinned, pinned)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .overlay(alignment: .leading) {
            if menu.isOpen && !pinned && !menu.hidden {
                ZStack(alignment: .leading) {
                    Color.black.opacity(0.35).ignoresSafeArea()
                        .onTapGesture { menu.close() }
                        .accessibilityLabel("Close menu")
                        .accessibilityAddTraits(.isButton)
                        .accessibilityAction { menu.close() }
                    SideMenuList(selection: $tab)
                        .frame(width: 280)
                        .transition(.move(edge: .leading))
                }
                .accessibilityAction(.escape) { menu.close() }
            }
        }
        .environment(menu)
        // Again after a profile switch: the deck and dashboard belong to the profile that is open.
        .task(id: [paired.revision, profiles.switchCount]) {
            await connection.connect()
            quickActions.load(cuebotId: paired.activeId, client: connection.client)
            async let decks: Void = deck.load(client: connection.client)
            async let board: Void = dashboard.load(client: connection.client)
            async let personas: Void = profiles.load(client: connection.client)
            async let chatLines: Void = chat.load(client: connection.client)
            _ = await (decks, board, personas, chatLines)
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                deck.resume()
                dashboard.resume()
                chat.resume()
                Task { await profiles.refresh() }
                Task { await connection.check() }
            case .background:
                deck.stop()   // no background networking in v1
                dashboard.stop()
                chat.stop()
            default:
                break
            }
        }
    }
}

extension RootView {
    /// The section on screen. Only that one is built: the stores keep each section's data while it is away.
    @ViewBuilder fileprivate var section: some View {
        switch tab {
        case .deck: DeckTab()
        case .dashboard: DashboardTab()
        case .chat: ChatTab()
        case .mods: ModsTab()
        case .settings: SettingsTab()
        }
    }
}

/// Shown instead of a tab's content when there is nothing to talk to yet, or Cuebot cannot be reached.
struct ConnectionGate<Content: View>: View {
    @Environment(ConnectionStore.self) private var connection
    @ViewBuilder var content: () -> Content

    var body: some View {
        Group { gate }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(Color.cuebotGround.ignoresSafeArea())
    }

    @ViewBuilder private var gate: some View {
        switch connection.state {
        case .unpaired:
            ContentUnavailableView {
                Label("Pair with Cuebot", systemImage: "qrcode.viewfinder")
            } description: {
                Text("Cuebot Companion is a remote for Cuebot, the free stream automation app for your computer.\n\nIn Cuebot, open the Servers page and tick \"Show pairing code\" under \"Pair a phone\" in the REST API card. Then scan the code from the Settings tab here.")
            } actions: {
                Link("Try a demo Cuebot", destination: URL(string: "https://cuebot-test.techdaddydigital.com/")!)
                    .buttonStyle(.borderedProminent)
                    .accessibilityIdentifier("gate-demo")
                Link("Get Cuebot", destination: URL(string: "https://techdaddydigital.com/cuebot/")!)
                    .accessibilityIdentifier("gate-get-cuebot")
            }
        case .unauthorized:
            ContentUnavailableView {
                Label("Pair again", systemImage: "key.slash")
            } description: {
                Text("Cuebot did not accept this phone's token (it may have been changed). Scan the pairing code again in Settings.")
            }
        case .unreachable(let message):
            UnreachableCard(message: message)
        case .checking, .online:
            content()
        }
    }
}

struct UnreachableCard: View {
    @Environment(ConnectionStore.self) private var connection
    let message: String

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 16) {
                Label("Can't reach Cuebot", systemImage: "wifi.exclamationmark").font(.title2.bold())
                if let endpoint = connection.endpoint {
                    Text(verbatim: "Tried \(endpoint.apiBase.absoluteString)").font(.callout.monospaced()).foregroundStyle(Color.cuebotMuted)
                }
                Text(message).font(.callout).foregroundStyle(Color.cuebotMuted)
                VStack(alignment: .leading, spacing: 8) {
                    Label("Is Cuebot running on your computer?", systemImage: "desktopcomputer")
                    Label("Is \"Allow LAN\" on in Cuebot (Servers page)?", systemImage: "network")
                    Label("Are the phone and the computer on the same Wi-Fi?", systemImage: "wifi")
                    Label("Did iOS block Local Network access for Cuebot Companion?", systemImage: "hand.raised")
                }
                HStack {
                    Button("Retry") { Task { await connection.check() } }.buttonStyle(.borderedProminent)
                    Button("Open Settings") {
                        if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                    }
                    .buttonStyle(.bordered)
                }
            }
            .padding(24)
            .frame(maxWidth: 560, alignment: .leading)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color.cuebotGround.ignoresSafeArea())
    }
}

/// The thin "Reconnecting…" bar over a live view.
struct ReconnectingBar: View {
    var body: some View {
        HStack(spacing: 8) {
            ProgressView().controlSize(.small)
            Text("Reconnecting…").font(.footnote.weight(.medium))
        }
        .padding(.vertical, 4)
        .frame(maxWidth: .infinity)
        .background(Color.cuebotWarn)
        .foregroundStyle(.black)   // black on Cuebot's amber reads in light and dark
    }
}

extension Color {
    /// A Cuebot color string, or `fallback` when it is empty or not understood.
    init(cuebot text: String, fallback: Color) {
        if let c = HexColor(text) { self = Color(.sRGB, red: c.red, green: c.green, blue: c.blue, opacity: c.alpha) } else { self = fallback }
    }
}
