import Foundation
import Testing
@testable import CuebotKit

struct PortalFlowTests {
    let relay = "fyrsbvvtain3yaljh3d4eu5ka25jvkjt"
    let key = "LiMg1rMCG7wBaT7HwlOqBrqaqTOGyoa7JxAEw-mJEuc"
    let secret = String(repeating: "x", count: 43)

    @Test func inviteLinksNameTheRelayNextToThePortal() {
        let t = PortalTarget.parse("https://portal.techdaddydigital.com/#i=\(secret)&r=\(relay)&k=\(key)")
        #expect(t == PortalTarget(relay: relay, fingerprint: key, relayOrigin: "wss://relay.techdaddydigital.com", invite: secret))
        #expect(PortalTarget.parse("https://cuebot-portal.example.workers.dev/#r=\(relay)&k=\(key)")?.relayOrigin == "wss://cuebot-portal.example.workers.dev")
        #expect(PortalTarget.parse(" https://portal.techdaddydigital.com/#r=\(relay)&k=\(key)\n")?.invite == nil)
    }

    @Test func anythingElseIsNotAnInvite() {
        #expect(PortalTarget.parse("http://portal.techdaddydigital.com/#i=\(secret)&r=\(relay)&k=\(key)") == nil)
        #expect(PortalTarget.parse("https://portal.techdaddydigital.com/?r=\(relay)&k=\(key)") == nil)
        #expect(PortalTarget.parse("https://portal.techdaddydigital.com/#r=\(relay.uppercased())&k=\(key)") == nil)
        #expect(PortalTarget.parse("https://portal.techdaddydigital.com/#i=short&r=\(relay)&k=\(key)") == nil)
        #expect(PortalTarget.parse("cuebot://pair?v=1") == nil)
        #expect(PortalTarget.parse("not a link") == nil)
    }

    let google = PortalProvider(id: "google", name: "Google", authorize: "https://accounts.google.com/o/oauth2/v2/auth?response_type=code&client_id=c", pkce: true)

    private func query(_ url: URL, _ name: String) -> String? {
        URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == name }?.value
    }

    @Test func providersComeFromCuebotsAnswerAndOnlyKnownHostsAreOpened() throws {
        let answer = try JSONDecoder().decode(PortalAnswer.self, from: Data("""
            {"ok":true,"providers":[{"id":"google","name":"Google","authorize":"\(google.authorize)","pkce":true},
             {"id":"twitch","name":"Twitch","authorize":"https://id.twitch.tv/oauth2/authorize?client_id=t","pkce":false},{"name":"broken"}]}
            """.utf8))
        let providers = PortalProvider.list(answer)
        #expect(providers.map(\.id) == ["google", "twitch"])

        let pending = PortalSignIn.start(PortalTarget(relay: relay, fingerprint: key, relayOrigin: "wss://relay.example", invite: secret), google)
        #expect(pending.state.hasPrefix("app."))
        let url = try PortalSignIn.authorizeURL(google, pending)
        #expect(query(url, "state") == pending.state)
        #expect(query(url, "client_id") == "c")
        #expect(query(url, "code_challenge") == PortalSignIn.challenge(pending.verifier))
        #expect(query(url, "code_challenge_method") == "S256")
        #expect(query(try PortalSignIn.authorizeURL(providers[1], pending), "code_challenge") == nil)
        var evil = google
        evil.authorize = "https://evil.example/auth"
        #expect(throws: PortalSignIn.NotAllowed.self) { try PortalSignIn.authorizeURL(evil, pending) }
        evil.authorize = "http://accounts.google.com/auth"
        #expect(throws: PortalSignIn.NotAllowed.self) { try PortalSignIn.authorizeURL(evil, pending) }
        // RFC 7636, appendix B.
        #expect(PortalSignIn.challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM")
    }

    @Test func theReturnHandOverIsCheckedAgainstTheSignInThisAppStarted() {
        let pending = PendingSignIn(target: PortalTarget(relay: relay, fingerprint: key, relayOrigin: "wss://relay.example"), provider: "google", state: "app.abc", verifier: "v")
        func read(_ link: String, _ p: PendingSignIn? = nil) -> PortalSignIn.Return { PortalSignIn.readReturn(URL(string: link)!, pending: p) }
        #expect(read("cuebot-portal://signin?code=4%2F0Ab_x&state=app.abc", pending) == .code("4/0Ab_x"))
        #expect(read("cuebot-portal://signin?code=c&state=app.other", pending) == .failed("This sign-in did not come from this app. Start again."))
        #expect(read("cuebot-portal://signin?error=access_denied&state=app.abc", pending) == .failed("The sign-in was cancelled."))
        #expect(read("cuebot-portal://signin?code=c&state=app.abc") == .failed("This sign-in has expired. Open the invite again."))
        #expect(read("cuebot://pair?code=c&state=app.abc", pending) == .failed("That is not a sign-in answer."))
        #expect(read("cuebot-portal://signin?state=app.abc", pending) == .failed("The provider did not send a sign-in code. Start again."))
    }

    @Test func relayCloseCodesHaveWords() {
        #expect(PortalLink.closeReason(4404) == PortalLink.closeReason(4503))
        #expect(PortalLink.closeReason(4404).contains("not online"))
        #expect(PortalLink.closeReason(4403) == "The streamer has closed the mod portal.")
        #expect(PortalLink.closeReason(4429).contains("Too many people"))
        #expect(PortalLink.closeReason(1006) == "The connection to the streamer's Cuebot was lost.")
    }
}

struct PortalApprovalsClientTests {
    @Test func listsAndDecidesChanges() async throws {
        let host = "approvals.test"
        let session = StubProtocol.session(host: host) { request in
            switch (request.httpMethod, request.url?.path) {
            case ("GET", "/api/v1/portal/changes"):
                .init(body: #"{"changes":[{"id":"c1","member":"Ann","area":"commands","kind":"edit","target":"!hug","at":1762300000000,"expires":1762900000000},{"id":"c2","member":"Bo","target":"Alert","summary":"Bo changed the alert"}]}"#)
            case ("POST", "/api/v1/portal/changes/c1/approve"):
                .init(body: #"{"result":"conflict","changes":[{"id":"c1","member":"Ann","target":"!hug"}]}"#)
            case ("POST", "/api/v1/portal/changes/c2/reject"):
                .init(body: #"{"result":"rejected","changes":[]}"#)
            default:
                .init(status: 404)
            }
        }
        let client = CuebotClient(endpoint: CuebotEndpoint(host: host, token: "secret"), session: session)
        let changes = try await client.portalChanges()
        #expect(changes.map(\.id) == ["c1", "c2"])
        #expect(changes[0].summary == "Ann changed '!hug'" && changes[0].expires == 1_762_900_000_000)
        #expect(changes[1].summary == "Bo changed the alert")
        let conflict = try await client.decideChange("c1", approve: true, force: true)
        #expect(conflict.result == .conflict && conflict.pending.count == 1)
        let rejected = try await client.decideChange("c2", approve: false)
        #expect(rejected.result == .rejected && rejected.pending.isEmpty)
        let posts = StubProtocol.requests(host: host).filter { $0.httpMethod == "POST" }
        #expect(posts.map { String(decoding: $0.httpBody ?? Data(), as: UTF8.self) } == [#"{"force":true}"#, #"{"force":false}"#])
        #expect(posts.allSatisfy { $0.value(forHTTPHeaderField: "Authorization") == "Bearer secret" })
        await #expect(throws: CuebotError.notFound) { try await client.decideChange("gone", approve: true) }
    }
}
