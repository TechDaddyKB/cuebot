import Foundation

/// The kinds of stream events the Chat page's events card can show or hide, one switch each. Cuebot's feed kinds
/// (Follow, Subscription, GiftSubscription, Tip, Raid, Redemption, StreamOnline, StreamOffline, Other) and the names
/// other sources use (cheer, bits, Kicks, Super Chat, rant, membership...) fall into one group each.
public enum FeedGroup: String, CaseIterable, Identifiable, Sendable {
    case follows, subs, tips, raids, redemptions, other

    public var id: String { rawValue }

    public var label: String {
        switch self {
        case .follows: "Follows"
        case .subs: "Subs and memberships"
        case .tips: "Tips, bits, Kicks, Super Chats and rants"
        case .raids: "Raids"
        case .redemptions: "Channel point redemptions"
        case .other: "Everything else (going live, offline, other)"
        }
    }

    public var shownByDefault: Bool { self != .redemptions && self != .other }

    public static let defaults: Set<FeedGroup> = Set(allCases.filter(\.shownByDefault))

    private static let kinds: [(FeedGroup, Set<String>)] = [
        (.follows, ["follow", "follower"]),
        (.subs, ["subscription", "giftsubscription", "sub", "resub", "resubscription", "giftsub", "giftedsub", "membership", "membergift", "giftmembership"]),
        (.tips, ["tip", "donation", "cheer", "bits", "kicks", "kicksgifted", "superchat", "supersticker", "rant", "charity"]),
        (.raids, ["raid", "host"]),
        (.redemptions, ["redemption", "reward"]),
    ]

    /// The group of a feed item's `kind` (case and separators do not matter); unknown kinds are `.other`.
    public static func of(_ kind: String) -> FeedGroup {
        let key = String(kind.lowercased().filter { $0.isLetter || $0.isNumber })
        return kinds.first { $0.1.contains(key) }?.0 ?? .other
    }

    /// The groups in a comma-separated list of keys; unknown keys are skipped.
    public static func parse(_ text: String) -> Set<FeedGroup> {
        Set(text.split(separator: ",").compactMap { FeedGroup(rawValue: $0.trimmingCharacters(in: .whitespaces)) })
    }

    public static func encode(_ groups: Set<FeedGroup>) -> String {
        allCases.filter(groups.contains).map(\.rawValue).joined(separator: ",")
    }

    /// The items the card shows for `groups`, in the feed's order (Cuebot sends the newest first), at most `limit`.
    public static func filter(_ feed: [FeedItem], groups: Set<FeedGroup>, limit: Int = 50) -> [FeedItem] {
        Array(feed.lazy.filter { groups.contains(of($0.kind)) }.prefix(limit))
    }

    /// A feed item's `kind` for people: "GiftSubscription" is "Gift subscription"; empty for none.
    public static func kindLabel(_ kind: String) -> String {
        var words = ""
        var previous: Character?
        for c in kind.replacingOccurrences(of: "_", with: " ") {
            if c.isUppercase, let p = previous, p.isLowercase { words.append(" ") }
            words.append(c)
            previous = c
        }
        let lower = words.trimmingCharacters(in: .whitespaces).lowercased()
        return lower.prefix(1).uppercased() + lower.dropFirst()
    }
}
