package digital.techdaddy.cuebot.companion

import android.Manifest
import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollToNode
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import digital.techdaddy.cuebot.core.CuebotEndpoint
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

/** The app on a device against a Cuebot stand-in (MockWebServer on the device itself). */
@OptIn(ExperimentalTestApi::class)
@RunWith(AndroidJUnit4::class)
class CompanionUiTest {
    @get:Rule val compose = createEmptyComposeRule()
    @get:Rule val camera: GrantPermissionRule = GrantPermissionRule.grant(Manifest.permission.CAMERA)

    private val server = MockWebServer()
    private val requests = CopyOnWriteArrayList<RecordedRequest>()
    private val container get() = ApplicationProvider.getApplicationContext<CuebotApp>().container
    private var scenario: ActivityScenario<MainActivity>? = null

    private fun json(body: String, code: Int = 200) = MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()
    private fun sse(body: String) = MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(body)
        .throttleBody(body.length.toLong(), 60, TimeUnit.SECONDS).build()

    private val deck = """{"id":"d1","name":"Test deck","rows":2,"columns":3,"background":"#16161c","page":0,"pageName":"Main","pageCount":2,
        "items":[{"id":"i1","row":0,"column":0,"title":"Mute mic","value":"3"},{"id":"i2","row":1,"column":2,"title":"Clip it"}]}""".replace("\n", "")

    private val dashboard = """{"instanceId":"ui-test","revision":5,
        "connections":[{"name":"Twitch","state":"Connected"},{"name":"OBS","state":"Error","status":"not running"}],
        "totals":{"cheer-session":{"count":2,"amount":600},"raid-session":{"count":1,"amount":1},"raid-latest":{"name":"bob","amount":42}},
        "subathon":{"timers":[{"id":"main","name":"Subathon","state":"paused","remaining":3600,"added":0,"multiplier":1}],
                    "goals":[{"id":"subgoal","name":"Sub goal","points":12,"steps":[{"name":"Emote","points":10},{"name":"Cosplay","points":25}]}]},
        "topSpenders":{"currency":"USD","periods":{"stream":{"all":{"total":0,"spenders":[]}}}},
        "feed":[{"platform":"Twitch","kind":"Cheer","user":"ann","title":"ann cheered 500 bits","isTest":true}]}""".replace("\n", "")

