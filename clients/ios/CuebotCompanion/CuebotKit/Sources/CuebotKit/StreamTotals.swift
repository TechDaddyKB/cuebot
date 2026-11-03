import Foundation

/// This stream's numbers from the dashboard's `totals` (the StreamElements-style session data Cuebot keeps for
/// widgets): one row per `<type>-session` entry, e.g. `cheer-session {count, amount}` → "Bits 500". For types that
/// are not money (follows, subs, raids) the session amount counts events too, so only the count is shown; a raid adds
/// the last raid's viewers from `raid-latest`.
public struct StreamTotal: Hashable, Sendable, Identifiable {
    public var type: String
    public var label: String
    public var value: String
    public var detail: String
    public var id: String { type }
}

public enum StreamTotals {
    private static let order = ["follower", "subscriber", "cheer", "tip", "superchat", "raid", "host", "merch", "charityCampaignDonation"]

    public static func rows(_ totals: [String: JSONValue]) -> [StreamTotal] {
        var rows: [StreamTotal] = []
        for (key, value) in totals where key.hasSuffix("-session") {
            let type = String(key.dropLast("-session".count))
            guard !type.contains("-") else { continue }   // "subscriber-new-session" and friends are breakdowns
            let count = value["count"]?.double ?? 0
            let amount = value["amount"]?.double ?? 0
            rows.append(row(type: type, count: count, amount: amount, latest: totals["\(type)-latest"]?["amount"]?.double))
        }
        return rows.sorted { (order.firstIndex(of: $0.type) ?? 99, $0.type) < (order.firstIndex(of: $1.type) ?? 99, $1.type) }
    }

    static func row(type: String, count: Double, amount: Double, latest: Double? = nil) -> StreamTotal {
        func n(_ value: Double) -> String { value.formatted(.number.precision(.fractionLength(0...2))) }
        switch type {
        case "follower": return StreamTotal(type: type, label: "Follows", value: n(count), detail: "")
        case "subscriber": return StreamTotal(type: type, label: "Subs", value: n(count), detail: "")
        case "cheer": return StreamTotal(type: type, label: "Bits", value: n(amount), detail: count == 1 ? "1 cheer" : "\(n(count)) cheers")
        case "tip": return StreamTotal(type: type, label: "Tips", value: n(amount), detail: count == 1 ? "1 tip" : "\(n(count)) tips")
        case "superchat": return StreamTotal(type: type, label: "Super Chats", value: n(amount), detail: count == 1 ? "1 sent" : "\(n(count)) sent")
        case "raid":
            let last = latest.map { $0 == 1 ? "last: 1 viewer" : "last: \(n($0)) viewers" } ?? ""
            return StreamTotal(type: type, label: "Raids", value: n(count), detail: count > 0 ? last : "")
        default:
            var words = ""
            for character in type {
                if character.isUppercase, !words.isEmpty { words += " " }
                words += String(character)
            }
            let label = words.prefix(1).uppercased() + words.dropFirst().lowercased()
            return StreamTotal(type: type, label: label, value: n(amount > 0 ? amount : count), detail: amount > 0 ? (count == 1 ? "once" : "\(n(count)) times") : "")
        }
    }
}
