import Foundation

/// What an invite link (or a saved membership) points at: the streamer's relay ID, the fingerprint of their Cuebot's
/// identity key, the invite secret when joining, and where the relay is. Everything comes from the link's fragment,
/// which no server sees (docs/MOD_PORTAL_PLAN.md, section 4).
public struct PortalTarget: Codable, Hashable, Sendable {
    public var relay: String
    public var fingerprint: String
    public var relayOrigin: String
    public var invite: String?

    public init(relay: String, fingerprint: String, relayOrigin: String, invite: String? = nil) {
        self.relay = relay
        self.fingerprint = fingerprint
        self.relayOrigin = relayOrigin
        self.invite = invite
    }

    /// Reads `https://portal.<domain>/#i=<invite>&r=<relay id>&k=<fingerprint>`; nil when it is not an invite (or
    /// portal) link. The relay is relay.<domain> next to portal.<domain>, else the link's own host.
    public static func parse(_ link: String) -> PortalTarget? {
        guard let url = URLComponents(string: link.trimmingCharacters(in: .whitespacesAndNewlines)),
              url.scheme == "https", let host = url.host, !host.isEmpty, let fragment = url.fragment else { return nil }
        var p: [String: String] = [:]
        for part in fragment.split(separator: "&") {
            guard let at = part.firstIndex(of: "="), at != part.startIndex else { continue }
            p[String(part[..<at])] = String(part[part.index(after: at)...]).removingPercentEncoding
        }
        let relay = p["r"] ?? "", fingerprint = p["k"] ?? "", invite = p["i"].flatMap { $0.isEmpty ? nil : $0 }
        guard relay.wholeMatch(of: /[a-z2-7]{32}/) != nil, fingerprint.wholeMatch(of: /[A-Za-z0-9_-]{43}/) != nil else { return nil }
        if let invite, invite.wholeMatch(of: /[A-Za-z0-9_-]{43}/) == nil { return nil }
        let full = url.port.map { "\(host):\($0)" } ?? host
        let origin = full.hasPrefix("portal.") ? "wss://relay.\(full.dropFirst("portal.".count))" : "wss://\(full)"
        return PortalTarget(relay: relay, fingerprint: fingerprint, relayOrigin: origin, invite: invite)
    }
}

/// A provider as Cuebot describes it (`providers`): the authorize URL lacks only state and the PKCE challenge.
public struct PortalProvider: Hashable, Sendable, Identifiable {
    public var id: String, name: String, authorize: String, pkce: Bool

    public init(id: String, name: String, authorize: String, pkce: Bool) {
        self.id = id
        self.name = name
        self.authorize = authorize
        self.pkce = pkce
    }

    public static func list(_ answer: PortalAnswer) -> [PortalProvider] {
        guard case .array(let items)? = answer["providers"] else { return [] }
        return items.compactMap { item in
            guard let id = item["id"]?.string, let authorize = item["authorize"]?.string else { return nil }
            var pkce = false
            if case .bool(let b)? = item["pkce"] { pkce = b }
            return PortalProvider(id: id, name: item["name"]?.string ?? id, authorize: authorize, pkce: pkce)
        }
    }
}

/// What survives the trip to the provider and back.
public struct PendingSignIn: Codable, Hashable, Sendable {
    public var target: PortalTarget, provider: String, state: String, verifier: String
}

/// The sign-in steps that need no screen or network (portal/web/flow.ts does the same in a browser). The provider
/// sends the mod back to the one fixed return page on portal.<domain>; when the state starts with `appState`, that page
/// hands the code to this app (cuebot-portal://signin?code=…&state=…) instead of signing the browser in.
public enum PortalSignIn {
    /// The prefix the return page looks for; "." never appears in base64url, so a browser's own state never has it.
    public static let appState = "app."
    public static let returnScheme = "cuebot-portal"
    public static let returnHost = "signin"

    /// Only the provider pages Cuebot may send a mod to.
    private static let providerHosts: Set<String> = ["accounts.google.com", "id.twitch.tv", "discord.com"]

    public static func start(_ target: PortalTarget, _ provider: PortalProvider) -> PendingSignIn {
        PendingSignIn(target: target, provider: provider.id, state: appState + PortalCrypto.base64Url(PortalCrypto.randomBytes(32)),
                      verifier: PortalCrypto.base64Url(PortalCrypto.randomBytes(48)))
    }

    public static func challenge(_ verifier: String) -> String { PortalCrypto.base64Url(PortalCrypto.sha256(Data(verifier.utf8))) }

    public struct NotAllowed: Error, Sendable { public let message: String }

    /// The address to send the mod to; throws when Cuebot named a page that is not a known provider's.
    public static func authorizeURL(_ provider: PortalProvider, _ pending: PendingSignIn) throws(NotAllowed) -> URL {
        guard var url = URLComponents(string: provider.authorize), url.scheme == "https", let host = url.host,
              providerHosts.contains(host) else { throw NotAllowed(message: "Unexpected sign-in address for \(provider.name).") }
        var items = (url.queryItems ?? []).filter { !["state", "code_challenge", "code_challenge_method"].contains($0.name) }
        items.append(URLQueryItem(name: "state", value: pending.state))
        if provider.pkce {
            items.append(URLQueryItem(name: "code_challenge", value: challenge(pending.verifier)))
            items.append(URLQueryItem(name: "code_challenge_method", value: "S256"))
        }
        url.queryItems = items
        guard let result = url.url else { throw NotAllowed(message: "Unexpected sign-in address for \(provider.name).") }
        return result
    }

    public enum Return: Equatable, Sendable {
        case code(String)
        case failed(String)
    }

    /// The return page's hand-over (`cuebot-portal://signin?…`), checked against the sign-in this app started.
    public static func readReturn(_ link: URL, pending: PendingSignIn?) -> Return {
        guard let url = URLComponents(url: link, resolvingAgainstBaseURL: false), url.scheme == returnScheme, url.host == returnHost else {
            return .failed("That is not a sign-in answer.")
        }
        var p: [String: String] = [:]
        for item in url.queryItems ?? [] { if let v = item.value { p[item.name] = v } }
        guard let pending else { return .failed("This sign-in has expired. Open the invite again.") }
        guard p["state"] == pending.state else { return .failed("This sign-in did not come from this app. Start again.") }
        if let error = p["error"] { return .failed(error == "access_denied" ? "The sign-in was cancelled." : "The sign-in failed (\(error)).") }
        guard let code = p["code"], !code.isEmpty else { return .failed("The provider did not send a sign-in code. Start again.") }
        return .code(code)
    }
}

/// One answer from the streamer's Cuebot over the link: every answer has "ok".
public typealias PortalAnswer = [String: JSONValue]

public extension Dictionary where Key == String, Value == JSONValue {
    var ok: Bool { if case .bool(true)? = self["ok"] { return true } else { return false } }
    var error: String? { if case .string(let s)? = self["error"] { return s } else { return nil } }
    func text(_ name: String) -> String? { if case .string(let s)? = self[name] { return s } else { return nil } }
}
