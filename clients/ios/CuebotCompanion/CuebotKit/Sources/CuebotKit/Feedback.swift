import Foundation

public enum FeedbackKind: String, CaseIterable, Identifiable, Sendable {
    case problem, idea

    public var id: String { rawValue }
    /// What the report intake calls this kind.
    public var wire: String { self == .problem ? "problem" : "feature" }
    public var label: String { self == .problem ? "Problem" : "Idea" }
    public var heading: String { self == .problem ? "What happened" : "Idea" }
    public var prompt: String { self == .problem ? "What happened, and what did you expect?" : "What would you like the app to do?" }
}

/// What a report says about the app: counts and versions, never addresses, names or tokens.
public struct FeedbackContext: Equatable, Sendable {
    public var appVersion: String, build: String, system: String, device: String, formFactor: String
    public var language: String, appearance: String, connection: String
    public var pairedCuebots: Int, modMemberships: Int
    public var cuebotVersion: String?, apiVersion: String?

    public init(appVersion: String, build: String, system: String, device: String, formFactor: String, language: String,
                appearance: String, connection: String, pairedCuebots: Int, modMemberships: Int,
                cuebotVersion: String?, apiVersion: String?) {
        self.appVersion = appVersion
        self.build = build
        self.system = system
        self.device = device
        self.formFactor = formFactor
        self.language = language
        self.appearance = appearance
        self.connection = connection
        self.pairedCuebots = pairedCuebots
        self.modMemberships = modMemberships
        self.cuebotVersion = cuebotVersion
        self.apiVersion = apiVersion
    }

    public var os: String { String("Companion for iOS \(system) (\(device))".prefix(100)) }

    public var rows: [(name: String, value: String)] {
        var rows: [(name: String, value: String)] = [
            ("App", "Cuebot Companion \(appVersion) (\(build))"),
            ("iOS", system),
            ("Device", "\(device), \(formFactor.lowercased())"),
            ("Language", language),
            ("Appearance", appearance),
            ("Connection", connection),
            ("Paired Cuebots", String(pairedCuebots)),
            ("Mod portals joined", String(modMemberships)),
        ]
        if let cuebotVersion, !cuebotVersion.isEmpty { rows.append(("Cuebot", cuebotVersion)) }
        if let apiVersion, !apiVersion.isEmpty { rows.append(("Cuebot API", apiVersion)) }
        return rows
    }
}

/// Reports become public GitHub issues, so what the user typed is cleaned here and again by the report intake.
public enum FeedbackText {
    public static let maxTitle = 200
    public static let maxDetails = 8_000
    private static let titlePrefix = "[iOS] "

    private static let rules: [(pattern: String, template: String)] = [
        // Any scheme: a pasted webhook, stream or pairing link can carry a secret in its path or query.
        (#"(?i)\b[a-z][a-z0-9+.-]*://\S+"#, "[link removed]"),
        (#"\beyJ[\w-]+\.[\w-]+\.[\w-]+"#, "[token removed]"),
        (#"(?i)\bBearer\s+[\w.~+/=-]+"#, "Bearer [token removed]"),
        (#"\b(?:gh[pousr]_|github_pat_|sk-|xox[abprs]-)[\w-]{10,}"#, "[token removed]"),
        (#"(?i)\b([\w-]*(?:token|secret|password|api[_-]?key))\s*[=:]\s*\S+"#, "$1=[removed]"),
        (#"[\w.+-]+@[\w-]+(?:\.[\w-]+)+"#, "[email removed]"),
        (#"(?i)\b(?:[a-z0-9-]+\.)+[a-z]{2,}/\S*"#, "[link removed]"),
        (#"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b"#, "[address removed]"),
        (#"(?i)\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b"#, "[address removed]"),
        (#"(?i)\b[\w-]+(?:\.[\w-]+)*\.(?:local|lan|home|internal)\b(?::\d+)?"#, "[address removed]"),
    ]

    public static func scrub(_ text: String) -> String {
        rules.reduce(text) { current, rule in
            guard let regex = try? NSRegularExpression(pattern: rule.pattern) else { return current }
            let range = NSRange(current.startIndex..., in: current)
            return regex.stringByReplacingMatches(in: current, range: range, withTemplate: rule.template)
        }
    }

    public static func title(_ text: String) -> String {
        let single = scrub(text).split(whereSeparator: \.isWhitespace).joined(separator: " ")
        return String((titlePrefix + single).prefix(maxTitle))
    }

    public static func body(kind: FeedbackKind, details: String, context: FeedbackContext) -> String {
        var body = "### \(kind.heading)\n\n"
        body += String(scrub(details).trimmingCharacters(in: .whitespacesAndNewlines).prefix(maxDetails))
        body += "\n\n### App and device\n\n| | |\n| --- | --- |\n"
        for row in context.rows {
            let value = row.value.replacingOccurrences(of: "|", with: "/").replacingOccurrences(of: "\n", with: " ")
            body += "| \(row.name) | \(value) |\n"
        }
        return body
    }
}

public enum FeedbackResult: Equatable, Sendable {
    case sent(number: Int, url: URL)
    case failed(String)
}

/// Files feedback through Cuebot's report intake, which holds the GitHub credentials. The app has none.
public struct FeedbackReporter: Sendable {
    public static let endpoint = URL(string: "https://cuebot-report-intake.kris-barrantes.workers.dev/v1/reports")!
    private static let tryLater = "The report service could not take this right now. Please try again later."

    private let url: URL
    private let session: URLSession

    public init(url: URL = FeedbackReporter.endpoint, session: URLSession = .shared) {
        self.url = url
        self.session = session
    }

    /// A random id that only lets the intake limit how often one install sends. Issues show a hash of it.
    public static func newInstallId() -> String {
        var generator = SystemRandomNumberGenerator()
        return (0..<16).map { _ in String(format: "%02x", UInt8.random(in: .min ... .max, using: &generator)) }.joined()
    }

    public static func isInstallId(_ text: String) -> Bool {
        text.count == 32 && text.allSatisfy { "0123456789abcdef".contains($0) }
    }

    public func send(kind: FeedbackKind, title: String, details: String, context: FeedbackContext, installId: String) async -> FeedbackResult {
        let payload: [String: String] = [
            "kind": kind.wire,
            "title": FeedbackText.title(title),
            "body": FeedbackText.body(kind: kind, details: details, context: context),
            "version": context.appVersion,
            "os": context.os,
            "installId": installId,
        ]
        var request = URLRequest(url: url, timeoutInterval: 30)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        guard let body = try? JSONSerialization.data(withJSONObject: payload) else { return .failed(Self.tryLater) }
        request.httpBody = body
        do {
            let (data, response) = try await session.data(for: request)
            let status = (response as? HTTPURLResponse)?.statusCode ?? 0
            let answer = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            if (200..<300).contains(status), let number = answer?["number"] as? Int,
               let text = answer?["url"] as? String, let issue = URL(string: text) {
                return .sent(number: number, url: issue)
            }
            if status == 429 {
                let seconds = answer?["retryAfter"] as? Int ?? 3600
                return .failed("Too many reports were sent recently. Try again in about \(max(1, (seconds + 59) / 60)) minutes.")
            }
            return .failed(Self.tryLater)
        } catch {
            return .failed("Could not reach the report service. Check your internet connection and try again.")
        }
    }
}
