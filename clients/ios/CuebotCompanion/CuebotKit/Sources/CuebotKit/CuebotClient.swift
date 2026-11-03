import Foundation

/// Why a call to Cuebot failed, in terms the app can act on.
public enum CuebotError: Error, Equatable, Sendable {
    /// No connection (Cuebot not running, "Allow LAN" off, another network, Local Network access denied).
    case unreachable(String)
    /// 401: the token is wrong or was rotated; pair again.
    case unauthorized
    /// 404: an unknown item, or an endpoint this Cuebot is too old to have.
    case notFound
    /// 429: too many event streams are open.
    case tooManyStreams
    case http(status: Int, message: String)
    case invalidResponse(String)
}

/// Talks to one paired Cuebot: the REST API (`apiPort`, Bearer token) and the deck routes of the HTTP server
/// (`httpPort`, `X-Cuebot-Deck-Token` when a deck token is set).
public actor CuebotClient {
    public nonisolated let endpoint: CuebotEndpoint
    private let session: URLSession
    private let decoder = JSONDecoder()

    public init(endpoint: CuebotEndpoint, session: URLSession = .shared) {
        self.endpoint = endpoint
        self.session = session
    }

    // MARK: REST API

    public func status() async throws(CuebotError) -> StatusResponse { try await get(api("/api/v1/status")) }

    public func dashboard() async throws(CuebotError) -> Dashboard { try await get(api("/api/v1/dashboard")) }

    public func actions() async throws(CuebotError) -> [ActionSummary] {
        let list: Listed<ActionSummary> = try await get(api("/api/v1/actions"))
        return list.items(key: "actions")
    }

    public func decks() async throws(CuebotError) -> [DeckSummary] {
        let list: Listed<DeckSummary> = try await get(api("/api/v1/decks"))
        return list.items(key: "decks")
    }

    public func runAction(_ id: String, args: [String: String] = [:]) async throws(CuebotError) {
        try await send(api("/api/v1/actions/\(escaped(id))/run"), method: "POST", body: args.isEmpty ? nil : ["args": .object(args.mapValues(JSONValue.string))])
    }

    public enum SubathonCommand: String, Sendable { case start, pause, resume, toggle, reset }

    public func subathon(_ timer: String, _ command: SubathonCommand) async throws(CuebotError) {
        try await send(api("/api/v1/subathon/\(escaped(timer))/\(command.rawValue)"), method: "POST")
    }

    /// Adds `add` seconds (negative takes time off) or sets the time left to `set` seconds.
    public func subathonTime(_ timer: String, add: Double? = nil, set: Double? = nil) async throws(CuebotError) {
        try await send(api("/api/v1/subathon/\(escaped(timer))/time"), method: "POST", body: Self.addOrSet(add, set))
    }

    public func subathonMultiplier(_ timer: String, _ value: Double) async throws(CuebotError) {
        try await send(api("/api/v1/subathon/\(escaped(timer))/multiplier"), method: "POST", body: ["value": .number(value)])
    }

    public func goalPoints(_ goal: String, add: Double? = nil, set: Double? = nil) async throws(CuebotError) {
        try await send(api("/api/v1/goals/\(escaped(goal))/points"), method: "POST", body: Self.addOrSet(add, set))
    }

    public func goalMultiplier(_ goal: String, _ value: Double) async throws(CuebotError) {
        try await send(api("/api/v1/goals/\(escaped(goal))/multiplier"), method: "POST", body: ["value": .number(value)])
    }

    public func goalReset(_ goal: String) async throws(CuebotError) {
        try await send(api("/api/v1/goals/\(escaped(goal))/reset"), method: "POST")
    }

    // MARK: Decks (HTTP server)

    public func deckState(_ deck: String) async throws(CuebotError) -> DeckView { try await get(deckRequest(deck, "state")) }

    public func press(deck: String, item: String) async throws(CuebotError) {
        try await send(deckRequest(deck, "press"), method: "POST", body: ["item": .string(item)])
    }

    public func page(deck: String, delta: Int) async throws(CuebotError) {
        try await send(deckRequest(deck, "page"), method: "POST", body: ["delta": .number(Double(delta))])
    }

    public func page(deck: String, page: Int) async throws(CuebotError) {
        try await send(deckRequest(deck, "page"), method: "POST", body: ["page": .number(Double(page))])
    }

    // MARK: Event streams

    /// `/api/v1/events` (optionally only some event types), reconnecting with backoff until cancelled.
    public nonisolated func apiEvents(types: [String] = [], backoff: Backoff = .standard,
                                      onState: @escaping @Sendable (StreamState) -> Void = { _ in }) -> AsyncThrowingStream<ServerEvent, Error> {
        let endpoint = endpoint
        return EventStream.events(session: session, backoff: backoff, onState: onState) { lastId in
            var request = Self.apiRequest(endpoint, "/api/v1/events", query: types.isEmpty ? [] : [URLQueryItem(name: "types", value: types.joined(separator: ","))])
            Self.prepareStream(&request, lastId: lastId)
            return request
        }
    }

    /// `/deck/{id}/events`: `state` (the full view, first on connect) and `notify` events.
    public nonisolated func deckEvents(_ deck: String, backoff: Backoff = .standard,
                                       onState: @escaping @Sendable (StreamState) -> Void = { _ in }) -> AsyncThrowingStream<ServerEvent, Error> {
        let endpoint = endpoint
        return EventStream.events(session: session, backoff: backoff, onState: onState) { lastId in
            var request = Self.deckRequest(endpoint, deck, "events")
            Self.prepareStream(&request, lastId: lastId)
            return request
        }
    }

    // MARK: Requests

    private func api(_ path: String) -> URLRequest { Self.apiRequest(endpoint, path) }

    private func deckRequest(_ deck: String, _ route: String) -> URLRequest { Self.deckRequest(endpoint, deck, route) }

    static func apiRequest(_ endpoint: CuebotEndpoint, _ path: String, query: [URLQueryItem] = []) -> URLRequest {
        var components = URLComponents(url: endpoint.apiBase, resolvingAgainstBaseURL: false)!
        components.percentEncodedPath = path
        if !query.isEmpty { components.queryItems = query }
        var request = URLRequest(url: components.url!, timeoutInterval: 10)
        request.setValue("Bearer \(endpoint.token)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        return request
    }

    static func deckRequest(_ endpoint: CuebotEndpoint, _ deck: String, _ route: String) -> URLRequest {
        var components = URLComponents(url: endpoint.httpBase, resolvingAgainstBaseURL: false)!
        components.percentEncodedPath = "/deck/\(escaped(deck))/\(route)"
        var request = URLRequest(url: components.url!, timeoutInterval: 10)
        if let token = endpoint.deckToken, !token.isEmpty { request.setValue(token, forHTTPHeaderField: "X-Cuebot-Deck-Token") }
        return request
    }

    private static func prepareStream(_ request: inout URLRequest, lastId: String?) {
        request.timeoutInterval = 60 * 60 * 24   // idle between events is normal; keep-alives arrive every few seconds
        request.setValue("text/event-stream", forHTTPHeaderField: "Accept")
        request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
        if let lastId { request.setValue(lastId, forHTTPHeaderField: "Last-Event-ID") }
    }

    private static func escaped(_ segment: String) -> String {
        segment.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed.subtracting(CharacterSet(charactersIn: "/?#"))) ?? segment
    }

    private nonisolated func escaped(_ segment: String) -> String { Self.escaped(segment) }

    private static func addOrSet(_ add: Double?, _ set: Double?) -> [String: JSONValue] {
        if let set { return ["set": .number(set)] }
        return ["add": .number(add ?? 0)]
    }

    private func get<T: Decodable>(_ request: URLRequest) async throws(CuebotError) -> T {
        let data = try await perform(request)
        do { return try decoder.decode(T.self, from: data) }
        catch { throw .invalidResponse(String(describing: error)) }
    }

    private func send(_ request: URLRequest, method: String, body: [String: JSONValue]? = nil) async throws(CuebotError) {
        var request = request
        request.httpMethod = method
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try? JSONEncoder().encode(body)
        }
        _ = try await perform(request)
    }

    private func perform(_ request: URLRequest) async throws(CuebotError) -> Data {
        let data: Data
        let response: URLResponse
        do { (data, response) = try await session.data(for: request) }
        catch { throw .unreachable(error.localizedDescription) }
        guard let http = response as? HTTPURLResponse else { throw .invalidResponse("not HTTP") }
        if let failure = Self.failure(status: http.statusCode, body: data) { throw failure }
        return data
    }

    static func failure(status: Int, body: Data) -> CuebotError? {
        switch status {
        case 200..<300: return nil
        case 401: return .unauthorized
        case 403 where String(decoding: body, as: UTF8.self).localizedCaseInsensitiveContains("token"): return .unauthorized
        case 404: return .notFound
        case 429: return .tooManyStreams
        default:
            let text = String(decoding: body.prefix(300), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
            return .http(status: status, message: text)
        }
    }
}

/// A list response: either a bare array or an object holding it under a key (`{"actions": [...], "count": 3}`).
private struct Listed<Item: Decodable>: Decodable {
    private var array: [Item]?
    private var object: [String: JSONValue]?

    init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if let array = try? container.decode([Item].self) { self.array = array; return }
        object = try container.decode([String: JSONValue].self)
    }

    func items(key: String) -> [Item] {
        if let array { return array }
        guard let value = object?[key], let data = try? JSONEncoder().encode(value) else { return [] }
        return (try? JSONDecoder().decode([Item].self, from: data)) ?? []
    }
}
