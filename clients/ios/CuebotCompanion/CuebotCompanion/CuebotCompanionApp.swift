import CuebotKit
import SwiftUI

@main
struct CuebotCompanionApp: App {
    @State private var paired: PairedCuebots
    @State private var connection: ConnectionStore
    @State private var deck = DeckStore()
    @State private var pairingError: String?

    init() {
        let paired = PairedCuebots()
        _paired = State(initialValue: paired)
        _connection = State(initialValue: ConnectionStore(paired: paired))
        UITestSupport.applyLaunchArguments(to: paired)
    }

    var body: some Scene {
        WindowGroup {
            RootView()
                .environment(paired)
                .environment(connection)
                .environment(deck)
                .onOpenURL { url in
                    do {
                        paired.pair(try PairingLink.parse(url.absoluteString))
                    } catch {
                        pairingError = PairingView.describe(error)
                    }
                }
                .alert("Could not pair", isPresented: Binding(get: { pairingError != nil }, set: { if !$0 { pairingError = nil } })) {
                    Button("OK", role: .cancel) {}
                } message: {
                    Text(pairingError ?? "")
                }
        }
    }
}

/// Launch arguments the UI tests use: `-resetPairing` starts with nothing paired; `-pair <link>` pairs first.
enum UITestSupport {
    @MainActor static func applyLaunchArguments(to paired: PairedCuebots) {
        let args = ProcessInfo.processInfo.arguments
        if args.contains("-resetPairing") { for entry in paired.all { paired.remove(entry.id) } }
        if let index = args.firstIndex(of: "-pair"), index + 1 < args.count, let endpoint = try? PairingLink.parse(args[index + 1]) {
            paired.pair(endpoint)
        }
    }
}
