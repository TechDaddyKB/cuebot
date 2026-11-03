import CuebotKit
import Foundation
import Observation

/// Actions pinned to the Dashboard, per paired Cuebot, and which of them ask before running.
@MainActor @Observable
final class QuickActionsStore {
    struct Pinned: Codable, Hashable, Identifiable {
        var id: String
        var name: String
        var askFirst: Bool
    }

    enum RunResult: Equatable { case started, failed(String) }

    private let defaults: UserDefaults
    private(set) var cuebotId: String?
    private(set) var pinned: [Pinned] = []
    private(set) var available: [ActionSummary] = []
    private(set) var loadError: String?
    private var client: CuebotClient?

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
    }

    private func key(_ id: String) -> String { "quickActions.\(id)" }

    func load(cuebotId: String?, client: CuebotClient?) {
        self.cuebotId = cuebotId
        self.client = client
        pinned = cuebotId.flatMap { defaults.data(forKey: key($0)) }.flatMap { try? JSONDecoder().decode([Pinned].self, from: $0) } ?? []
        available = []
    }

    /// The Cuebot's actions, for choosing which to pin.
    func fetchActions() async {
        guard let client else { return }
        do {
            available = try await client.actions().sorted { ($0.group, $0.name) < ($1.group, $1.name) }
            loadError = nil
            // Keep pinned names current (an action renamed in Cuebot keeps its id).
            for index in pinned.indices {
                if let action = available.first(where: { $0.id == pinned[index].id }) { pinned[index].name = action.name }
            }
            save()
        } catch {
            loadError = ConnectionStore.describe(error)
        }
    }

    func isPinned(_ id: String) -> Bool { pinned.contains { $0.id == id } }

    func toggle(_ action: ActionSummary) {
        if let index = pinned.firstIndex(where: { $0.id == action.id }) { pinned.remove(at: index) }
        else { pinned.append(Pinned(id: action.id, name: action.name, askFirst: false)) }
        save()
    }

    func setAskFirst(_ id: String, _ askFirst: Bool) {
        guard let index = pinned.firstIndex(where: { $0.id == id }) else { return }
        pinned[index].askFirst = askFirst
        save()
    }

    func move(from source: IndexSet, to destination: Int) {
        pinned.move(fromOffsets: source, toOffset: destination)
        save()
    }

    func run(_ action: Pinned) async -> RunResult {
        guard let client else { return .failed("Not connected") }
        do {
            try await client.runAction(action.id)
            return .started
        } catch {
            return .failed(error == .notFound ? "\(action.name) no longer exists in Cuebot." : ConnectionStore.describe(error))
        }
    }

    private func save() {
        guard let cuebotId else { return }
        defaults.set(try? JSONEncoder().encode(pinned), forKey: key(cuebotId))
    }
}
