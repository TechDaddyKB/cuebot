package digital.techdaddy.cuebot.core

import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.util.concurrent.TimeUnit

class ClientTest {
    private lateinit var server: MockWebServer
    private val fast = Backoff(listOf(0.01), 0.01)

    @Before fun start() { server = MockWebServer(); server.start() }
    @After fun stop() { server.close() }

    private fun client(deckToken: String? = "dk") =
        CuebotClient(CuebotEndpoint("Test", server.hostName, server.port, server.port, "api-token", deckToken))

    private fun json(body: String, code: Int = 200) =
        MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    private fun sse(body: String) = MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(body).build()

    @Test fun sendsTheBearerTokenAndDecodesTheDashboard() = runBlocking {
        server.enqueue(json("""{"revision":3,"feed":[]}"""))
        assertEquals(3L, client().dashboard().revision)
        val r = server.takeRequest()
        assertEquals("/api/v1/dashboard", r.url.encodedPath)
        assertEquals("Bearer api-token", r.headers["Authorization"])
    }

    @Test fun listsActionsAndDecksFromWrappedAnswers() = runBlocking {
        server.enqueue(json("""{"actions":[{"id":"a1","name":"Phone test","enabled":true}],"count":1}"""))
        server.enqueue(json("""{"decks":[{"id":"d1","name":"Stream deck","enabled":true,"rows":3,"columns":5,"pageCount":2}]}"""))
        assertEquals("Phone test", client().actions().single().name)
        assertEquals(2, client().decks().single().pageCount)
    }

    @Test fun controlsPostTheirBodiesAndReturnTheNewState() = runBlocking {
        server.enqueue(json("""{"timers":[{"id":"main","name":"Main","state":"running","remaining":60}],"goals":[]}"""))
        server.enqueue(json("""{"timers":[],"goals":[{"id":"g","name":"Goal","points":5,"steps":[]}]}"""))
        assertEquals("main", client().subathonTime("main", add = 300.0).timers.single().id)
        assertEquals(5.0, client().goalPoints("sub goal", set = 5.0).goals.single().points, 0.0)
        val a = server.takeRequest(); val b = server.takeRequest()
        assertEquals("POST" to "/api/v1/subathon/main/time", a.method to a.url.encodedPath)
        assertEquals("""{"add":300.0}""", a.body?.utf8())
        assertEquals("/api/v1/goals/sub%20goal/points", b.url.encodedPath)
        assertEquals("""{"set":5.0}""", b.body?.utf8())
    }

    @Test fun deckRoutesCarryTheDeckToken() = runBlocking {
        server.enqueue(json("""{"id":"d1","rows":1,"columns":1,"items":[]}"""))
        server.enqueue(json("{}"))
        client().deckState("d1")
        client().press("d1", "i1")
        val state = server.takeRequest(); val press = server.takeRequest()
        assertEquals("/deck/d1/state", state.url.encodedPath)
        assertEquals("dk", state.headers["X-Cuebot-Deck-Token"])
        assertNull(state.headers["Authorization"])
        assertEquals("""{"item":"i1"}""", press.body?.utf8())
    }

    @Test fun withoutADeckTokenNoHeaderIsSent() = runBlocking {
        server.enqueue(json("{}"))
        client(deckToken = null).page("d1", 1)
        assertNull(server.takeRequest().headers["X-Cuebot-Deck-Token"])
    }

    @Test fun mapsStatusCodesToErrors() = runBlocking {
        server.enqueue(json("""{"error":"bad token"}""", 401))
        server.enqueue(json("", 404))
        server.enqueue(json("Deck token required", 401))
        server.enqueue(json("boom", 500))
        suspend fun failure(block: suspend () -> Unit) = try { block(); null } catch (e: CuebotException) { e }
        assertTrue(failure { client().status() } is CuebotException.Unauthorized)
        assertTrue(failure { client().dashboard() } is CuebotException.NotFound)
        assertTrue(failure { client().deckState("d1") } is CuebotException.Unauthorized)
        val http = failure { client().runAction("a") } as CuebotException.Http
        assertEquals(500, http.status)
    }

