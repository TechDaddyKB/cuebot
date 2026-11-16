package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.json
import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.sse
import digital.techdaddy.cuebot.companion.ui.chat.ChatViewModel
import digital.techdaddy.cuebot.core.ModAction
import digital.techdaddy.cuebot.core.SendAs
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class ChatViewModelTest {
    @get:Rule val cuebot = FakeCuebot()

    private val chat = json("""{"messages":[
        {"platform":"Twitch","id":"m1","user":"Ann","userId":"u1","text":"hello","time":"2026-11-16T10:00:00.0000000+00:00"},
        {"platform":"Kick","id":"k1","user":"Bob","userId":"u2","text":"yo","time":"2026-11-16T10:00:01.0000000+00:00"}],
        "moderation":[],"platforms":["Twitch","Kick"],"sendablePlatforms":["Twitch","Kick"],"botPlatforms":["Twitch"]}""")

    private val capabilities = json("""{"platforms":[{"platform":"Twitch","actions":[{"action":"delete","state":"works","canRun":true},
        {"action":"timeout","state":"works","canRun":true}],"timeoutPresets":[{"requestedSeconds":600,"appliedSeconds":600}]}]}""")

    private fun viewModel() = cuebot.viewModel { ChatViewModel(cuebot.connection, cuebot.foreground) }

    private fun events(vararg lines: String) = sse(lines.joinToString("") { "data: $it\n\n" })

    @Test fun loadsChatAndFollowsMessagesAndDeletions() {
        cuebot.route("GET /api/v1/chat", chat)
        cuebot.route("GET /api/v1/chat/capabilities", capabilities)
        cuebot.route("GET /api/v1/events", events(
            """{"type":"chat.received","args":{"platform":"Twitch","id":"m2","user":"Cat","userId":"u3","text":"new","time":"2026-11-16T10:00:02.0000000+00:00"}}""",
            """{"type":"chat.moderated","args":{"platform":"Twitch","kind":"messageDeleted","messageId":"m1"}}""",
            """{"type":"chat.moderated","args":{"platform":"Kick","kind":"userTimedOut","user":"Bob","durationSeconds":600,"time":"2026-11-16T10:00:03.0000000+00:00"}}""",
        ))
        val vm = viewModel()
        cuebot.pair()
        eventually("events applied") { vm.state.value.rows.size == 4 }
        val rows = vm.state.value.rows
        val first = rows[0] as ChatViewModel.Row.Message
        assertEquals("m1", first.line.id)
        assertTrue(first.line.deleted)
        assertEquals("Bob was timed out for 10 min", (rows[3] as ChatViewModel.Row.Notice).text)
        val stream = cuebot.requests.first { it.url.encodedPath == "/api/v1/events" }
        assertEquals("chat.received,chat.moderated,chat.modLog", stream.url.queryParameter("types"))

        vm.showPlatform("Kick")
        assertEquals(listOf("Kick", "Kick"), vm.state.value.visibleRows.map { it.platform })
        assertEquals("Twitch", vm.state.value.capabilities("twitch").platform)
    }

    @Test fun botIsOfferedOnlyWhereABotAccountIsSignedIn() = runBlocking {
        cuebot.route("GET /api/v1/chat", chat)
        cuebot.route("GET /api/v1/chat/capabilities", capabilities)
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("POST /api/v1/chat/send", json("""{"sent":true,"platforms":["Twitch"],"as":"bot"}"""))
        val vm = viewModel()
        cuebot.pair()
        eventually("loaded") { vm.state.value.loaded }
        assertEquals(listOf("All", "Twitch", "Kick"), vm.state.value.sendTargets)

        vm.sendTo("Kick")
        vm.sendAs(SendAs.Bot)
        assertFalse(vm.state.value.canSendAsBot)
        assertEquals(SendAs.Broadcaster, vm.state.value.sendAs)

        vm.sendTo("Twitch")
        vm.sendAs(SendAs.Bot)
        assertEquals(SendAs.Bot, vm.state.value.sendAs)
        assertTrue(vm.send("  hi chat "))
        assertEquals("""{"platform":"Twitch","text":"hi chat","as":"bot"}""", cuebot.requests.last { it.url.encodedPath == "/api/v1/chat/send" }.body?.utf8())

        vm.sendTo("Kick")   // no bot there: back to the broadcaster
        assertEquals(SendAs.Broadcaster, vm.state.value.sendAs)
    }

    @Test fun aRefusedSendShowsCuebotsReason() = runBlocking {
        cuebot.route("GET /api/v1/chat", chat)
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("POST /api/v1/chat/send", json("""{"error":"Kick cannot send chat messages"}""", 409))
        val vm = viewModel()
        cuebot.pair()
        eventually("loaded") { vm.state.value.loaded }
        vm.sendTo("Kick")
        assertFalse(vm.send("hi"))
        assertEquals("Kick cannot send chat messages", vm.state.value.outcome?.text)
    }

    @Test fun moderatesAsTheStreamerAndShowsTheOutcome() {
        cuebot.route("GET /api/v1/chat", chat)
        cuebot.route("GET /api/v1/chat/capabilities", capabilities)
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("POST /api/v1/chat/moderate") { r ->
            if (r.body!!.utf8().contains("\"ban\"")) json("""{"ok":false,"result":"skipped","message":"Ban is done on Kick's website.","websiteUrl":"https://kick.com/mod"}""")
            else json("""{"ok":true,"result":"ok","message":"Timed out for 10 min","appliedDurationSeconds":600}""")
        }
        val vm = viewModel()
        cuebot.pair()
        eventually("loaded") { vm.state.value.rows.isNotEmpty() }
        val ann = (vm.state.value.rows[0] as ChatViewModel.Row.Message).line

        vm.moderate(ann, ModAction.Timeout, durationSeconds = 600)
        eventually("timeout done") { vm.state.value.outcome?.text == "Timed out for 10 min" }
        assertEquals("""{"platform":"Twitch","action":"timeout","messageId":"m1","userId":"u1","userName":"Ann","durationSeconds":600}""",
            cuebot.requests.last { it.url.encodedPath == "/api/v1/chat/moderate" }.body?.utf8())

        val bob = (vm.state.value.rows[1] as ChatViewModel.Row.Message).line
        vm.moderate(bob, ModAction.Ban)
        eventually("ban on the website") { vm.state.value.outcome?.websiteUrl == "https://kick.com/mod" }
        assertFalse(vm.state.value.outcome!!.ok)
    }

    @Test fun anOlderCuebotSaysUpdate() {
        cuebot.route("GET /api/v1/chat", json("", 404))
        val vm = viewModel()
        cuebot.pair()
        eventually("too old") { vm.state.value.tooOld }
        Thread.sleep(200)
        assertEquals(0, cuebot.requested("GET /api/v1/events"))
    }
}
