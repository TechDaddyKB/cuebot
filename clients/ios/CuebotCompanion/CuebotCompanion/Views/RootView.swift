import CuebotKit
import SwiftUI

struct RootView: View {
    @Environment(PairedCuebots.self) private var paired
    @Environment(ConnectionStore.self) private var connection
    @Environment(DeckStore.self) private var deck
    @Environment(\.scenePhase) private var scenePhase
    @State private var tab = Tab.deck

    enum Tab: Hashable { case deck, dashboard, settings }

    var body: some View {
        TabView(selection: $tab) {
            DeckTab()
                .tabItem { Label("Deck", systemImage: "square.grid.3x3.fill") }
                .tag(Tab.deck)
            DashboardTab()
                .tabItem { Label("Dashboard", systemImage: "gauge.with.dots.needle.50percent") }
                .tag(Tab.dashboard)
            SettingsTab()
                .tabItem { Label("Settings", systemImage: "gearshape") }
                .tag(Tab.settings)
        }
        .task(id: paired.revision) {
            await connection.connect()
            await deck.load(client: connection.client)
        }
        .onChange(of: scenePhase) { _, phase in
            switch phase {
            case .active:
                deck.resume()
                Task { await connection.check() }
            case .background:
                deck.stop()   // no background networking in v1
            default:
                break
            }
        }
    }
}

/// Shown instead of a tab's content when there is nothing to talk to yet, or Cuebot cannot be reached.
struct ConnectionGate<Content: View>: View {
    @Environment(ConnectionStore.self) private var connection
    @ViewBuilder var content: () -> Content

    var body: some View {
        switch connection.state {
        case .unpaired:
            ContentUnavailableView {
                Label("Pair with Cuebot", systemImage: "qrcode.viewfinder")
            } description: {
                Text("In Cuebot on your computer, open Settings → API → Pair a phone, then scan the code from the Settings tab here.")
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
                    Text(verbatim: "Tried \(endpoint.apiBase.absoluteString)").font(.callout.monospaced()).foregroundStyle(.secondary)
                }
                Text(message).font(.callout).foregroundStyle(.secondary)
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
        .background(.yellow.opacity(0.85))
        .foregroundStyle(.black)
    }
}

extension Color {
    /// A Cuebot color string, or `fallback` when it is empty or not understood.
    init(cuebot text: String, fallback: Color) {
        if let c = HexColor(text) { self = Color(.sRGB, red: c.red, green: c.green, blue: c.blue, opacity: c.alpha) } else { self = fallback }
    }
}
