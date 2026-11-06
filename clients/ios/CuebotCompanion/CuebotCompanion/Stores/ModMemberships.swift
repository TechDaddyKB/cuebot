import CuebotKit
import Foundation
import Observation

/// One streamer this phone is a mod for: where their Cuebot is, the identity it pinned at sign-in, and the alias of
/// this device's key for that membership. Like a browser's saved device (portal/web/main.ts), with the key in the
/// Secure Enclave or the Keychain.
struct Membership: Codable, Hashable, Identifiable, Sendable {
    static let defaultName = "Mod portal"

    var target: PortalTarget
    /// What the mod calls this streamer's portal on this phone: Cuebot does not say whose Cuebot it is.
    var name = Membership.defaultName
    /// The mod's own name in that Cuebot (the sign-in answer's "name").
    var member: String
    var role: String
    var keyAlias: String
    var at: Date

    var id: String { target.relay }

    /// "Signed in as Sam · Standard Mod".
    var signedInAs: String { "Signed in as " + [member.isEmpty ? "a mod" : member, role].filter { !$0.isEmpty }.joined(separator: " · ") }

    private enum CodingKeys: String, CodingKey { case target, name, member, role, keyAlias, at }

    init(target: PortalTarget, name: String = Membership.defaultName, member: String, role: String, keyAlias: String, at: Date) {
        (self.target, self.name, self.member, self.role, self.keyAlias, self.at) = (target, name, member, role, keyAlias, at)
    }

    init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        target = try c.decode(PortalTarget.self, forKey: .target)
        name = (try? c.decode(String.self, forKey: .name)) ?? Membership.defaultName
        member = (try? c.decode(String.self, forKey: .member)) ?? ""
        role = (try? c.decode(String.self, forKey: .role)) ?? ""
        keyAlias = try c.decode(String.self, forKey: .keyAlias)
        at = (try? c.decode(Date.self, forKey: .at)) ?? .now
    }
}

/// The streamers this phone mods for (UserDefaults; no secrets in them), and the sign-in waiting for the browser to
/// come back (the Keychain: it holds the PKCE verifier).
@MainActor @Observable
final class ModMemberships {
    private static let listKey = "modMemberships", pendingAccount = "modPendingSignIn"
    private let defaults: UserDefaults

    /// The newest first.
    private(set) var all: [Membership]

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        all = defaults.data(forKey: Self.listKey).flatMap { try? JSONDecoder().decode([Membership].self, from: $0) } ?? []
    }

    func find(_ relay: String) -> Membership? { all.first { $0.target.relay == relay } }

    /// Adds or replaces the membership for its relay; a replaced one's key is deleted when it is not reused.
    func save(_ membership: Membership) {
        if let old = find(membership.target.relay), old.keyAlias != membership.keyAlias { DeviceKeys.delete(old.keyAlias) }
        all = ([membership] + all.filter { $0.target.relay != membership.target.relay }).sorted { $0.at > $1.at }
        store()
    }

    func rename(_ relay: String, to name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let i = all.firstIndex(where: { $0.target.relay == relay }) else { return }
        all[i].name = trimmed.isEmpty ? Membership.defaultName : trimmed
        store()
    }

    /// Forgets a membership and deletes its key.
    func forget(_ relay: String) {
        if let m = find(relay) { DeviceKeys.delete(m.keyAlias) }
        all.removeAll { $0.target.relay == relay }
        store()
    }

    var pending: PendingSignIn? {
        get { Keychain.loadData(account: Self.pendingAccount).flatMap { try? JSONDecoder().decode(PendingSignIn.self, from: $0) } }
        set {
            if let newValue, let data = try? JSONEncoder().encode(newValue) { Keychain.saveData(data, account: Self.pendingAccount) }
            else { Keychain.deleteData(account: Self.pendingAccount) }
        }
    }

    private func store() { defaults.set(try? JSONEncoder().encode(all), forKey: Self.listKey) }
}
