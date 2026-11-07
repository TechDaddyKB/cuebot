import Network
import XCTest

/// The Dashboard's profile switcher against a fake Cuebot running in the test process.
final class ProfileSwitchTests: XCTestCase {
    override func setUp() { continueAfterFailure = false }

    private let wait: TimeInterval = 30

    @MainActor
    private func launch(_ server: FakeProfilesCuebot) -> XCUIApplication {
        let app = XCUIApplication()
        let link = "cuebot://pair?v=1&name=Fake&host=127.0.0.1&api=\(server.port)&http=\(server.port)&token=test-token"
        app.launchArguments = ["-resetPairing", "-pair", link, "-tab", "dashboard"]
        app.launch()
        return app
    }

    @MainActor
    func testSwitchingWhileLiveAsksTwiceThenReconnectsOnTheOtherProfile() throws {
        let server = try FakeProfilesCuebot(live: ["Twitch"])
        defer { server.stop() }
        let app = launch(server)

        let row = app.buttons["profile-row"]
        XCTAssertTrue(row.waitForExistence(timeout: wait))
        XCTAssertTrue(row.label.contains("Main"), row.label)
        row.tap()

        let nightOwl = app.buttons["profile-night"]
        XCTAssertTrue(nightOwl.waitForExistence(timeout: wait))
        XCTAssertTrue(app.buttons["profile-main"].isSelected)
        nightOwl.tap()

        let confirm = app.alerts["Switch to Night Owl?"]
        XCTAssertTrue(confirm.waitForExistence(timeout: wait))
        XCTAssertTrue(confirm.staticTexts["Cuebot restarts on that profile; it takes a few seconds."].exists)
        confirm.buttons["profile-switch-confirm"].firstMatch.tap()

        let force = app.alerts["You are live on Twitch. Switch anyway?"]
        XCTAssertTrue(force.waitForExistence(timeout: wait))
        force.buttons["profile-switch-force"].firstMatch.tap()

        XCTAssertTrue(app.descendants(matching: .any)["profile-switching"].waitForExistence(timeout: wait))
        XCTAssertTrue(app.staticTexts["Switching to Night Owl…"].exists)

        let switched = NSPredicate(format: "label CONTAINS %@", "Night Owl")
        expectation(for: switched, evaluatedWith: app.buttons["profile-row"])
        waitForExpectations(timeout: wait)
        XCTAssertFalse(app.descendants(matching: .any)["profile-switching"].exists)
        XCTAssertEqual(server.activations, ["night force=false", "night force=true"])
        XCTAssertGreaterThanOrEqual(server.dashboardFetches(for: "night"), 1, "the dashboard loads again after the switch")
    }

    @MainActor
    func testNoRowWithOneProfileOrWhenCuebotHasNoProfiles() throws {
        for server in [try FakeProfilesCuebot(profileCount: 1), try FakeProfilesCuebot(unavailable: true)] {
            defer { server.stop() }
            let app = launch(server)
            XCTAssertTrue(app.staticTexts["Quick actions"].waitForExistence(timeout: wait))
            XCTAssertTrue(server.profileFetches > 0)
            XCTAssertFalse(app.buttons["profile-row"].exists)
            app.terminate()
        }
    }
}

