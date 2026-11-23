import Foundation
import Testing
@testable import CuebotKit

struct FeedbackTests {
    private let context = FeedbackContext(appVersion: "1.0.6", build: "140.1", system: "26.1", device: "iPhone18,1", formFactor: "Phone",
                                          language: "en-US", appearance: "Dark", connection: "Online", pairedCuebots: 2, modMemberships: 1,
                                          cuebotVersion: "1.0.6", apiVersion: "1")
    private let installId = String(repeating: "a", count: 32)

    private func json(_ request: URLRequest) -> [String: String] {
        (try? JSONSerialization.jsonObject(with: request.httpBody ?? Data())) as? [String: String] ?? [:]
    }

    private func reporter(_ host: String, reply: @escaping @Sendable (URLRequest) -> StubProtocol.Reply) -> FeedbackReporter {
        FeedbackReporter(url: URL(string: "https://\(host)/v1/reports")!, session: StubProtocol.session(host: host, reply: reply))
    }

    @Test func removesPrivateDetails() {
        let text = FeedbackText.scrub("mail me@example.com at 192.168.1.20:7475 or studio.local:7475 cuebot://pair?host=x&token=abc " +
            "token=hunter2 Bearer abc.def ghp_abcdefghijklmnop eyJhbGci.eyJzdWIi.c2lnbmF0dXJl")
        for secret in ["me@example.com", "192.168", "studio.local", "cuebot://", "hunter2", "abc.def", "ghp_", "eyJ"] {
            #expect(!text.contains(secret), "\(secret)")
        }
    }

    @Test func removesEveryKindOfLink() {
        let text = FeedbackText.scrub("See https://discord.com/api/webhooks/123/secret-a and http://example.test/private?id=secret-b " +
            "rtmp://live.example.test/app/secret-c discord.com/api/webhooks/456/secret-d in version 1.0.6")
        for secret in ["secret-a", "secret-b", "secret-c", "secret-d", "discord.com", "example.test"] {
            #expect(!text.contains(secret), "\(secret)")
        }
        #expect(text.contains("version 1.0.6"))
    }

    @Test func postsAProblemWithTheAppDetails() async throws {
        let host = "feedback-problem.test"
        let result = await reporter(host) { _ in .init(status: 201, body: #"{"number":60,"url":"https://github.com/TechDaddyKB/cuebot/issues/60"}"#) }
            .send(kind: .problem, title: "Deck is blank", details: "It went blank at 10.0.0.2", context: context, installId: installId)
        #expect(result == .sent(number: 60, url: URL(string: "https://github.com/TechDaddyKB/cuebot/issues/60")!))
        let sent = json(try #require(StubProtocol.requests(host: host).first))
        #expect(sent["kind"] == "problem")
        #expect(sent["title"] == "[iOS] Deck is blank")
        #expect(sent["os"] == "Companion for iOS 26.1 (iPhone18,1)")
        #expect(sent["installId"] == installId)
        let body = try #require(sent["body"])
        #expect(!body.contains("10.0.0.2"))
        for expected in ["Cuebot Companion 1.0.6 (140.1)", "iPhone18,1", "| Connection | Online |", "| Paired Cuebots | 2 |", "| Cuebot | 1.0.6 |"] {
            #expect(body.contains(expected), "\(expected)")
        }
    }

    @Test func anIdeaIsFiledAsAFeature() async throws {
        let host = "feedback-idea.test"
        _ = await reporter(host) { _ in .init(status: 201, body: #"{"number":61,"url":"https://example.test/61"}"#) }
            .send(kind: .idea, title: "Add widgets", details: "Home screen widgets", context: context, installId: installId)
        #expect(json(try #require(StubProtocol.requests(host: host).first))["kind"] == "feature")
    }

    @Test func explainsARateLimitAndAFailure() async {
        let limited = await reporter("feedback-limit.test") { _ in .init(status: 429, body: #"{"retryAfter":120}"#) }
            .send(kind: .problem, title: "Title", details: "Details", context: context, installId: installId)
        guard case .failed(let message) = limited else { Issue.record("expected a failure"); return }
        #expect(message.contains("2 minutes"))
        let failed = await reporter("feedback-fail.test") { _ in .init(status: 502, body: "{}") }
            .send(kind: .problem, title: "Title", details: "Details", context: context, installId: installId)
        if case .sent = failed { Issue.record("expected a failure") }
    }

    @Test func installIdsAreThirtyTwoHexCharacters() {
        #expect(FeedbackReporter.isInstallId(FeedbackReporter.newInstallId()))
        #expect(!FeedbackReporter.isInstallId("short"))
    }
}
