import CuebotKit
import Foundation
import Observation

/// One paired Cuebot as the app keeps it: everything but the tokens in UserDefaults, the tokens in the Keychain.
struct PairedCuebot: Codable, Identifiable, Hashable {
    var id: String = UUID().uuidString
    var name: String
    var host: String
    var apiPort: Int
    var httpPort: Int
    var instanceId: String?

    func endpoint(_ secrets: Keychain.Secrets) -> CuebotEndpoint {
        CuebotEndpoint(name: name, host: host, apiPort: apiPort, httpPort: httpPort, token: secrets.token,
                       deckToken: secrets.deckToken, instanceId: instanceId)
    }
}

/// The paired Cuebots and which one is active.
@MainActor @Observable
final class PairedCuebots {
    private static let listKey = "pairedCuebots", activeKey = "activeCuebot"
    private let defaults: UserDefaults

    private(set) var all: [PairedCuebot]
    private(set) var activeId: String?
    /// Changes whenever the active Cuebot or its details change (pairing again with a new token keeps its id).
    private(set) var revision = 0

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        all = (defaults.data(forKey: Self.listKey)).flatMap { try? JSONDecoder().decode([PairedCuebot].self, from: $0) } ?? []
        activeId = defaults.string(forKey: Self.activeKey)
        if active == nil { activeId = all.first?.id }
    }

    var active: PairedCuebot? { all.first { $0.id == activeId } }

    /// The active Cuebot with its tokens, or nil when none is paired (or its tokens are gone).
    var activeEndpoint: CuebotEndpoint? {
        guard let active, let secrets = Keychain.load(for: active.id) else { return nil }
        return active.endpoint(secrets)
    }

    /// Adds a Cuebot, or updates the one with the same instance id (or host and port), and makes it active.
    @discardableResult
    func pair(_ endpoint: CuebotEndpoint) -> PairedCuebot {
        // The same Cuebot: its instance id, else the same address (a reinstalled Cuebot gets a new id but keeps its
        // address, and pairing it again should update the entry, not add a second one).
        let index = all.firstIndex { existing in
            if let id = endpoint.instanceId, id == existing.instanceId { return true }
            return existing.host == endpoint.host && existing.apiPort == endpoint.apiPort
        }
        var entry = index.map { all[$0] } ?? PairedCuebot(name: endpoint.name, host: endpoint.host, apiPort: endpoint.apiPort, httpPort: endpoint.httpPort)
        entry.name = endpoint.name
        entry.host = endpoint.host
        entry.apiPort = endpoint.apiPort
        entry.httpPort = endpoint.httpPort
        entry.instanceId = endpoint.instanceId ?? entry.instanceId
        // Drop other entries for the same address left by earlier versions.
        all.removeAll { $0.id != entry.id && $0.host == entry.host && $0.apiPort == entry.apiPort }
        if let index { all[index] = entry } else { all.append(entry) }
        Keychain.save(.init(token: endpoint.token, deckToken: endpoint.deckToken), for: entry.id)
        activeId = entry.id
        save()
        return entry
    }

    func activate(_ id: String) {
        activeId = id
        save()
    }

    func rename(_ id: String, to name: String) {
        guard let index = all.firstIndex(where: { $0.id == id }), !name.trimmingCharacters(in: .whitespaces).isEmpty else { return }
        all[index].name = name.trimmingCharacters(in: .whitespaces)
        save()
    }

    func remove(_ id: String) {
        all.removeAll { $0.id == id }
        Keychain.delete(for: id)
        if activeId == id { activeId = all.first?.id }
        save()
    }

    /// Remembers the instance id the status reported, so the same Cuebot is recognized after an address change.
    func learnInstanceId(_ instanceId: String, for id: String) {
        guard !instanceId.isEmpty, let index = all.firstIndex(where: { $0.id == id }), all[index].instanceId != instanceId else { return }
        all[index].instanceId = instanceId
        save(bumpRevision: false)   // the same Cuebot: nothing needs to reconnect
    }

    private func save(bumpRevision: Bool = true) {
        if bumpRevision { revision += 1 }
        defaults.set(try? JSONEncoder().encode(all), forKey: Self.listKey)
        defaults.set(activeId, forKey: Self.activeKey)
    }
}
