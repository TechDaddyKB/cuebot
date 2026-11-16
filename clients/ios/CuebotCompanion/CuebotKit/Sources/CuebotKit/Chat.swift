import Foundation

// The combined chat of the Chat tab: GET /api/v1/chat, /chat/capabilities, /chat/modlog, POST /chat/send and
// /chat/moderate, and the chat.received, chat.moderated and chat.modLog events. Decoded tolerantly like every model.

/// One chat message, text only. `deleted` once the platform reported it deleted.
public struct ChatLine: Decodable, Hashable, Sendable, Identifiable {
    public var platform = "", id = "", user = "", userId = "", text = "", time = "", roles = "", color = "", amount = ""
    public var canDelete = true
    public var deleted = false

    /// Unique across platforms (two platforms can use the same message id).
    public var key: String { "\(platform):\(id)" }
    public var isModerator: Bool { roles.contains("Moderator") }
    public var isBroadcaster: Bool { roles.contains("Broadcaster") }

    private enum CodingKeys: String, CodingKey { case platform, id, user, userId, text, time, roles, color, amount, canDelete, deleted }

    public init(platform: String, id: String, user: String, userId: String = "", text: String = "", time: String = "", canDelete: Bool = true, deleted: Bool = false) {
        self.platform = platform
        self.id = id
        self.user = user
        self.userId = userId
        self.text = text
        self.time = time
        self.canDelete = canDelete
        self.deleted = deleted
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        platform = c.value(.platform, "")
        id = c.value(.id, "")
        user = c.value(.user, "")
        userId = c.value(.userId, "")
        text = c.value(.text, "")
        time = c.value(.time, "")
        roles = c.value(.roles, "")
        color = c.value(.color, "")
        amount = c.value(.amount, "")
        canDelete = c.value(.canDelete, true)
        deleted = c.value(.deleted, false)
    }
}

/// A deletion, ban or timeout a platform reported, whoever made it (Cuebot, a mod, the platform's website).
public struct ModerationNotice: Decodable, Hashable, Sendable {
    public enum Kind: String, Sendable { case messageDeleted, userBanned, userTimedOut, unknown }

    public var platform = "", messageId = "", userId = "", user = "", moderator = "", time = ""
    public var kind = Kind.unknown
    public var durationSeconds: Int?

    private enum CodingKeys: String, CodingKey { case platform, kind, messageId, userId, user, moderator, time, durationSeconds }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        platform = c.value(.platform, "")
        kind = Kind(rawValue: c.value(.kind, "")) ?? .unknown
        messageId = c.value(.messageId, "")
        userId = c.value(.userId, "")
        user = c.value(.user, "")
        moderator = c.value(.moderator, "")
        time = c.value(.time, "")
        durationSeconds = (c.optional(.durationSeconds) as Double?).map { Int($0) }
    }
}

/// `GET /api/v1/chat`: recent messages (oldest first) and who can send, and send as the bot account.
public struct ChatSnapshot: Decodable, Hashable, Sendable {
    public var messages: [ChatLine] = []
    public var moderation: [ModerationNotice] = []
    public var platforms: [String] = []
    public var sendablePlatforms: [String] = []
    public var botPlatforms: [String] = []

    private enum CodingKeys: String, CodingKey { case messages, moderation, platforms, sendablePlatforms, botPlatforms }

    public init() {}

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        messages = c.value(.messages, [])
        moderation = c.value(.moderation, [])
        platforms = c.value(.platforms, [])
        sendablePlatforms = c.value(.sendablePlatforms, [])
        botPlatforms = c.value(.botPlatforms, [])
    }
}

/// Who writes a message: the platform's own setting, the broadcaster, or the bot account.
public enum SendAs: String, Sendable, CaseIterable { case `default`, broadcaster, bot }

/// The moderation tools the Chat tab offers.
public enum ModAction: String, Sendable, CaseIterable {
    case delete, timeout, ban, unban, warn

    public var label: String {
        switch self {
        case .delete: "Delete message"
        case .timeout: "Timeout"
        case .ban: "Ban"
        case .unban: "Unban"
        case .warn: "Warn"
        }
    }
}

/// What one tool does on one platform now.
public struct ToolCapability: Decodable, Hashable, Sendable {
    public enum State: String, Sendable { case works, closest, onWebsite, notAvailable }

    public var action = "", label = "", note = "", websiteUrl = ""
    public var state = State.notAvailable
    public var canRun = false
    public var maxReasonLength: Int?

    private enum CodingKeys: String, CodingKey { case action, label, state, canRun, note, websiteUrl, maxReasonLength }

    public init(action: String, note: String = "") {
        self.action = action
        self.note = note
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        action = c.value(.action, "")
        label = c.value(.label, "")
        state = State(rawValue: c.value(.state, "")) ?? .notAvailable
        canRun = c.value(.canRun, state == .works || state == .closest)
        note = c.value(.note, "")
        websiteUrl = c.value(.websiteUrl, "")
        maxReasonLength = (c.optional(.maxReasonLength) as Double?).map { Int($0) }
    }
}

