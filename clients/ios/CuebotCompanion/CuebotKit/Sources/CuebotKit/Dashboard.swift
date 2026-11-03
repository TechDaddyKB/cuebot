import Foundation

/// `GET /api/v1/dashboard`: one snapshot of everything the Dashboard tab shows. `revision` goes up with every change;
/// a `dashboard.changed` event tells the app to fetch it again.
public struct Dashboard: Codable, Hashable, Sendable {
    public var instanceId = ""
    public var version = ""
    public var connections: [ConnectionInfo] = []
    /// The stream totals session snapshot (followers, subs, bits, tips, raids ...), kept as-is.
    public var totals: [String: JSONValue] = [:]
    public var subathon = SubathonState()
    public var topSpenders = TopSpenders()
    public var feed: [FeedItem] = []
    public var revision = 0

    private enum CodingKeys: String, CodingKey { case instanceId, version, connections, totals, subathon, topSpenders, feed, revision }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        instanceId = c.value(.instanceId, "")
        version = c.value(.version, "")
        connections = c.value(.connections, [])
        totals = c.value(.totals, [:])
        subathon = c.value(.subathon, SubathonState())
        topSpenders = c.value(.topSpenders, TopSpenders())
        feed = c.value(.feed, [])
        revision = c.value(.revision, 0)
    }
}

public struct SubathonState: Codable, Hashable, Sendable {
    public var timers: [SubathonTimer] = []
    public var goals: [PointsGoal] = []

    private enum CodingKeys: String, CodingKey { case timers, goals }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        timers = c.value(.timers, [])
        goals = c.value(.goals, [])
    }
}

public struct SubathonTimer: Codable, Hashable, Sendable, Identifiable {
    public enum State: String, Codable, Hashable, Sendable {
        /// Cuebot's TimerState: Stopped, Running, Paused, or Ended once it ran out (Start restarts it).
        case running, paused, stopped, ended, unknown

        public init(from decoder: Decoder) throws {
            self = State(rawValue: ((try? decoder.singleValueContainer().decode(String.self)) ?? "").lowercased()) ?? .unknown
        }
    }

    public var id = "", name = ""
    public var state = State.stopped
    /// Seconds left when the snapshot was taken.
    public var remaining = 0.0
    /// Unix time in milliseconds when a running timer reaches zero; nil while paused or stopped.
    public var endsAt: Double?
    public var elapsed = 0.0, added = 0.0, multiplier = 1.0

    private enum CodingKeys: String, CodingKey { case id, name, state, remaining, endsAt, elapsed, added, multiplier }

    public init(id: String, name: String, state: State, remaining: Double, endsAt: Double? = nil) {
        self.id = id
        self.name = name
        self.state = state
        self.remaining = remaining
        self.endsAt = endsAt
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        state = c.value(.state, .stopped)
        remaining = c.value(.remaining, 0)
        endsAt = c.optional(.endsAt)
        elapsed = c.value(.elapsed, 0)
        added = c.value(.added, 0)
        multiplier = c.value(.multiplier, 1)
    }

    /// Seconds left at `now`: counted down on the phone from `endsAt` while running, else the snapshot's value.
    public func remaining(at now: Date) -> Double {
        guard state == .running, let endsAt else { return max(0, remaining) }
        return max(0, endsAt / 1000 - now.timeIntervalSince1970)
    }

    /// "H:MM:SS" (or "D:HH:MM:SS" past a day), rounded down like Cuebot's own timer.
    public static func format(_ seconds: Double) -> String {
        let total = Int(max(0, seconds).rounded(.down))
        let (days, hours, minutes, secs) = (total / 86_400, total / 3_600 % 24, total / 60 % 60, total % 60)
        return days > 0
            ? String(format: "%d:%02d:%02d:%02d", days, hours, minutes, secs)
            : String(format: "%d:%02d:%02d", hours, minutes, secs)
    }
}

public struct GoalStep: Codable, Hashable, Sendable {
    public var name = ""
    public var points = 0.0
    public var reached = false

    private enum CodingKeys: String, CodingKey { case name, points, reached }

    public init(name: String, points: Double, reached: Bool = false) {
        self.name = name
        self.points = points
        self.reached = reached
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        name = c.value(.name, "")
        points = c.value(.points, 0)
        reached = c.value(.reached, false)
    }
}

public struct PointsGoal: Codable, Hashable, Sendable, Identifiable {
    public var id = "", name = ""
    public var points = 0.0, multiplier = 1.0
    public var steps: [GoalStep] = []
    public var next: GoalStep?
    public var previous: GoalStep?