    @Before fun setUp() {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requests += request
                return when ("${request.method} ${request.url.encodedPath}") {
                    "GET /api/v1/status" -> json("""{"app":{"name":"Cuebot","version":"9.9.9","instanceId":"ui-test","apiVersion":"v1"}}""")
                    "GET /api/v1/decks" -> json("""{"decks":[{"id":"d1","name":"Test deck","enabled":true,"pageCount":2}]}""")
                    "GET /deck/d1/events" -> sse("event: state\ndata: $deck\n\n")
                    "POST /deck/d1/press", "POST /deck/d1/page" -> json("{}")
                    "GET /api/v1/dashboard" -> json(dashboard)
                    "GET /api/v1/events" -> sse(": connected\n\n")
                    "POST /api/v1/subathon/main/time" -> json("""{"timers":[{"id":"main","name":"Subathon","state":"paused","remaining":3900}],"goals":[]}""")
                    else -> MockResponse.Builder().code(404).build()
                }
            }
        }
        server.start()
        unpairAll()
    }

    @After fun tearDown() {
        scenario?.close()
        unpairAll()
        server.close()
    }

    private fun unpairAll() = container.paired.state.value.all.forEach { container.paired.remove(it.id) }

    private fun launch() { scenario = ActivityScenario.launch(MainActivity::class.java) }

    private fun pairWithServer() = container.paired.pair(CuebotEndpoint("UI test", server.hostName, server.port, server.port, "api-token", "dk"))

    private fun count(key: String) = requests.count { "${it.method} ${it.url.encodedPath}" == key }

    @Test fun unpairedLeadsToPairingByHand() {
        launch()
        compose.waitUntilExactlyOneExists(hasText("Pair with Cuebot"), 10_000)
        compose.onNodeWithTag("gate-pair").performClick()
        compose.onNodeWithTag("pair-button").performClick()
        compose.onNodeWithText("Enter the details instead").performClick()
        compose.onNodeWithTag("pair-host").performTextInput(server.hostName)
        compose.onNodeWithTag("pair-api-port").performTextClearance()
        compose.onNodeWithTag("pair-api-port").performTextInput(server.port.toString())
        compose.onNodeWithTag("pair-http-port").performTextClearance()
        compose.onNodeWithTag("pair-http-port").performTextInput(server.port.toString())
        compose.onNodeWithTag("pair-token").performTextInput("api-token")
        assertEquals(0, container.paired.state.value.all.size)
        compose.onNodeWithTag("pair-confirm").performClick()
        compose.waitUntil(5_000) { container.paired.state.value.all.size == 1 }
        assertEquals(server.hostName, container.paired.state.value.active?.host)
        // Paired: the deck loads from the stand-in.
        compose.onNodeWithTag("tab-deck").performClick()
        compose.waitUntilExactlyOneExists(hasTestTag("deck-button-i1"), 10_000)
    }

    @Test fun deckShowsLiveAndPresses() {
        pairWithServer()
        launch()
        compose.waitUntilExactlyOneExists(hasTestTag("deck-button-i1"), 10_000)
        compose.onNodeWithText("Test deck").assertExists()
        compose.onNodeWithTag("page-label").assertExists()
        compose.onNodeWithTag("deck-button-i1").performClick()
        compose.waitUntil(5_000) { count("POST /deck/d1/press") == 1 }
        val press = requests.last { it.url.encodedPath == "/deck/d1/press" }
        assertEquals("dk", press.headers["X-Cuebot-Deck-Token"])
        compose.onNode(androidx.compose.ui.test.hasContentDescription("Next page")).performClick()
        compose.waitUntil(5_000) { count("POST /deck/d1/page") == 1 }
    }

    @Test fun dashboardShowsCardsAndControlsWork() {
        pairWithServer()
        launch()
        compose.waitUntilExactlyOneExists(hasTestTag("tab-dashboard"), 10_000)
        compose.onNodeWithTag("tab-dashboard").performClick()
        compose.waitUntilExactlyOneExists(hasTestTag("subathon-main"), 10_000)
        compose.onNodeWithText("1:00:00").assertExists()
        compose.onNodeWithTag("add-5-min").performClick()
        compose.waitUntil(5_000) { count("POST /api/v1/subathon/main/time") == 1 }
        compose.waitUntilExactlyOneExists(hasText("1:05:00"), 5_000)
        compose.onNodeWithTag("dashboard").performScrollToNode(hasTestTag("top-spenders"))
        compose.onNodeWithTag("spenders-empty").assertExists()
        compose.onNodeWithTag("dashboard").performScrollToNode(hasTestTag("totals"))
        compose.onNodeWithText("last: 42 viewers").assertExists()
        compose.onNodeWithTag("dashboard").performScrollToNode(hasTestTag("feed"))
        compose.onNodeWithTag("test-badge", useUnmergedTree = true).assertExists()
    }

    @Test fun settingsListsTheCuebotAndChangesAppearance() {
        pairWithServer()
        launch()
        compose.waitUntilExactlyOneExists(hasTestTag("tab-settings"), 10_000)
        compose.onNodeWithTag("tab-settings").performClick()
        compose.onNodeWithText("UI test").assertExists()
        compose.waitUntilExactlyOneExists(hasText("9.9.9"), 5_000)
        compose.onNodeWithTag("appearance-dark").performClick()
        compose.waitUntil(2_000) { container.store["appearance"] == "dark" }
        compose.onNodeWithTag("appearance-system").performClick()
    }

    @Test fun anUnreachableCuebotShowsTheChecklist() {
        val gone = server.port
        server.close()
        container.paired.pair(CuebotEndpoint("Gone", "127.0.0.1", gone, gone, "t"))
        launch()
        compose.waitUntilExactlyOneExists(hasTestTag("unreachable"), 15_000)
        compose.onNodeWithText("Is \"Allow LAN\" on in Cuebot (Servers page)?").assertExists()
        assertTrue(requests.isEmpty())
    }
}