/// Just enough of Cuebot's REST API for the Dashboard and profiles, on one port for the API and the deck server.
/// A switch drops every request for a moment, like Cuebot restarting, then answers on the other profile.
final class FakeProfilesCuebot: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "fake-profiles-cuebot")
    private var streams: [NWConnection] = []
    private var open = "main"
    private var downUntil = Date.distantPast
    private let live: [String]
    private let profiles: [(id: String, name: String, color: String)]
    private let unavailable: Bool
    private var _activations: [String] = []
    private var _profileFetches = 0
    private var _dashboardFetches: [String: Int] = [:]
    private(set) var port: UInt16 = 0

    var activations: [String] { queue.sync { _activations } }
    var profileFetches: Int { queue.sync { _profileFetches } }
    func dashboardFetches(for profile: String) -> Int { queue.sync { _dashboardFetches[profile] ?? 0 } }

    init(live: [String] = [], profileCount: Int = 2, unavailable: Bool = false) throws {
        self.live = live
        self.unavailable = unavailable
        profiles = Array([("main", "Main", "#22C55E"), ("night", "Night Owl", "#8B5CF6")].prefix(profileCount))
        let parameters = NWParameters.tcp
        parameters.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: .any)
        listener = try NWListener(using: parameters)
        let ready = DispatchSemaphore(value: 0)
        listener.stateUpdateHandler = { if case .ready = $0 { ready.signal() } }
        listener.newConnectionHandler = { [weak self] connection in
            guard let self else { return }
            connection.start(queue: queue)
            receive(connection, buffer: Data())
        }
        listener.start(queue: queue)
        guard ready.wait(timeout: .now() + 10) == .success, let port = listener.port?.rawValue else { throw URLError(.cannotConnectToHost) }
        self.port = port
    }

    func stop() {
        listener.cancel()
        queue.sync { streams.forEach { $0.cancel() } }
    }

    private func receive(_ connection: NWConnection, buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 65536) { [weak self] data, _, done, error in
            guard let self else { return }
            var buffer = buffer
            if let data { buffer.append(data) }
            if let end = buffer.range(of: Data("\r\n\r\n".utf8)) {
                let head = String(decoding: buffer[..<end.lowerBound], as: UTF8.self)
                let length = head.split(separator: "\r\n").first { $0.lowercased().hasPrefix("content-length:") }
                    .flatMap { Int($0.split(separator: ":")[1].trimmingCharacters(in: .whitespaces)) } ?? 0
                let body = buffer[end.upperBound...]
                if body.count >= length {
                    handle(connection, head: head, body: Data(body.prefix(length)))
                    return
                }
            }
            if done || error != nil { connection.cancel() } else { receive(connection, buffer: buffer) }
        }
    }

    private func handle(_ connection: NWConnection, head: String, body: Data) {
        let words = head.split(separator: " ")
        let method = String(words.first ?? ""), target = words.count > 1 ? String(words[1]) : "/"
        let path = String(target.split(separator: "?").first ?? "")
        if Date() < downUntil {   // restarting: nobody answers
            connection.cancel()
            return
        }
        switch (method, path) {
        case ("GET", "/api/v1/events"):
            streams.append(connection)
            connection.send(content: Data("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n\r\n: hi\n\n".utf8),
                            completion: .contentProcessed { _ in })
            return
        case ("GET", "/api/v1/status"):
            reply(connection, 200, #"{"app":{"instanceId":"i-\#(open)"},"connections":[]}"#)
        case ("GET", "/api/v1/dashboard"):
            _dashboardFetches[open, default: 0] += 1
            reply(connection, 200, #"{"instanceId":"i-\#(open)","revision":1,"connections":[]}"#)
        case ("GET", "/api/v1/profiles"):
            _profileFetches += 1
            if unavailable { reply(connection, 503, #"{"error":"Profiles are not available"}"#); return }
            reply(connection, 200, profilesJSON())
        case ("POST", let p) where p.hasPrefix("/api/v1/profiles/") && p.hasSuffix("/activate"):
            let id = p.dropFirst("/api/v1/profiles/".count).dropLast("/activate".count)
            let force = String(decoding: body, as: UTF8.self).replacingOccurrences(of: " ", with: "").contains(#""force":true"#)
            _activations.append("\(id) force=\(force)")
            guard profiles.contains(where: { $0.id == id }) else { reply(connection, 404, #"{"error":"No profile"}"#); return }
            if id == open { reply(connection, 200, #"{"result":"alreadyOpen"}"#); return }
            if !live.isEmpty && !force {
                reply(connection, 409, #"{"error":"Live on \#(live.joined(separator: ", ")); send force true to switch anyway"}"#)
                return
            }
            reply(connection, 200, #"{"result":"switching"}"#)
            open = String(id)
            downUntil = Date().addingTimeInterval(2)
            streams.forEach { $0.cancel() }
            streams = []
        case ("GET", "/api/v1/decks"), ("GET", "/api/v1/actions"):
            reply(connection, 200, "[]")
        default:
            reply(connection, 404, #"{"error":"Not found"}"#)
        }
    }

    private func profilesJSON() -> String {
        func one(_ p: (id: String, name: String, color: String)) -> String {
            #"{"id":"\#(p.id)","name":"\#(p.name)","color":"\#(p.color)","open":\#(p.id == open)}"#
        }
        let current = profiles.first { $0.id == open } ?? profiles[0]
        let live = self.live.map { #""\#($0)""# }.joined(separator: ",")
        return #"{"current":\#(one(current)),"profiles":[\#(profiles.map(one).joined(separator: ","))],"live":[\#(live)],"switching":false}"#
    }

    private func reply(_ connection: NWConnection, _ status: Int, _ body: String) {
        let data = Data(body.utf8)
        let head = "HTTP/1.1 \(status) X\r\nContent-Type: application/json\r\nContent-Length: \(data.count)\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + data, completion: .contentProcessed { _ in connection.cancel() })
    }
}
