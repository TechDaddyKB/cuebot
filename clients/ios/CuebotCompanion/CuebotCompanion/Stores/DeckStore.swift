import CuebotKit
import Foundation
import Observation

/// The deck shown on the Deck tab: the list of decks, the chosen one's live view, presses, pages and notifications.
@MainActor @Observable
final class DeckStore {
    enum PressResult: Equatable { case ok, failed }

    private static let deckKey = "deckId"
    private let defaults: UserDefaults

    private(set) var decks: [DeckSummary] = []
    private(set) var decksError: String?
    private(set) var view: DeckView?
    private(set) var streamState: StreamState = .connecting
    private(set) var notification: DeckNotification?
    /// The last press result per item id, for the pressed / failed flash.
    private(set) var lastPress: [String: PressResult] = [:]

    var deckId: String? {
        didSet { defaults.set(deckId, forKey: Self.deckKey) }
    }

    private var client: CuebotClient?
    private var streamTask: Task<Void, Never>?
    private var notificationTask: Task<Void, Never>?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        deckId = defaults.string(forKey: Self.deckKey)
    }

    var isReconnecting: Bool {
        if case .reconnecting = streamState { return true }
        return false
    }

    /// Loads the deck list from the active Cuebot and opens the chosen deck (the only one, if there is just one).
    func load(client: CuebotClient?) async {
        self.client = client
        guard let client else {
            stop()
            decks = []
            return
        }
        do {
            decks = try await client.decks().filter(\.enabled)
            decksError = decks.isEmpty ? "Cuebot has no enabled decks. Make one in Cuebot under Input > Decks." : nil
        } catch {
            decks = []
            decksError = ConnectionStore.describe(error)
        }
        if let id = deckId, decks.contains(where: { $0.id == id }) {
            open(id)
        } else if decks.count == 1 {
            open(decks[0].id)
        } else {
            stop()
        }
    }

    func open(_ id: String) {
        guard let client else { return }
        if deckId == id, streamTask != nil { return }
        stop()
        deckId = id
        view = nil
        streamTask = Task { [weak self] in
            let events = client.deckEvents(id) { state in Task { @MainActor in self?.streamState = state } }
            do {
                for try await event in events {
                    guard let self else { return }
                    switch event.type {
                    case "state": if let view = try? event.decode(DeckView.self) { self.view = view }
                    case "notify": if let note = try? event.decode(DeckNotification.self) { self.show(note) }
                    default: break
                    }
                }
            } catch {
                self?.streamState = .reconnecting(retryIn: 0, error: ConnectionStore.describe(error as? CuebotError ?? .unreachable("\(error)")))
            }
        }
    }

    /// Closes the live stream (when the app goes to the background or another Cuebot is chosen).
    func stop() {
        streamTask?.cancel()
        streamTask = nil
    }

    /// Reopens the live stream after coming back to the foreground.
    func resume() {
        guard streamTask == nil, let id = deckId else { return }
        open(id)
    }

    @discardableResult
    func press(_ item: DeckItem) async -> PressResult {
        guard let client, let deck = view?.id ?? deckId else { return .failed }
        let result: PressResult
        do {
            try await client.press(deck: deck, item: item.id)
            result = .ok
        } catch {
            result = .failed
        }
        lastPress[item.id] = result
        return result
    }

    func page(_ delta: Int) async {
        guard let client, let deck = view?.id ?? deckId, let view, view.pageCount > 1 else { return }
        try? await client.page(deck: deck, delta: delta)
    }

    func dismissNotification() { notification = nil }

    private func show(_ note: DeckNotification) {
        notification = note
        notificationTask?.cancel()
        notificationTask = Task { [weak self] in
            try? await Task.sleep(for: .seconds(4))
            if !Task.isCancelled { self?.notification = nil }
        }
    }
}
