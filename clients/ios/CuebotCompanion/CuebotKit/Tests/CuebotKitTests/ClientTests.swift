import Foundation
import Testing
@testable import CuebotKit

/// Answers requests from a table and records them; one per test (keyed by the host the test uses).
final class StubProtocol: URLProtocol, @unchecked Sendable {
    struct Reply: Sendable {
        var status = 200
        var body = ""
    }

    nonisolated(unsafe) private static var replies: [String: @Sendable (URLRequest) -> Reply] = [:]
    nonisolated(unsafe) private static var seen: [String: [URLRequest]] = [:]
    private static let lock = NSLock()

    static func session(host: String, reply: @escaping @Sendable (URLRequest) -> Reply) -> URLSession {
        lock.withLock {
            replies[host] = reply
            seen[host] = []
        }
        let configuration = URLSessionConfiguration.ephemeral
        configuration.protocolClasses = [StubProtocol.self]
        return URLSession(configuration: configuration)
    }

    static func requests(host: String) -> [URLRequest] { lock.withLock { seen[host] ?? [] } }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        var request = self.request
        if request.httpBody == nil, let stream = request.httpBodyStream {   // URLSession moves bodies into a stream
            stream.open()
            var data = Data()
            var buffer = [UInt8](repeating: 0, count: 4096)
            while stream.hasBytesAvailable, case let n = stream.read(&buffer, maxLength: buffer.count), n > 0 { data.append(buffer, count: n) }
            stream.close()
            request.httpBody = data
        }
        let host = request.url?.host ?? ""
        let handler = Self.lock.withLock { () -> (@Sendable (URLRequest) -> Reply)? in
            Self.seen[host, default: []].append(request)
            return Self.replies[host]
        }
        let reply = handler?(request) ?? Reply(status: 500)
        let response = HTTPURLResponse(url: request.url!, statusCode: reply.status, httpVersion: "HTTP/1.1", headerFields: nil)!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        client?.urlProtocol(self, didLoad: Data(reply.body.utf8))
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}
}

struct ClientTests {
    private func client(_ host: String, deckToken: String? = nil, reply: @escaping @Sendable (URLRequest) -> StubProtocol.Reply) -> CuebotClient {
        CuebotClient(endpoint: CuebotEndpoint(host: host, token: "secret", deckToken: deckToken),
                     session: StubProtocol.session(host: host, reply: reply))
    }

    private func json(_ request: URLRequest) -> JSONValue {
        (try? JSONDecoder().decode(JSONValue.self, from: request.httpBody ?? Data())) ?? .null
    }

    @Test func restCallsCarryTheBearerTokenToTheApiPort() async throws {
        let host = "rest.test"
        let client = client(host) { request in
            switch request.url?.path {
            case "/api/v1/status": .init(body: #"{"app":{"instanceId":"i1"},"connections":[]}"#)
            case "/api/v1/actions": .init(body: #"{"actions":[{"id":"a1","name":"BRB","group":"Scenes","enabled":true,"queue":""}],"count":1}"#)
            case "/api/v1/decks": .init(body: #"[{"id":"d1","name":"Main","rows":2,"columns":4,"pageCount":3}]"#)
            default: .init(status: 204)
            }
        }
        #expect(try await client.status().app.instanceId == "i1")
        #expect(try await client.actions().map(\.name) == ["BRB"])
        #expect(try await client.decks() == [DeckSummary(id: "d1", name: "Main", rows: 2, columns: 4, pageCount: 3)])
        try await client.runAction("a 1/x")
        let requests = StubProtocol.requests(host: host)
        #expect(requests.allSatisfy { $0.url?.port == 7475 && $0.value(forHTTPHeaderField: "Authorization") == "Bearer secret" })
        #expect(requests.last?.httpMethod == "POST")
        #expect(requests.last?.url?.absoluteString == "http://rest.test:7475/api/v1/actions/a%201%2Fx/run")
    }

    @Test func controlsPostTheDocumentedBodies() async throws {
        let host = "controls.test"
        let client = client(host) { _ in .init(status: 204) }
        try await client.subathon("Main timer", .pause)
        try await client.subathonTime("t1", add: 60)
        try await client.subathonTime("t1", set: 3600)
        try await client.subathonMultiplier("t1", 2)
        try await client.goalPoints("g1", add: 5)
        try await client.goalMultiplier("g1", 0.5)
        try await client.goalReset("g1")
        let requests = StubProtocol.requests(host: host)
        #expect(requests.map { $0.url!.path } == [
            "/api/v1/subathon/Main timer/pause", "/api/v1/subathon/t1/time", "/api/v1/subathon/t1/time",
            "/api/v1/subathon/t1/multiplier", "/api/v1/goals/g1/points", "/api/v1/goals/g1/multiplier", "/api/v1/goals/g1/reset",
        ])
        #expect(requests.map(json) == [
            .null, ["add": .number(60)], ["set": .number(3600)], ["value": .number(2)], ["add": .number(5)], ["value": .number(0.5)], .null,
        ])
    }

    @Test func deckCallsGoToTheHttpPortWithTheDeckToken() async throws {
        let host = "deck.test"
        let client = client(host, deckToken: "dk") { request in
            request.url?.path == "/deck/d1/state" ? .init(body: #"{"id":"d1","rows":2,"columns":2,"items":[]}"#) : .init(status: 204)
        }
        #expect(try await client.deckState("d1").rows == 2)
        try await client.press(deck: "d1", item: "i9")
        try await client.page(deck: "d1", delta: -1)
        try await client.page(deck: "d1", page: 2)
        let requests = StubProtocol.requests(host: host)
        #expect(requests.allSatisfy { $0.url?.port == 7474 && $0.value(forHTTPHeaderField: "X-Cuebot-Deck-Token") == "dk" })
        #expect(requests.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == nil })
        #expect(requests.dropFirst().map(json) == [["item": .string("i9")], ["delta": .number(-1)], ["page": .number(2)]])
    }

    @Test func statusCodesBecomeErrorsTheAppCanActOn() async {
        let client = client("errors.test") { request in
            switch request.url?.path {
            case "/api/v1/status": .init(status: 401, body: "Missing or invalid token")
            case "/api/v1/dashboard": .init(status: 404)
            case "/api/v1/decks": .init(status: 500, body: "boom")
            default: .init(body: "not json")
            }
        }
        await #expect(throws: CuebotError.unauthorized) { try await client.status() }
        await #expect(throws: CuebotError.notFound) { try await client.dashboard() }
        await #expect(throws: CuebotError.http(status: 500, message: "boom")) { try await client.decks() }
        await #expect { try await client.actions() } throws: { ($0 as? CuebotError).map { if case .invalidResponse = $0 { true } else { false } } ?? false }
    }

    @Test func anUnreachableHostSaysSo() async {
        let client = CuebotClient(endpoint: CuebotEndpoint(host: "127.0.0.1", apiPort: 9, token: "t"))
        await #expect { try await client.status() } throws: { ($0 as? CuebotError).map { if case .unreachable = $0 { true } else { false } } ?? false }
    }
}

extension JSONValue: ExpressibleByDictionaryLiteral {
    public init(dictionaryLiteral elements: (String, JSONValue)...) { self = .object(Dictionary(uniqueKeysWithValues: elements)) }
}
