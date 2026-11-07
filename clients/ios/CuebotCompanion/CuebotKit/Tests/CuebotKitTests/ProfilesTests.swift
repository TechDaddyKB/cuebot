import Foundation
import Testing
@testable import CuebotKit

struct ProfilesTests {
    private static let answer = #"""
    {"current":{"id":"main","name":"Main","color":"#22C55E","open":true},
     "profiles":[{"id":"main","name":"Main","color":"#22C55E","open":true},{"id":"night-owl","name":"Night Owl","color":"#8B5CF6","open":false}],
     "live":["Twitch"],"switching":false}
    """#

    private func client(_ host: String, reply: @escaping @Sendable (URLRequest) -> StubProtocol.Reply) -> CuebotClient {
        CuebotClient(endpoint: CuebotEndpoint(host: host, token: "secret"), session: StubProtocol.session(host: host, reply: reply))
    }

    @Test func profilesDecodeTheOpenOneAllOfThemAndWhatIsLive() async throws {
        let client = client("profiles.test") { _ in .init(body: Self.answer) }
        let state = try #require(try await client.profiles())
        #expect(state.current == StreamerProfile(id: "main", name: "Main", color: "#22C55E", open: true))
        #expect(state.profiles.map(\.name) == ["Main", "Night Owl"])
        #expect(state.profiles.map(\.open) == [true, false])
        #expect(state.live == ["Twitch"])
        #expect(!state.switching)
        let request = try #require(StubProtocol.requests(host: "profiles.test").first)
        #expect(request.url?.absoluteString == "http://profiles.test:7475/api/v1/profiles")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer secret")
    }

    @Test func aCuebotWithoutProfilesAnswersNil() async throws {
        let unavailable = client("noprofiles.test") { _ in .init(status: 503, body: #"{"error":"Profiles are not available"}"#) }
        #expect(try await unavailable.profiles() == nil)
        let tooOld = client("oldprofiles.test") { _ in .init(status: 404) }
        #expect(try await tooOld.profiles() == nil)
        let broken = client("badprofiles.test") { _ in .init(status: 500, body: "boom") }
        await #expect(throws: CuebotError.http(status: 500, message: "boom")) { try await broken.profiles() }
    }

    @Test func missingFieldsFallBackAndTheOpenProfileStandsInForCurrent() throws {
        let state = try JSONDecoder().decode(ProfilesState.self, from: Data(#"{"profiles":[{"id":"a"},{"id":"b","name":"B","open":true}]}"#.utf8))
        #expect(state.current?.id == "b")
        #expect(state.profiles.first?.name == "a")
        #expect(state.live.isEmpty && !state.switching)
    }

    @Test func activatePostsForceAndReadsTheResult() async throws {
        let host = "activate.test"
        let client = client(host) { request in
            switch request.url?.path {
            case "/api/v1/profiles/main/activate": .init(body: #"{"result":"alreadyOpen"}"#)
            default: .init(body: #"{"result":"switching"}"#)
            }
        }
        #expect(try await client.activateProfile("Night Owl") == .switching)
        #expect(try await client.activateProfile("main", force: true) == .alreadyOpen)
        let requests = StubProtocol.requests(host: host)
        #expect(requests.map(\.httpMethod) == ["POST", "POST"])
        #expect(requests.first?.url?.absoluteString == "http://activate.test:7475/api/v1/profiles/Night%20Owl/activate")
        let bodies = requests.map { (try? JSONDecoder().decode(JSONValue.self, from: $0.httpBody ?? Data())) ?? .null }
        #expect(bodies == [["force": .bool(false)], ["force": .bool(true)]])
    }

    @Test func conflictsSayWhetherTheStreamIsLiveOrASwitchRuns() async throws {
        let client = client("conflict.test") { request in
            request.url?.path.contains("night") == true
                ? .init(status: 409, body: #"{"error":"Live on Twitch, YouTube; send force true to switch anyway"}"#)
                : .init(status: 409, body: #"{"error":"A profile switch is already under way"}"#)
        }
        #expect(try await client.activateProfile("night") == .live("Live on Twitch, YouTube; send force true to switch anyway"))
        #expect(try await client.activateProfile("main") == .busy("A profile switch is already under way"))
        let unknown = self.client("unknownprofile.test") { _ in .init(status: 404, body: #"{"error":"No profile 'x'"}"#) }
        await #expect(throws: CuebotError.notFound) { try await unknown.activateProfile("x") }
    }
}
