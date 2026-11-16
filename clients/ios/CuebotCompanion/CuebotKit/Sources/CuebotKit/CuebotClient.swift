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

    // MARK: Streamer profiles

    /// The profiles, or nil when this Cuebot has none to offer (503: no profiles in this build or tool; 404: too old).
    public func profiles() async throws(CuebotError) -> ProfilesState? {
        do { return try await get(api("/api/v1/profiles")) as ProfilesState }
        catch .notFound { return nil }
        catch .http(status: 503, message: _) { return nil }
    }

    /// Opens another profile (by id or name). Without `force` Cuebot refuses while a stream is live.
    public func activateProfile(_ id: String, force: Bool = false) async throws(CuebotError) -> ProfileActivation {
        do {
            let answer: JSONValue = try await post(api("/api/v1/profiles/\(escaped(id))/activate"), body: ["force": .bool(force)])
            return answer["result"]?.string == "alreadyOpen" ? .alreadyOpen : .switching
        } catch .http(status: 409, let body) {
            // Both refusals are 409; only the live one tells you to send force.
            let message = Self.errorMessage(body)
            return message.localizedCaseInsensitiveContains("force") ? .live(message) : .busy(message)
        }
    }

    // MARK: Chat: the combined chat, sending as the broadcaster or the bot, and moderating as the streamer

    public func chat(limit: Int = 200) async throws(CuebotError) -> ChatSnapshot {
        try await get(Self.apiRequest(endpoint, "/api/v1/chat", query: [URLQueryItem(name: "limit", value: String(limit))]))
    }

    /// Sends to one platform or "All"; the platforms that took it. A refusal (no platform, no bot account) is `.http(409, …)`.
    @discardableResult
    public func sendChat(platform: String, text: String, as sendAs: SendAs = .default) async throws(CuebotError) -> [String] {
        let answer: JSONValue = try await post(api("/api/v1/chat/send"),
                                               body: ["platform": .string(platform), "text": .string(text), "as": .string(sendAs.rawValue)])
        if case .array(let platforms)? = answer["platforms"] { return platforms.compactMap(\.string) }
        return []
    }

    public func chatCapabilities() async throws(CuebotError) -> ChatCapabilities { try await get(api("/api/v1/chat/capabilities")) }

    /// Runs one tool as the streamer. A refusal or failure comes back in the outcome, not as an error.
    public func moderate(platform: String, action: ModAction, messageId: String? = nil, userId: String = "", userName: String = "",
                         durationSeconds: Int? = nil, reason: String? = nil) async throws(CuebotError) -> ModerationOutcome {
        var body: [String: JSONValue] = [
            "platform": .string(platform), "action": .string(action.rawValue), "userId": .string(userId), "userName": .string(userName),
        ]
        if let messageId { body["messageId"] = .string(messageId) }
        if let durationSeconds { body["durationSeconds"] = .number(Double(durationSeconds)) }
        if let reason, !reason.trimmingCharacters(in: .whitespaces).isEmpty { body["reason"] = .string(reason) }
        return try await post(api("/api/v1/chat/moderate"), body: body)
    }

    /// The mod log, newest first; about one account with `target` (a user id).
    public func modLog(target: String? = nil, platform: String? = nil) async throws(CuebotError) -> [ModLogEntry] {
        var query: [URLQueryItem] = []
        if let target { query.append(URLQueryItem(name: "target", value: target)) }
        if let platform { query.append(URLQueryItem(name: "platform", value: platform)) }
        let list: Listed<ModLogEntry> = try await get(Self.apiRequest(endpoint, "/api/v1/chat/modlog", query: query))
        return list.items(key: "entries")
    }

    // MARK: Mod portal approvals: mods' changes waiting for the streamer's OK

    public func portalChanges() async throws(CuebotError) -> [PendingChange] {
        let list: Listed<PendingChange> = try await get(api("/api/v1/portal/changes"))
        return list.items(key: "changes")
    }

    /// Approves (with `force`, even when it changed since the mod asked) or rejects one change; the queue as it is now.
    public func decideChange(_ id: String, approve: Bool, force: Bool = false) async throws(CuebotError) -> ChangeDecision {
        try await post(api("/api/v1/portal/changes/\(escaped(id))/\(approve ? "approve" : "reject")"), body: ["force": .bool(force)])
    }

    public func runAction(_ id: String, args: [String: String] = [:]) async throws(CuebotError) {
        try await send(api("/api/v1/actions/\(escaped(id))/run"), method: "POST", body: args.isEmpty ? nil : ["args": .object(args.mapValues(JSONValue.string))])
    }

    public enum SubathonCommand: String, Sendable { case start, pause, resume, toggle, reset }

    // The subathon and goal controls answer with the whole {timers, goals}, so the app can show the result at once.

    @discardableResult
    public func subathon(_ timer: String, _ command: SubathonCommand) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/subathon/\(escaped(timer))/\(command.rawValue)"))
    }

    /// Adds `add` seconds (negative takes time off) or sets the time left to `set` seconds.
    @discardableResult
    public func subathonTime(_ timer: String, add: Double? = nil, set: Double? = nil) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/subathon/\(escaped(timer))/time"), body: Self.addOrSet(add, set))
    }

    @discardableResult
    public func subathonMultiplier(_ timer: String, _ value: Double) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/subathon/\(escaped(timer))/multiplier"), body: ["value": .number(value)])
    }

    @discardableResult
    public func goalPoints(_ goal: String, add: Double? = nil, set: Double? = nil) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/goals/\(escaped(goal))/points"), body: Self.addOrSet(add, set))
    }

    @discardableResult
    public func goalMultiplier(_ goal: String, _ value: Double) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/goals/\(escaped(goal))/multiplier"), body: ["value": .number(value)])
    }

    @discardableResult
    public func goalReset(_ goal: String) async throws(CuebotError) -> SubathonState {
        try await post(api("/api/v1/goals/\(escaped(goal))/reset"))
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

    /// The Chat tab's events: chat.received, chat.moderated and chat.modLog (Cuebot sends them only when asked by name).
    public nonisolated func chatEvents(backoff: Backoff = .standard,
                                       onState: @escaping @Sendable (StreamState) -> Void = { _ in }) -> AsyncThrowingStream<ServerEvent, Error> {
        apiEvents(types: Self.chatEvents, backoff: backoff, onState: onState)
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

    /// A deck route. With a deck token: the `X-Cuebot-Deck-Token` header on every request, and also `?token=` on the
    /// events stream (Cuebot accepts either; the query form is what browsers use for server-sent events).
    static func deckRequest(_ endpoint: CuebotEndpoint, _ deck: String, _ route: String) -> URLRequest {
        var components = URLComponents(url: endpoint.httpBase, resolvingAgainstBaseURL: false)!
        components.percentEncodedPath = "/deck/\(escaped(deck))/\(route)"
        if route == "events", let token = endpoint.deckToken, !token.isEmpty { components.queryItems = [URLQueryItem(name: "token", value: token)] }
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

    /// A POST whose answer is decoded (an empty answer decodes from `{}`, so tolerant models still work).
    private func post<T: Decodable>(_ request: URLRequest, body: [String: JSONValue]? = nil) async throws(CuebotError) -> T {
        let data = try await perform(prepare(request, method: "POST", body: body))
        do { return try decoder.decode(T.self, from: data.isEmpty ? Data("{}".utf8) : data) }
        catch { throw .invalidResponse(String(describing: error)) }
    }

    private func prepare(_ request: URLRequest, method: String, body: [String: JSONValue]?) -> URLRequest {
        var request = request
        request.httpMethod = method
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try? JSONEncoder().encode(body)
        }
        return request
    }

    private func send(_ request: URLRequest, method: String, body: [String: JSONValue]? = nil) async throws(CuebotError) {
        _ = try await perform(prepare(request, method: method, body: body))
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

    /// The `error` text of a JSON error body (`{"error": "…"}`), else the body itself.
    static func errorMessage(_ body: String) -> String {
        (try? JSONDecoder().decode(JSONValue.self, from: Data(body.utf8)))?["error"]?.string ?? body
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
