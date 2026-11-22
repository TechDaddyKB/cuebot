import Foundation

/// Where a paired Cuebot is and how to talk to it: the REST API (Bearer token) and the HTTP server that serves decks
/// (optional deck token). Secrets are kept in the Keychain by the app; this value type only carries them.
public struct CuebotEndpoint: Codable, Hashable, Sendable {
    public var name: String
    public var host: String
    public var apiPort: Int
    public var httpPort: Int
    public var token: String
    public var deckToken: String?
    /// `status.app.instanceId`: recognizes the same Cuebot again after its address changes.
    public var instanceId: String?
    /// Talk to this Cuebot over HTTPS. Only a hosted server sets this: a Cuebot on the home network serves plain HTTP.
    public var https: Bool?

    public static let defaultApiPort = 7475
    public static let defaultHttpPort = 7474

    public init(name: String = "Cuebot", host: String, apiPort: Int = defaultApiPort, httpPort: Int = defaultHttpPort,
                token: String, deckToken: String? = nil, instanceId: String? = nil, https: Bool? = nil) {
        self.name = name
        self.host = host
        self.apiPort = apiPort
        self.httpPort = httpPort
        self.token = token
        self.deckToken = deckToken
        self.instanceId = instanceId
        self.https = https
    }

    /// `http://host:apiPort`, or `https://` when `https` is set (IPv6 literals in brackets).
    public var apiBase: URL { Self.base(host: host, port: apiPort, https: https == true) }

    /// `http://host:httpPort` (or `https://`), which also resolves the deck's relative image links.
    public var httpBase: URL { Self.base(host: host, port: httpPort, https: https == true) }

    private static func base(host: String, port: Int, https: Bool) -> URL {
        var components = URLComponents()
        components.scheme = https ? "https" : "http"
        components.host = host.contains(":") && !host.hasPrefix("[") ? "[\(host)]" : host
        components.port = port
        return components.url ?? URL(string: "http://127.0.0.1:\(port)")!
    }
}

/// The pairing link Cuebot shows as a QR code:
/// `cuebot://pair?v=1&name=…&host=…&api=7475&http=7474&token=…&deckToken=…&id=…`.
/// A hosted server adds `https=443`: the app then uses HTTPS on that port for both the API and decks, while older apps
/// ignore it and keep using `api` and `http`.
public enum PairingLink {
    public enum Failure: Error, Equatable, Sendable {
        case notAPairingLink
        case unsupportedVersion(String)
        case missing(String)
        case badPort(String)
    }

    public static func parse(_ text: String) throws(Failure) -> CuebotEndpoint {
        guard let components = URLComponents(string: text.trimmingCharacters(in: .whitespacesAndNewlines)),
              components.scheme?.lowercased() == "cuebot", components.host?.lowercased() == "pair" else {
            throw .notAPairingLink
        }
        var query: [String: String] = [:]
        for item in components.queryItems ?? [] where query[item.name] == nil { query[item.name] = item.value ?? "" }
        if let version = query["v"], version != "1" { throw .unsupportedVersion(version) }
        guard let host = nonEmpty(query["host"]) else { throw .missing("host") }
        guard let token = nonEmpty(query["token"]) else { throw .missing("token") }
        var apiPort = try port(query["api"], name: "api", default: CuebotEndpoint.defaultApiPort)
        var httpPort = try port(query["http"], name: "http", default: CuebotEndpoint.defaultHttpPort)
        var https: Bool?
        if nonEmpty(query["https"]) != nil {
            let httpsPort = try port(query["https"], name: "https", default: 443)
            apiPort = httpsPort
            httpPort = httpsPort
            https = true
        }
        return CuebotEndpoint(
            name: nonEmpty(query["name"]) ?? "Cuebot",
            host: host,
            apiPort: apiPort,
            httpPort: httpPort,
            token: token,
            deckToken: nonEmpty(query["deckToken"]),
            instanceId: nonEmpty(query["id"]),
            https: https)
    }

    private static func nonEmpty(_ value: String?) -> String? {
        guard let value = value?.trimmingCharacters(in: .whitespaces), !value.isEmpty else { return nil }
        return value
    }

    private static func port(_ value: String?, name: String, default fallback: Int) throws(Failure) -> Int {
        guard let value = nonEmpty(value) else { return fallback }
        guard let port = Int(value), (1...65535).contains(port) else { throw .badPort(name) }
        return port
    }
}
