import Foundation

/// One streamer profile: a persona's whole Cuebot (accounts, mods, actions). One is open at a time.
public struct StreamerProfile: Codable, Hashable, Sendable, Identifiable {
    public var id = "", name = "", color = ""
    public var open = false

    public init(id: String, name: String, color: String = "", open: Bool = false) {
        (self.id, self.name, self.color, self.open) = (id, name.isEmpty ? id : name, color, open)
    }

    private enum CodingKeys: String, CodingKey { case id, name, color, open }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(id: c.value(.id, ""), name: c.value(.name, ""), color: c.value(.color, ""), open: c.value(.open, false))
    }
}

/// `GET /api/v1/profiles`: the open profile, all of them, the platforms live right now, and whether a switch runs.
public struct ProfilesState: Decodable, Hashable, Sendable {
    public var current: StreamerProfile?
    public var profiles: [StreamerProfile] = []
    public var live: [String] = []
    public var switching = false

    public init(current: StreamerProfile?, profiles: [StreamerProfile], live: [String] = [], switching: Bool = false) {
        (self.current, self.profiles, self.live, self.switching) = (current, profiles, live, switching)
    }

    private enum CodingKeys: String, CodingKey { case current, profiles, live, switching }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        profiles = c.value(.profiles, [])
        current = c.optional(.current) ?? profiles.first(where: \.open)
        live = c.value(.live, [])
        switching = c.value(.switching, false)
    }
}

/// What asking Cuebot to open another profile did.
public enum ProfileActivation: Equatable, Sendable {
    /// Cuebot is restarting on that profile: it is gone for a few seconds, then back.
    case switching
    case alreadyOpen
    /// 409: a stream is live; nothing happened. Ask again with force to switch anyway. Carries Cuebot's words.
    case live(String)
    /// 409: a switch is already under way.
    case busy(String)
}
