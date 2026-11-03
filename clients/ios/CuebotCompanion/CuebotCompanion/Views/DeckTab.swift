import CuebotKit
import SwiftUI

struct DeckTab: View {
    @Environment(DeckStore.self) private var deck
    @Environment(ConnectionStore.self) private var connection
    @State private var streamMode = false
    @Environment(\.colorScheme) private var systemScheme

    /// Light or dark to match the deck's own background, so titles and bars stay readable on it.
    private var deckScheme: ColorScheme? {
        guard let view = deck.view, let color = HexColor(view.background) else { return nil }
        return color.isDark ? .dark : .light
    }

    var body: some View {
        NavigationStack {
            ConnectionGate {
                content
            }
            .navigationTitle(deck.view?.name.isEmpty == false ? deck.view!.name : "Deck")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar { if !streamMode { toolbar } }
            .toolbar(streamMode ? .hidden : .automatic, for: .navigationBar, .tabBar)
            .toolbarColorScheme(deckScheme, for: .navigationBar, .tabBar)
        }
        .environment(\.colorScheme, deckScheme ?? systemScheme)
        .statusBarHidden(streamMode)
        .persistentSystemOverlays(streamMode ? .hidden : .automatic)
        .onChange(of: streamMode) { _, on in UIApplication.shared.isIdleTimerDisabled = on }
    }

    @ViewBuilder private var content: some View {
        if let view = deck.view {
            VStack(spacing: 0) {
                if deck.isReconnecting { ReconnectingBar() }
                DeckGridView(view: view, streamMode: streamMode)
                    .gesture(DragGesture(minimumDistance: 40).onEnded { drag in
                        guard abs(drag.translation.width) > abs(drag.translation.height) else { return }
                        Task { await deck.page(drag.translation.width < 0 ? 1 : -1) }
                    })
                if !streamMode && view.pageCount > 1 { PageBar(view: view) }
            }
            .background(Color(cuebot: view.background, fallback: .cuebotGround).ignoresSafeArea())
            .overlay(alignment: .top) { NotificationBanner() }
            .overlay(alignment: .topTrailing) { if streamMode { StreamModeExit(streamMode: $streamMode) } }
        } else if let error = deck.decksError {
            ContentUnavailableView("No deck", systemImage: "square.grid.3x3", description: Text(error))
        } else if deck.decks.count > 1 && deck.deckId == nil {
            DeckChooser()
        } else {
            ProgressView("Loading the deck…")
        }
    }

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        ToolbarItem(placement: .topBarLeading) {
            if deck.decks.count > 1 {
                Menu {
                    ForEach(deck.decks) { summary in
                        Button { deck.open(summary.id) } label: {
                            if summary.id == deck.deckId { Label(summary.name, systemImage: "checkmark") } else { Text(summary.name) }
                        }
                    }
                } label: {
                    Label("Choose deck", systemImage: "square.stack")
                }
            }
        }
        ToolbarItem(placement: .topBarTrailing) {
            Button { streamMode = true } label: { Label("Stream mode", systemImage: "rectangle.inset.filled") }
                .disabled(deck.view == nil)
                .accessibilityHint("Hides everything but the deck and keeps the screen on. Press and hold the top-right corner to leave.")
        }
    }
}

struct DeckChooser: View {
    @Environment(DeckStore.self) private var deck

    var body: some View {
        List(deck.decks) { summary in
            Button { deck.open(summary.id) } label: {
                VStack(alignment: .leading) {
                    Text(summary.name).font(.headline)
                    Text("\(summary.columns) × \(summary.rows)" + (summary.pageCount > 1 ? ", \(summary.pageCount) pages" : ""))
                        .font(.subheadline).foregroundStyle(Color.cuebotMuted)
                }
            }
        }
        .navigationTitle("Choose a deck")
    }
}

struct PageBar: View {
    @Environment(DeckStore.self) private var deck
    let view: DeckView

    var body: some View {
        HStack {
            Button { Task { await deck.page(-1) } } label: { Image(systemName: "chevron.left") }
                .accessibilityLabel("Previous page")
            Spacer()
            Text(view.pageName.isEmpty ? "Page \(view.page + 1) of \(view.pageCount)" : "\(view.pageName) · \(view.page + 1)/\(view.pageCount)")
                .font(.footnote.monospacedDigit())
            Spacer()
            Button { Task { await deck.page(1) } } label: { Image(systemName: "chevron.right") }
                .accessibilityLabel("Next page")
        }
        .padding(.horizontal, 24)
        .padding(.vertical, 10)
        .background(.bar)
    }
}

/// Press and hold the top-right corner for a second to leave stream mode.
struct StreamModeExit: View {
    @Binding var streamMode: Bool

    var body: some View {
        Color.clear
            .frame(width: 64, height: 64)
            .contentShape(Rectangle())
            .onLongPressGesture(minimumDuration: 1) { streamMode = false }
            .accessibilityLabel("Leave stream mode")
            .accessibilityAddTraits(.isButton)
            .accessibilityAction { streamMode = false }
    }
}

struct NotificationBanner: View {
    @Environment(DeckStore.self) private var deck

    var body: some View {
        if let note = deck.notification {
            VStack(alignment: .leading, spacing: 2) {
                Text(note.title).font(.headline)
                if !note.description.isEmpty { Text(note.description).font(.subheadline) }
            }
            .padding(12)
            .frame(maxWidth: 480, alignment: .leading)
            .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 12))
            .padding()
            .onTapGesture { deck.dismissNotification() }
            .transition(.move(edge: .top).combined(with: .opacity))
            .accessibilityAddTraits(.isStaticText)
        }
    }
}