    @Test fun anUnreachableServerSaysSo() = runBlocking {
        val gone = client(); server.close()
        try { gone.status(); fail() } catch (e: CuebotException.Unreachable) { }
    }

    @Test fun nonJsonIsAnInvalidResponse() = runBlocking {
        server.enqueue(json("<html>"))
        try { client().status(); fail() } catch (e: CuebotException.InvalidResponse) { }
    }

    @Test fun deckEventsUseTheQueryTokenAndParse() = runBlocking {
        server.enqueue(sse("event: state\ndata: {\"id\":\"d1\"}\n\nevent: notify\ndata: {\"text\":\"hi\"}\n\n"))
        val events = withTimeout(5_000) { client().deckEvents("d1", fast).take(2).toList() }
        assertEquals(listOf("state", "notify"), events.map { it.type })
        val r = server.takeRequest()
        assertEquals("dk", r.url.queryParameter("token"))
        assertEquals("text/event-stream", r.headers["Accept"])
    }

    @Test fun apiEventsReconnectWithTheLastEventId() = runBlocking {
        server.enqueue(sse("id: 4\nevent: a\ndata: 1\n\n"))
        server.enqueue(MockResponse.Builder().code(503).build())
        server.enqueue(sse("id: 5\nevent: b\ndata: 2\n\n"))
        val states = mutableListOf<StreamState>()
        val events = withTimeout(5_000) { client().apiEvents(listOf("a", "b"), fast) { states += it }.take(2).toList() }
        assertEquals(listOf("a", "b"), events.map { it.type })
        val first = server.takeRequest()
        assertEquals("a,b", first.url.queryParameter("types"))
        assertNull(first.headers["Last-Event-ID"])
        assertEquals("4", server.takeRequest().headers["Last-Event-ID"])
        assertEquals("4", server.takeRequest().headers["Last-Event-ID"])
        assertTrue(states.any { it is StreamState.Reconnecting })
        assertEquals(StreamState.Connected, states.last())
    }

    @Test fun aRejectedStreamStopsInsteadOfRetrying() = runBlocking {
        server.enqueue(json("", 401))
        try { withTimeout(5_000) { client().deckEvents("d1", fast).first() }; fail() } catch (e: CuebotException.Unauthorized) { }
        assertEquals(1, server.requestCount)
    }

    @Test fun cancellingEndsAnIdleStream() = runBlocking {
        // An open stream that never sends: collection must still stop promptly when cancelled.
        server.enqueue(MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(": hi\n\n").bodyDelay(1, TimeUnit.HOURS).build())
        val started = System.nanoTime()
        val result = kotlinx.coroutines.withTimeoutOrNull(500) { client().apiEvents(backoff = fast).first() }
        assertNull(result)
        assertTrue((System.nanoTime() - started) / 1_000_000 < 3_000)
    }

    @Test fun listsAndDecidesModChanges() = runBlocking {
        val waiting = """{"id":"c1","member":"Sam","area":"action","kind":"update","target":"Hype","summary":"Sam changed the action 'Hype'","at":1000,"expires":2000}"""
        server.enqueue(json("""{"changes":[$waiting]}"""))
        val pending = client().portalChanges().single()
        assertEquals(PendingChange("c1", "Sam", "action", "update", "Hype", "Sam changed the action 'Hype'", 1000, 2000), pending)
        assertEquals("/api/v1/portal/changes", server.takeRequest().url.encodedPath)

        server.enqueue(json("""{"result":"conflict","changes":[$waiting]}"""))
        val conflict = client().decideChange("c1", approve = true)
        assertEquals(ChangeDecision.Result.Conflict, conflict.result)
        assertEquals(1, conflict.pending.size)
        val r = server.takeRequest()
        assertEquals("/api/v1/portal/changes/c1/approve", r.url.encodedPath)
        assertEquals("""{"force":false}""", r.body?.utf8())

        server.enqueue(json("""{"result":"rejected","changes":[]}"""))
        assertEquals(ChangeDecision.Result.Rejected, client().decideChange("c1", approve = false).result)
        assertEquals("/api/v1/portal/changes/c1/reject", server.takeRequest().url.encodedPath)
    }
}
