import Foundation

/// One server-sent event: its `event:` type (default "message"), its `data:` lines joined with "\n", and its `id:`.
public struct ServerEvent: Hashable, Sendable {
    public var type: String
    public var data: String
    public var id: String?

    public init(type: String = "message", data: String, id: String? = nil) {
        self.type = type
        self.data = data
        self.id = id
    }

    /// The data as JSON (`.null` when it is not JSON).
    public var json: JSONValue { (try? JSONDecoder().decode(JSONValue.self, from: Data(data.utf8))) ?? .null }

    public func decode<T: Decodable>(_ type: T.Type) throws -> T { try JSONDecoder().decode(type, from: Data(data.utf8)) }
}

/// The text/event-stream format (WHATWG HTML, "Server-sent events"), fed with raw bytes: lines end with CR, LF or
/// CRLF; a blank line dispatches the event; lines starting with ":" are comments (Cuebot's ": connected" and
/// ": keep-alive"); `retry:` is honoured by the stream. Not thread-safe: one parser per connection.
public struct ServerSentEventParser: Sendable {
    private var line: [UInt8] = []
    private var lastWasCR = false
    private var type = ""
    private var data: [String] = []
    private var hasData = false
    /// The last `id:` seen (kept across events, as the spec says), sent back as Last-Event-ID on reconnect.
    public private(set) var lastEventId: String?
    /// A `retry:` value in milliseconds, if the server sent one.
    public private(set) var retryMilliseconds: Int?
    /// Comment lines seen, without the colon (": connected" → "connected").
    public private(set) var comments: [String] = []

    public init(lastEventId: String? = nil) { self.lastEventId = lastEventId }

    /// Feeds bytes; returns the events they complete.
    public mutating func feed<S: Sequence>(_ bytes: S) -> [ServerEvent] where S.Element == UInt8 {
        var events: [ServerEvent] = []
        for byte in bytes {
            if byte == 0x0A, lastWasCR {   // the LF of a CRLF
                lastWasCR = false
                continue
            }
            lastWasCR = byte == 0x0D
            if byte == 0x0A || byte == 0x0D {
                if let event = endLine() { events.append(event) }
            } else {
                line.append(byte)
            }
        }
        return events
    }

    private mutating func endLine() -> ServerEvent? {
        let text = String(decoding: line, as: UTF8.self)
        line.removeAll(keepingCapacity: true)
        if text.isEmpty { return dispatch() }
        if text.hasPrefix(":") {
            comments.append(String(text.dropFirst()).trimmingCharacters(in: .whitespaces))
            if comments.count > 16 { comments.removeFirst() }
            return nil
        }
        let field: Substring
        var value: Substring
        if let colon = text.firstIndex(of: ":") {
            field = text[..<colon]
            value = text[text.index(after: colon)...]
            if value.hasPrefix(" ") { value = value.dropFirst() }
        } else {
            field = Substring(text)
            value = ""
        }
        switch field {
        case "event": type = String(value)
        case "data":
            data.append(String(value))
            hasData = true
        case "id" where !value.contains("\0"): lastEventId = String(value)
        case "retry": if let ms = Int(value), ms >= 0 { retryMilliseconds = ms }
        default: break
        }
        return nil
    }

    private mutating func dispatch() -> ServerEvent? {
        defer {
            type = ""
            data.removeAll()
            hasData = false
        }
        guard hasData else { return nil }
        return ServerEvent(type: type.isEmpty ? "message" : type, data: data.joined(separator: "\n"), id: lastEventId)
    }
}

/// Waits between reconnects: 1, 2, 4 and 8 seconds, then every 15 seconds (reset after a connection that worked).
public struct Backoff: Sendable {
    public static let standard = Backoff(steps: [1, 2, 4, 8], then: 15)

    public var steps: [Double]
    public var then: Double

    public init(steps: [Double], then: Double) {
        self.steps = steps
        self.then = then
    }

    /// Seconds to wait before attempt `attempt` (0 = the first retry).
    public func delay(attempt: Int) -> Double { attempt < steps.count ? steps[max(0, attempt)] : then }
}
