import Foundation

/// One entry of `GET /api/v1/portal/changes`: a mod's change waiting for the streamer. Times are Unix milliseconds.
public struct PendingChange: Codable, Hashable, Sendable, Identifiable {
    public var id = "", member = "", area = "", kind = "", target = "", summary = ""
    public var at = 0.0, expires = 0.0

    public init(id: String, member: String, area: String = "", kind: String = "", target: String, summary: String = "", at: Double = 0, expires: Double = 0) {
        self.id = id
        self.member = member
        self.area = area
        self.kind = kind
        self.target = target
        self.summary = summary.isEmpty ? "\(member) changed '\(target)'" : summary
        self.at = at
        self.expires = expires
    }

    private enum CodingKeys: String, CodingKey { case id, member, area, kind, target, summary, at, expires }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(id: c.value(.id, ""), member: c.value(.member, ""), area: c.value(.area, ""), kind: c.value(.kind, ""),
                  target: c.value(.target, ""), summary: c.value(.summary, ""), at: c.value(.at, 0.0), expires: c.value(.expires, 0.0))
    }
}

/// What approving or rejecting did, and the queue after it.
public struct ChangeDecision: Decodable, Sendable {
    public enum Result: String, Sendable {
        case applied, rejected
        /// The thing changed since the mod asked: nothing was applied; approving with force uses the mod's version.
        case conflict
        /// Someone decided it already, or it expired.
        case notPending
    }

    public var result: Result
    public var pending: [PendingChange]

    private enum CodingKeys: String, CodingKey { case result, changes }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        result = Result(rawValue: c.value(.result, "")) ?? .notPending
        pending = c.value(.changes, [PendingChange]())
    }
}