    private enum CodingKeys: String, CodingKey { case id, name, points, multiplier, steps, next, previous }

    public init(id: String, name: String, points: Double, steps: [GoalStep]) {
        self.id = id
        self.name = name
        self.points = points
        self.steps = steps
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        name = c.value(.name, "")
        points = c.value(.points, 0)
        multiplier = c.value(.multiplier, 1)
        steps = c.value(.steps, [])
        next = c.optional(.next)
        previous = c.optional(.previous)
        if next?.name.isEmpty == true && next?.points == 0 { next = nil }   // "{}" means none
        if previous?.name.isEmpty == true && previous?.points == 0 { previous = nil }
    }

    /// Progress (0...1) from the last reached step (or 0) to the next one; 1 when every step is reached.
    public var progressToNext: Double {
        let reached = steps.filter { $0.points <= points }.map(\.points).max() ?? 0
        guard let target = steps.map(\.points).filter({ $0 > points }).min() else { return steps.isEmpty ? 0 : 1 }
        let span = target - reached
        return span <= 0 ? 0 : min(1, max(0, (points - reached) / span))
    }
}

public struct Spender: Codable, Hashable, Sendable, Identifiable {
    public var rank = 0
    public var name = "", platform = "", formatted = ""
    public var amount = 0.0
    public var count = 0
    public var id: String { "\(platform)|\(name)" }

    private enum CodingKeys: String, CodingKey { case rank, name, platform, formatted, amount, count }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        rank = c.value(.rank, 0)
        name = c.value(.name, "")
        platform = c.value(.platform, "")
        formatted = c.value(.formatted, "")
        amount = c.value(.amount, 0)
        count = c.value(.count, 0)
    }
}

public struct SpenderBoard: Codable, Hashable, Sendable {
    public var total = 0.0
    public var formattedTotal = ""
    public var spenders: [Spender] = []

    private enum CodingKeys: String, CodingKey { case total, formattedTotal, spenders }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        total = c.value(.total, 0)
        formattedTotal = c.value(.formattedTotal, "")
        spenders = c.value(.spenders, [])
    }
}

/// Top spenders: `periods[period][platform]`, where platform is "all" or a lowercase platform name. Period and
/// platform keys come from the snapshot (never hard-coded); "stream" is listed first when present.
public struct TopSpenders: Codable, Hashable, Sendable {
    public var currency = ""
    public var periods: [String: [String: SpenderBoard]] = [:]

    private enum CodingKeys: String, CodingKey { case currency, periods }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        currency = c.value(.currency, "")
        periods = c.value(.periods, [:])
    }

    private static let periodOrder = ["stream", "day", "week", "month", "year", "all", "alltime"]

    public var periodKeys: [String] {
        periods.keys.sorted { (Self.periodOrder.firstIndex(of: $0) ?? 99, $0) < (Self.periodOrder.firstIndex(of: $1) ?? 99, $1) }
    }

    public func platformKeys(period: String) -> [String] {
        (periods[period]?.keys.sorted() ?? []).sorted { ($0 == "all" ? 0 : 1, $0) < ($1 == "all" ? 0 : 1, $1) }
    }

    public func board(period: String, platform: String = "all") -> SpenderBoard {
        periods[period]?[platform] ?? SpenderBoard()
    }
}

/// One line of the activity feed.
public struct FeedItem: Codable, Hashable, Sendable {
    public var platform = "", kind = "", user = "", title = "", detail = "", amount = "", time = ""
    public var isTest = false

    private enum CodingKeys: String, CodingKey { case platform, kind, user, title, detail, message, amount, time, isTest }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(platform, forKey: .platform)
        try c.encode(kind, forKey: .kind)
        try c.encode(user, forKey: .user)
        try c.encode(title, forKey: .title)
        try c.encode(detail, forKey: .detail)
        try c.encode(amount, forKey: .amount)
        try c.encode(time, forKey: .time)
        try c.encode(isTest, forKey: .isTest)
    }

    public init(platform: String, kind: String, user: String, title: String = "") {
        self.platform = platform
        self.kind = kind
        self.user = user
        self.title = title
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        platform = c.value(.platform, "")
        kind = c.value(.kind, "")
        user = c.value(.user, "")
        title = c.value(.title, "")
        detail = c.value(.detail, c.value(.message, ""))   // "message" in the first draft of the plan
        amount = c.value(.amount, "")
        time = c.value(.time, "")
        isTest = c.value(.isTest, false)
    }
}
