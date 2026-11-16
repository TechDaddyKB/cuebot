import Foundation
import Testing
@testable import CuebotKit

struct ChatTests {
    private func client(_ host: String, reply: @escaping @Sendable (URLRequest) -> StubProtocol.Reply) -> CuebotClient {
        CuebotClient(endpoint: CuebotEndpoint(host: host, token: "secret"), session: StubProtocol.session(host: host, reply: reply))
    }

    private func json(_ request: URLRequest) -> JSONValue {
        (try? JSONDecoder().decode(JSONValue.self, from: request.httpBody ?? Data())) ?? .null
    }

    @Test func readsChatWithDeletedFlagsAndBotPlatforms() async throws {
        let client = client("chat.test") { _ in
            .init(body: #"""
            {"messages":[{"platform":"Twitch","id":"m1","user":"Ann","userId":"u1","text":"hi","roles":"Moderator, Subscriber","deleted":true},
              {"platform":"Kick","id":"m2","user":"Bob","text":"yo","canDelete":false}],
             "moderation":[{"platform":"Twitch","kind":"userTimedOut","user":"Cat","durationSeconds":600}],
             "platforms":["Twitch","Kick"],"sendablePlatforms":["Twitch","Kick"],"botPlatforms":["Twitch"]}
            """#)
        }
        let chat = try await client.chat(limit: 50)
        #expect(chat.messages.map(\.id) == ["m1", "m2"])
        #expect(chat.messages[0].deleted && chat.messages[0].isModerator)
        #expect(!chat.messages[1].canDelete)
        #expect(chat.moderation.first?.kind == .userTimedOut)
        #expect(chat.moderation.first?.durationSeconds == 600)
        #expect(chat.botPlatforms == ["Twitch"])
        let request = try #require(StubProtocol.requests(host: "chat.test").first)
        #expect(request.url?.path == "/api/v1/chat")
        #expect(request.url?.query == "limit=50")
        #expect(request.value(forHTTPHeaderField: "Authorization") == "Bearer secret")
    }

    @Test func sendsAsTheBotAndModeratesWithTheDocumentedBodies() async throws {
        let host = "chat-post.test"
        let client = client(host) { request in
            switch request.url?.path {
            case "/api/v1/chat/send": .init(body: #"{"sent":true,"platforms":["Twitch"],"as":"bot"}"#)
            default: .init(body: #"{"ok":false,"result":"skipped","message":"Pin is done on the website","websiteUrl":"https://kick.com/x","appliedDurationSeconds":120}"#)
            }
        }
        #expect(try await client.sendChat(platform: "Twitch", text: "hello", as: .bot) == ["Twitch"])
        let outcome = try await client.moderate(platform: "Twitch", action: .timeout, messageId: "m1", userId: "u1", userName: "Ann", durationSeconds: 90)
        #expect(!outcome.ok && outcome.websiteUrl == "https://kick.com/x" && outcome.appliedDurationSeconds == 120)
        let requests = StubProtocol.requests(host: host)
        #expect(requests.map { $0.url!.path } == ["/api/v1/chat/send", "/api/v1/chat/moderate"])
        #expect(json(requests[0]) == ["platform": .string("Twitch"), "text": .string("hello"), "as": .string("bot")])
        #expect(json(requests[1]) == ["platform": .string("Twitch"), "action": .string("timeout"), "messageId": .string("m1"),
                                      "userId": .string("u1"), "userName": .string("Ann"), "durationSeconds": .number(90)])
    }

    @Test func aRefusedSendIsAnHttpError() async {
        let client = client("chat-409.test") { _ in .init(status: 409, body: #"{"error":"No bot account is signed in on Kick"}"#) }
        await #expect(throws: CuebotError.http(status: 409, message: #"{"error":"No bot account is signed in on Kick"}"#)) {
            try await client.sendChat(platform: "Kick", text: "x", as: .bot)
        }
    }

    @Test func readsCapabilitiesAndTheModLog() async throws {
        let host = "chat-caps.test"
        let client = client(host) { request in
            request.url?.path == "/api/v1/chat/modlog"
                ? .init(body: #"{"entries":[{"id":"e1","actorKind":"Streamer","action":"ban","targetName":"Bob","result":"Ok"}]}"#)
                : .init(body: #"""
                {"platforms":[{"platform":"Kick","canSend":true,
                  "actions":[{"action":"timeout","state":"closest","canRun":true,"note":"Whole minutes"},{"action":"warn","state":"notAvailable","note":"Off"},
                             {"action":"pin","state":"onWebsite","websiteUrl":"https://kick.com/x"}],
                  "timeoutPresets":[{"requestedSeconds":90,"appliedSeconds":120,"note":"Kick: 2 min, rounded up from 1 min 30 s"}]}]}
                """#)
        }
        let kick = try await client.chatCapabilities().of("kick")
        #expect(kick.tool(.timeout).state == .closest)
        #expect(!kick.tool(.warn).canRun)
        #expect(!kick.tool(.ban).canRun)   // not listed: not offered
        #expect(kick.timeoutPresets.first?.appliedSeconds == 120)
        #expect(try await client.modLog(target: "u2").map(\.targetName) == ["Bob"])
        #expect(StubProtocol.requests(host: host).last?.url?.query == "target=u2")
    }

    @Test func durationLabels() {
        #expect(durationLabel(45) == "45 s")
        #expect(durationLabel(600) == "10 min")
        #expect(durationLabel(3600) == "1 h")
        #expect(durationLabel(86400) == "1 day")
        #expect(durationLabel(604800) == "7 days")
    }
}