/// A timeout length the Chat tab offers, and what the platform really applies (`note` when it differs).
public struct TimeoutPreset: Decodable, Hashable, Sendable, Identifiable {
    public var requestedSeconds = 0, appliedSeconds = 0
    public var note = ""
    public var id: Int { requestedSeconds }
    public var label: String { durationLabel(requestedSeconds) }

    private enum CodingKeys: String, CodingKey { case requestedSeconds, appliedSeconds, note }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        requestedSeconds = Int(c.value(.requestedSeconds, 0.0))
        appliedSeconds = Int(c.value(.appliedSeconds, Double(requestedSeconds)))
        note = c.value(.note, "")
    }
}

/// One platform's tools.
public struct PlatformCapabilities: Decodable, Hashable, Sendable {
    public var platform = ""
    public var canSend = false, canSendAsBot = false
    public var actions: [ToolCapability] = []
    public var timeoutPresets: [TimeoutPreset] = []

    private enum CodingKeys: String, CodingKey { case platform, canSend, canSendAsBot, actions, timeoutPresets }

    public init(platform: String) { self.platform = platform }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        platform = c.value(.platform, "")
        canSend = c.value(.canSend, false)
        canSendAsBot = c.value(.canSendAsBot, false)
        actions = c.value(.actions, [])
        timeoutPresets = c.value(.timeoutPresets, [])
    }

    /// The tool, or "not available" when Cuebot did not list it.
    public func tool(_ action: ModAction) -> ToolCapability {
        actions.first { $0.action == action.rawValue } ?? ToolCapability(action: action.rawValue, note: "Cuebot did not say.")
    }
}

/// `GET /api/v1/chat/capabilities`.
public struct ChatCapabilities: Decodable, Hashable, Sendable {
    public var platforms: [PlatformCapabilities] = []

    private enum CodingKeys: String, CodingKey { case platforms }

    public init() {}

    public init(from decoder: Decoder) throws {
        platforms = try decoder.container(keyedBy: CodingKeys.self).value(.platforms, [])
    }

    public func of(_ platform: String) -> PlatformCapabilities {
        platforms.first { $0.platform.caseInsensitiveCompare(platform) == .orderedSame } ?? PlatformCapabilities(platform: platform)
    }
}

/// What `POST /api/v1/chat/moderate` did. `websiteUrl` when the platform only does it on its website.
public struct ModerationOutcome: Decodable, Hashable, Sendable {
    public var ok = false
    public var result = "", message = "", websiteUrl = ""
    public var appliedDurationSeconds: Int?

    private enum CodingKeys: String, CodingKey { case ok, result, message, websiteUrl, appliedDurationSeconds }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        ok = c.value(.ok, false)
        result = c.value(.result, "")
        message = c.value(.message, "")
        websiteUrl = c.value(.websiteUrl, "")
        appliedDurationSeconds = (c.optional(.appliedDurationSeconds) as Double?).map { Int($0) }
    }
}

/// One mod log row.
public struct ModLogEntry: Decodable, Hashable, Sendable, Identifiable {
    public var id = "", at = "", actorKind = "", actorName = "", platform = "", action = "", targetUserId = "", targetName = ""
    public var reason = "", result = "", error = ""
    public var durationAppliedSeconds: Int?

    private enum CodingKeys: String, CodingKey {
        case id, at, actorKind, actorName, platform, action, targetUserId, targetName, reason, result, error, durationAppliedSeconds
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        id = c.value(.id, "")
        at = c.value(.at, "")
        actorKind = c.value(.actorKind, "")
        actorName = c.value(.actorName, "")
        platform = c.value(.platform, "")
        action = c.value(.action, "")
        targetUserId = c.value(.targetUserId, "")
        targetName = c.value(.targetName, "")
        reason = c.value(.reason, "")
        result = c.value(.result, "")
        error = c.value(.error, "")
        durationAppliedSeconds = (c.optional(.durationAppliedSeconds) as Double?).map { Int($0) }
    }
}

/// "45 s", "10 min", "1 h", "1 day", "7 days".
public func durationLabel(_ seconds: Int) -> String {
    switch seconds {
    case ..<60: return "\(seconds) s"
    case ..<3_600: return seconds % 60 == 0 ? "\(seconds / 60) min" : "\(seconds / 60) min \(seconds % 60) s"
    case ..<86_400: return seconds % 3_600 == 0 ? "\(seconds / 3_600) h" : "\(seconds / 3_600) h \(seconds % 3_600 / 60) min"
    default:
        let days = seconds / 86_400, hours = seconds % 86_400 / 3_600
        return (days == 1 ? "1 day" : "\(days) days") + (hours > 0 ? " \(hours) h" : "")
    }
}

extension CuebotClient {
    public static let chatReceived = "chat.received"
    public static let chatModerated = "chat.moderated"
    public static let chatModLog = "chat.modLog"
    /// Sent only when asked for by name.
    public static let chatEvents = [chatReceived, chatModerated, chatModLog]
}
