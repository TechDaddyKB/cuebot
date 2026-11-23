package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.data.FeedbackContext
import digital.techdaddy.cuebot.companion.data.FeedbackDevice
import digital.techdaddy.cuebot.companion.data.FeedbackKind
import digital.techdaddy.cuebot.companion.data.FeedbackReporter
import digital.techdaddy.cuebot.companion.data.FeedbackResult
import digital.techdaddy.cuebot.companion.data.FeedbackText
import digital.techdaddy.cuebot.companion.data.MemoryKeyValueStore
import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class FeedbackTest {
    private val server = MockWebServer()
    private val store = MemoryKeyValueStore()
    private val device = FeedbackDevice("1.0.6", 136, "Google Play", "15", 35, "Google Pixel 9", "Phone", "en-US", "Dark")
    private val context = FeedbackContext(device, "Online", 2, 1, "1.0.6", "1")

    @Before fun start() = server.start()
    @After fun stop() = server.close()

    private fun reporter() = FeedbackReporter(store, server.url("/v1/reports").toString())
    private fun answer(code: Int, body: String) = MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

    @Test fun removesPrivateDetails() {
        val text = FeedbackText.scrub(
            "mail me@example.com at 192.168.1.20:7475 or studio.local:7475 cuebot://pair?host=x&token=abc token=hunter2 " +
                "Bearer abc.def ghp_abcdefghijklmnop eyJhbGci.eyJzdWIi.c2lnbmF0dXJl",
        )
        listOf("me@example.com", "192.168", "studio.local", "cuebot://", "hunter2", "abc.def", "ghp_", "eyJ").forEach { assertFalse(it, text.contains(it)) }
    }

    @Test fun postsAProblemWithTheAppDetails(): Unit = runBlocking {
        server.enqueue(answer(201, """{"number":58,"url":"https://github.com/TechDaddyKB/cuebot/issues/58","duplicate":false}"""))
        val result = reporter().send(FeedbackKind.Problem, "Deck is blank", "It went blank at 10.0.0.2", context)
        assertEquals(FeedbackResult.Sent(58, "https://github.com/TechDaddyKB/cuebot/issues/58"), result)
        val sent = Json.parseToJsonElement(server.takeRequest().body!!.utf8()).jsonObject
        assertEquals("problem", sent["kind"]!!.jsonPrimitive.content)
        assertEquals("[Android] Deck is blank", sent["title"]!!.jsonPrimitive.content)
        assertEquals("Companion for Android 15 (Google Pixel 9)", sent["os"]!!.jsonPrimitive.content)
        assertTrue(sent["installId"]!!.jsonPrimitive.content.matches(Regex("^[0-9a-f]{32}$")))
        val body = sent["body"]!!.jsonPrimitive.content
        assertFalse(body.contains("10.0.0.2"))
        listOf("Cuebot Companion 1.0.6 (136)", "Google Pixel 9", "| Connection | Online |", "| Paired Cuebots | 2 |", "| Cuebot | 1.0.6 |").forEach { assertTrue(it, body.contains(it)) }
    }

    @Test fun anIdeaIsFiledAsAFeatureAndKeepsTheSameInstall(): Unit = runBlocking {
        repeat(2) { server.enqueue(answer(201, """{"number":59,"url":"https://example.test/59"}""")) }
        reporter().send(FeedbackKind.Idea, "Add widgets", "Home screen widgets", context)
        reporter().send(FeedbackKind.Idea, "Add themes", "More themes", context)
        val first = Json.parseToJsonElement(server.takeRequest().body!!.utf8()).jsonObject
        val second = Json.parseToJsonElement(server.takeRequest().body!!.utf8()).jsonObject
        assertEquals("feature", first["kind"]!!.jsonPrimitive.content)
        assertEquals(first["installId"], second["installId"])
    }

    @Test fun explainsARateLimitAndAFailure(): Unit = runBlocking {
        server.enqueue(answer(429, """{"error":"Too many","retryAfter":120}"""))
        server.enqueue(answer(502, """{"error":"GitHub did not take the report"}"""))
        val limited = reporter().send(FeedbackKind.Problem, "Title", "Details", context) as FeedbackResult.Failed
        assertTrue(limited.message.contains("2 minutes"))
        assertTrue(reporter().send(FeedbackKind.Problem, "Title", "Details", context) is FeedbackResult.Failed)
    }
}
