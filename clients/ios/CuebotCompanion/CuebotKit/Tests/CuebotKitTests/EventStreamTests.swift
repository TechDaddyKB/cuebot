import Foundation
import Network
import Testing
@testable import CuebotKit

/// A tiny HTTP server on loopback: each connection gets the response the handler returns for its request (by the
/// connection's number, from 0), then the connection is closed, as when Cuebot restarts.
final class StubEventServer: @unchecked Sendable {
    struct Request: Sendable {
        var head: String
        func header(_ name: String) -> String? {
            head.split(separator: "\r\n").first { $0.lowercased().hasPrefix(name.lowercased() + ":") }
                .map { String($0.drop { $0 != ":" }.dropFirst()).trimmingCharacters(in: .whitespaces) }
        }
    }

    private let listener: NWListener
    private let queue = DispatchQueue(label: "stub-sse")
    private let lock = NSLock()
    private var count = 0
    private var _requests: [Request] = []
    private(set) var port = 0

    var requests: [Request] { lock.withLock { _requests } }

    init(handler: @escaping @Sendable (Int, Request) -> String) throws {
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
        let ready = DispatchSemaphore(value: 0)
        listener.stateUpdateHandler = { if case .ready = $0 { ready.signal() } }
        listener.newConnectionHandler = { [unowned self] connection in
            connection.start(queue: queue)
            receive(connection, buffer: Data(), handler: handler)
        }
        listener.start(queue: queue)
        _ = ready.wait(timeout: .now() + 5)
        port = Int(listener.port?.rawValue ?? 0)
    }

    private func receive(_ connection: NWConnection, buffer: Data, handler: @escaping @Sendable (Int, Request) -> String) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [self] data, _, done, _ in
            let buffer = buffer + (data ?? Data())
            guard let end = buffer.range(of: Data("\r\n\r\n".utf8)) else {
                if !done { receive(connection, buffer: buffer, handler: handler) }
                return
            }
            let request = Request(head: String(decoding: buffer[..<end.lowerBound], as: UTF8.self))
            let number = lock.withLock { () -> Int in
                _requests.append(request)
                count += 1
                return count - 1
            }
            connection.send(content: Data(handler(number, request).utf8), completion: .contentProcessed { _ in connection.cancel() })
        }
    }

    deinit { listener.cancel() }

    static func sse(_ body: String) -> String {
        "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n" + body
    }

    static func status(_ code: Int) -> String { "HTTP/1.1 \(code) Nope\r\nContent-Length: 0\r\nConnection: close\r\n\r\n" }
}

struct EventStreamTests {
    private static let fast = Backoff(steps: [0.05, 0.05], then: 0.05)

    @Test func reconnectsAfterTheServerGoesAwayAndResumesFromTheLastId() async throws {
        let server = try StubEventServer { number, _ in
            switch number {
            case 0: StubEventServer.sse(": connected\n\nid: 1\nevent: twitch.follow\ndata: {\"type\":\"twitch.follow\",\"args\":{\"user\":\"ann\"}}\n\n")
            case 1: StubEventServer.status(503)
            default: StubEventServer.sse(": connected\n\nid: 2\nevent: twitch.cheer\ndata: {\"type\":\"twitch.cheer\",\"args\":{\"bits\":5}}\n\n")
            }
        }
        let client = CuebotClient(endpoint: CuebotEndpoint(host: "127.0.0.1", apiPort: server.port, token: "tok"))
        let states = Recorder<StreamState>()
        var events: [ServerEvent] = []
        for try await event in client.apiEvents(types: ["twitch.follow", "twitch.cheer"], backoff: Self.fast, onState: states.add) {
            events.append(event)
            if events.count == 2 { break }
        }
        #expect(events.map(\.type) == ["twitch.follow", "twitch.cheer"])
        #expect(events[1].apiEvent.args["bits"]?.double == 5)
        let requests = server.requests
        #expect(requests[0].head.hasPrefix("GET /api/v1/events?types=twitch.follow,twitch.cheer "))
        #expect(requests[0].header("Authorization") == "Bearer tok")
        #expect(requests[0].header("Last-Event-ID") == nil)
        #expect(requests[1].header("Last-Event-ID") == "1")
        #expect(requests[2].header("Last-Event-ID") == "1")
        #expect(states.values.contains(.connected))
        #expect(states.values.contains { if case .reconnecting = $0 { true } else { false } })
    }

    @Test func aRejectedTokenEndsTheStreamInsteadOfRetrying() async throws {
        let server = try StubEventServer { _, _ in StubEventServer.status(401) }
        let client = CuebotClient(endpoint: CuebotEndpoint(host: "127.0.0.1", apiPort: server.port, token: "old"))
        await #expect(throws: CuebotError.unauthorized) {
            for try await _ in client.apiEvents(backoff: Self.fast) {}
        }
        #expect(server.requests.count == 1)
    }

    @Test func deckEventsCarryStateAndNotify() async throws {
        let server = try StubEventServer { _, request in
            request.head.hasPrefix("GET /deck/d1/events ")
                ? StubEventServer.sse("event: state\ndata: {\"id\":\"d1\",\"rows\":2,\"columns\":3,\"items\":[]}\n\n: keep-alive\n\nevent: notify\ndata: {\"title\":\"Hi\",\"description\":\"there\"}\n\n")
                : StubEventServer.status(404)
        }
        let client = CuebotClient(endpoint: CuebotEndpoint(host: "127.0.0.1", httpPort: server.port, token: "t", deckToken: "dk"))
        var events: [ServerEvent] = []
        for try await event in client.deckEvents("d1", backoff: Self.fast) {
            events.append(event)
            if events.count == 2 { break }
        }
        #expect(try events[0].decode(DeckView.self).columns == 3)
        #expect(try events[1].decode(DeckNotification.self) == DeckNotification(title: "Hi", description: "there"))
        #expect(server.requests[0].header("X-Cuebot-Deck-Token") == "dk")
    }
}

final class Recorder<T: Sendable>: @unchecked Sendable {
    private let lock = NSLock()
    private var _values: [T] = []
    var values: [T] { lock.withLock { _values } }
    func add(_ value: T) { lock.withLock { _values.append(value) } }
}
