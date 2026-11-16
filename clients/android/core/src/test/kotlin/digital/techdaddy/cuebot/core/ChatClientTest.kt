package digital.techdaddy.cuebot.core

import kotlinx.coroutines.runBlocking
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class ChatClientTest {
    private lateinit var server: MockWebServer

    @Before fun start() { server = MockWebServer(); server.start() }
    @After fun stop() { server.close() }

    private fun client() = CuebotClient(CuebotEndpoint("Test", server.hostName, server.port, server.port, "api-token", "dk"))

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    @Test fun readsChatWithDeletedFlagsAndBotPlatforms() = runBlocking {
        server.enqueue(json("""{"messages":[{"platform":"Twitch","id":"m1","user":"Ann","userId":"u1","text":"hi","roles":"Moderator, Subscriber","deleted":true,"canDelete":true},
            {"platform":"Kick","id":"m2","user":"Bob","text":"yo","canDelete":false}],
            "moderation":[{"platform":"Twitch","kind":"userTimedOut","user":"Cat","durationSeconds":600}],
            "platforms":["Twitch","Kick"],"sendablePlatforms":["Twitch","Kick"],"botPlatforms":["Twitch"]}"""))
        val chat = client().chat(limit = 50)
        val request = server.takeRequest()
        assertEquals("/api/v1/chat", request.url.encodedPath)
        assertEquals("50", request.url.queryParameter("limit"))
        assertTrue(chat.messages[0].deleted)
        assertTrue(chat.messages[0].isModerator)
        assertFalse(chat.messages[1].canDelete)
        assertEquals(ModerationNotice.Kind.UserTimedOut, chat.moderation.single().kind)
        assertEquals(600, chat.moderation.single().durationSeconds)
        assertEquals(listOf("Twitch"), chat.botPlatforms)
    }

    @Test fun sendsAsTheBotAndModeratesWithTheRightBodies() = runBlocking {
        server.enqueue(json("""{"sent":true,"platforms":["Twitch"],"as":"bot"}"""))
        server.enqueue(json("""{"ok":true,"result":"ok","message":"Timed out for 2 min","appliedDurationSeconds":120}"""))
        server.enqueue(json("""{"ok":false,"result":"skipped","message":"Pin is done on the website","websiteUrl":"https://kick.com/x"}"""))
        assertEquals(listOf("Twitch"), client().sendChat("Twitch", "hello", SendAs.Bot))
        val sent = server.takeRequest()
        assertEquals("/api/v1/chat/send", sent.url.encodedPath)
        assertEquals("""{"platform":"Twitch","text":"hello","as":"bot"}""", sent.body?.utf8())
        assertEquals("Bearer api-token", sent.headers["Authorization"])

        val outcome = client().moderate("Twitch", ModAction.Timeout, messageId = "m1", userId = "u1", userName = "Ann", durationSeconds = 90)
        assertEquals(120, outcome.appliedDurationSeconds)
        assertEquals("""{"platform":"Twitch","action":"timeout","messageId":"m1","userId":"u1","userName":"Ann","durationSeconds":90}""",
            server.takeRequest().body?.utf8())
        val skipped = client().moderate("Kick", ModAction.Ban, userId = "u2")
        assertFalse(skipped.ok)
        assertEquals("https://kick.com/x", skipped.websiteUrl)
    }

    @Test fun refusedSendIsAnHttpError() = runBlocking {
        server.enqueue(json("""{"error":"No bot account is signed in on Kick"}""", 409))
        try {
            client().sendChat("Kick", "x", SendAs.Bot)
            throw AssertionError("expected 409")
        } catch (e: CuebotException.Http) {
            assertEquals(409, e.status)
        }
    }

    @Test fun readsCapabilitiesAndTheModLog() = runBlocking {
        server.enqueue(json("""{"platforms":[{"platform":"Kick","canSend":true,"canSendAsBot":false,
            "actions":[{"action":"timeout","state":"closest","canRun":true,"note":"Whole minutes"},{"action":"warn","state":"notAvailable","note":"Soft warnings are off"},
                       {"action":"pin","state":"onWebsite","websiteUrl":"https://kick.com/x"}],
            "timeoutPresets":[{"requestedSeconds":90,"appliedSeconds":120,"note":"Kick: 2 min, rounded up from 1 min 30 s"}]}]}"""))
        server.enqueue(json("""{"entries":[{"id":"e1","actorKind":"Streamer","action":"ban","targetName":"Bob","result":"Ok"}]}"""))
        val kick = client().chatCapabilities().of("kick")!!
        assertEquals(ToolCapability.State.Closest, kick.tool(ModAction.Timeout).state)
        assertFalse(kick.tool(ModAction.Warn).canRun)
        assertFalse(kick.tool(ModAction.Ban).canRun)   // not listed: not offered
        assertEquals(120, kick.timeoutPresets.single().appliedSeconds)
        assertEquals("Bob", client().modLog(target = "u2").single().targetName)
        server.takeRequest()
        assertEquals("u2", server.takeRequest().url.queryParameter("target"))
    }

    @Test fun durationLabels() {
        assertEquals("45 s", durationLabel(45))
        assertEquals("10 min", durationLabel(600))
        assertEquals("1 h", durationLabel(3600))
        assertEquals("1 day", durationLabel(86400))
        assertEquals("7 days", durationLabel(604800))
    }
}
