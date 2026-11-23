package digital.techdaddy.cuebot.companion

import android.Manifest
import android.os.Build
import androidx.compose.ui.test.ExperimentalTestApi
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.junit4.createEmptyComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.test.performSemanticsAction
import androidx.compose.ui.semantics.SemanticsActions
import androidx.compose.ui.test.performScrollToNode
import androidx.compose.ui.test.performTextClearance
import androidx.compose.ui.test.performTextInput
import androidx.test.core.app.ActivityScenario
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.rule.GrantPermissionRule
import digital.techdaddy.cuebot.companion.ui.MENU_PINNED_KEY
import digital.techdaddy.cuebot.core.CuebotEndpoint
import digital.techdaddy.cuebot.core.EventsCardSettings
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
    // Camera for the QR scanner; notifications (Android 13+) so the app's one-time question does not cover the screen.
    @get:Rule val permissions: GrantPermissionRule = GrantPermissionRule.grant(
        *listOfNotNull(Manifest.permission.CAMERA, Manifest.permission.POST_NOTIFICATIONS.takeIf { Build.VERSION.SDK_INT >= 33 }).toTypedArray(),
    )

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

    private val chat = """{"messages":[{"platform":"Twitch","id":"m1","user":"Ann","userId":"u1","text":"hello from chat","time":"2026-11-16T10:00:00+00:00"},
        {"platform":"Kick","id":"k1","user":"Bob","userId":"u2","text":"kick says hi","time":"2026-11-16T10:00:01+00:00","deleted":true}],
        "moderation":[],"platforms":["Twitch","Kick"],"sendablePlatforms":["Twitch","Kick"],"botPlatforms":["Twitch"]}""".replace("\n", "")

    private val chatCapabilities = """{"platforms":[{"platform":"Twitch","actions":[{"action":"delete","state":"works","canRun":true},
        {"action":"timeout","state":"works","canRun":true},{"action":"ban","state":"works","canRun":true},{"action":"unban","state":"works","canRun":true},
        {"action":"warn","state":"notAvailable","canRun":false,"note":"Soft warnings are turned off."}],
        "timeoutPresets":[{"requestedSeconds":60,"appliedSeconds":60},{"requestedSeconds":600,"appliedSeconds":600}]}]}""".replace("\n", "")

    private val change = """{"id":"c1","member":"Sam","area":"command","kind":"update","target":"!hello","summary":"Sam changed the command '!hello'","at":1,"expires":2}"""
    @Volatile private var approved = false

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
                    "GET /api/v1/portal/changes" -> json(if (approved) """{"changes":[]}""" else """{"changes":[$change]}""")
                    "POST /api/v1/portal/changes/c1/approve" -> { approved = true; json("""{"result":"applied","changes":[]}""") }
                    "GET /api/v1/chat" -> json(chat)
                    "GET /api/v1/chat/capabilities" -> json(chatCapabilities)
                    "POST /api/v1/chat/send" -> json("""{"sent":true,"platforms":["Twitch"],"as":"bot"}""")
                    "POST /api/v1/chat/moderate" -> json("""{"ok":true,"result":"ok","message":"Timed out for 10 min","appliedDurationSeconds":600}""")
                    "POST /api/v1/subathon/main/time" -> json("""{"timers":[{"id":"main","name":"Subathon","state":"paused","remaining":3900}],"goals":[]}""")
                    else -> MockResponse.Builder().code(404).build()
                }
            }
        }
        server.start()
        unpairAll()
        resetSettings()
    }

    @After fun tearDown() {
        scenario?.close()
        unpairAll()
        resetSettings()
        server.close()
    }

    private fun unpairAll() = container.paired.state.value.all.forEach { container.paired.remove(it.id) }

    private fun launch() { scenario = ActivityScenario.launch(MainActivity::class.java) }

    private fun pairWithServer() = container.paired.pair(CuebotEndpoint("UI test", server.hostName, server.port, server.port, "api-token", "dk"))

    /** Opens a section from the side menu: the hamburger first, unless the menu is already open (or pinned). */
    private fun openTab(name: String) {
        compose.waitUntilExactlyOneExists(hasTestTag("tab-$name"), 10_000)
        val item = compose.onNodeWithTag("tab-$name")
        if (!item.isShown()) {
            compose.waitUntilExactlyOneExists(hasTestTag("menu-button"), 10_000)
            compose.onNodeWithTag("menu-button").performClick()
            compose.waitUntil(5_000) { item.isShown() }
        }
        item.performClick()
    }

    private fun SemanticsNodeInteraction.isShown() = runCatching { assertIsDisplayed() }.isSuccess

    private fun resetSettings() = listOf(EventsCardSettings.VISIBLE_KEY, EventsCardSettings.GROUPS_KEY, EventsCardSettings.EXPANDED_KEY, MENU_PINNED_KEY)
        .forEach { container.store.put(it, null) }

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
        openTab("deck")
        compose.waitUntilExactlyOneExists(hasTestTag("deck-button-i1"), 10_000)
    }

    @Test fun feedbackShowsWhatIsIncludedBeforeSending() {
        pairWithServer()
        launch()
        openTab("settings")
        compose.onNodeWithTag("settings").performScrollToNode(hasTestTag("settings-feedback"))
        compose.onNodeWithTag("settings-feedback").performClick()
        compose.onNodeWithTag("feedback-send").assertIsNotEnabled()
        compose.onNodeWithTag("feedback-title").performTextInput("Deck is blank")
        compose.onNodeWithTag("feedback-details").performTextInput("It went blank after a restart")
        compose.onNodeWithTag("feedback-send").assertIsEnabled()
        compose.onNodeWithText("Paired Cuebots: 1", substring = true).assertExists()
        compose.onNodeWithText("Cancel").performClick()
        compose.waitUntil(5_000) { compose.onAllNodesWithTag("feedback-form").fetchSemanticsNodes().isEmpty() }
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
        openTab("dashboard")
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

    @Test fun chatShowsMessagesSendsAsTheBotAndModerates() {
        pairWithServer()
        launch()
        openTab("chat")
        compose.waitUntilExactlyOneExists(hasTestTag("chat-line-m1"), 10_000)
        compose.onNodeWithText("(deleted)", substring = true).assertExists()

        // The events card: a strip above chat on a phone, the newest event in one line; a tap opens the list.
        compose.waitUntilExactlyOneExists(hasText("ann cheered 500 bits"), 10_000)
        compose.onNodeWithTag("events-toggle").performClick()
        compose.waitUntilExactlyOneExists(hasTestTag("event-0"), 5_000)
        compose.onNodeWithText("Twitch · Cheer · test", useUnmergedTree = true).assertExists()
        compose.waitUntil(2_000) { container.store[EventsCardSettings.EXPANDED_KEY] == "true" }
        compose.onNodeWithTag("events-toggle").performClick()
        compose.waitUntilDoesNotExist(hasTestTag("event-0"), 5_000)

        // Bot only where a bot account is signed in: Twitch here, not Kick.
        compose.onNodeWithTag("chat-send-to").performClick()
        compose.onNodeWithTag("chat-send-to-Kick").performClick()
        compose.onNodeWithTag("chat-as-bot").assertDoesNotExist()
        compose.onNodeWithTag("chat-send-to").performClick()
        compose.onNodeWithTag("chat-send-to-Twitch").performClick()
        compose.onNodeWithTag("chat-as-bot").performClick()
        compose.onNodeWithTag("chat-input").performTextInput("hi from the phone")
        compose.onNodeWithTag("chat-send").performClick()
        compose.waitUntil(5_000) { count("POST /api/v1/chat/send") == 1 }
        assertTrue(requests.last { it.url.encodedPath == "/api/v1/chat/send" }.body!!.utf8().contains("\"as\":\"bot\""))

        // Moderate (by the line's click action: the keyboard from sending moves the list): warn is greyed with its reason; a 10 minute timeout goes to Cuebot.
        compose.onNodeWithTag("chat-line-m1").performSemanticsAction(SemanticsActions.OnClick)
        compose.waitUntilExactlyOneExists(hasTestTag("mod-dialog"), 5_000)
        compose.onNodeWithTag("mod-warn").assertIsNotEnabled()
        compose.onNodeWithText("Soft warnings are turned off.").assertExists()
        compose.onNodeWithTag("mod-timeout-600").performClick()
        compose.waitUntil(5_000) { count("POST /api/v1/chat/moderate") == 1 }
        compose.waitUntilExactlyOneExists(hasText("Timed out for 10 min"), 5_000)

        // Ban asks first.
        compose.onNodeWithTag("chat-line-m1").performSemanticsAction(SemanticsActions.OnClick)
        compose.waitUntilExactlyOneExists(hasTestTag("mod-ban"), 5_000)
        compose.onNodeWithTag("mod-ban").performScrollTo().performClick()
        compose.waitUntilExactlyOneExists(hasTestTag("mod-ban-confirm"), 5_000)
        compose.onNodeWithText("Ban Ann?").assertExists()
        compose.onNodeWithTag("mod-ban-confirm").performClick()
        compose.waitUntil(5_000) { count("POST /api/v1/chat/moderate") == 2 }
    }

    @Test fun settingsListsTheCuebotAndChangesAppearance() {
        pairWithServer()
        launch()
        openTab("settings")
        compose.onNodeWithText("UI test").assertExists()
        // About is below the Chat page and Menu settings: scroll to it (the version shows once Cuebot answered).
        compose.waitUntil(5_000) { runCatching { compose.onNodeWithTag("settings").performScrollToNode(hasText("9.9.9")) }.isSuccess }
        compose.onNodeWithTag("settings").performScrollToNode(hasTestTag("appearance-picker"))
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

    @Test fun modsTabApprovesChangesAndOffersToJoin() {
        pairWithServer()
        launch()
        openTab("mods")
        compose.waitUntilExactlyOneExists(hasText("Sam changed the command '!hello'"), 10_000)
        compose.onNodeWithTag("approve-c1").performClick()
        compose.waitUntilExactlyOneExists(hasText("Approved: it is live now."), 5_000)
        assertEquals(1, count("POST /api/v1/portal/changes/c1/approve"))

        compose.onNodeWithTag("join-button").performClick()
        compose.onNodeWithTag("invite-paste").assertExists()
    }

    @Test fun theMenuClosesAfterChoosingAndTheEventsCardCanBeHidden() {
        pairWithServer()
        launch()
        openTab("chat")
        compose.waitUntilExactlyOneExists(hasTestTag("events-card"), 10_000)
        // Choosing a section closes the menu, so the page gets the whole screen.
        compose.waitUntil(5_000) { !compose.onNodeWithTag("tab-chat").isShown() }

        openTab("settings")
        compose.onNodeWithTag("settings").performScrollToNode(hasTestTag("events-group-tips"))
        compose.onNodeWithTag("events-group-tips").performClick()
        compose.waitUntil(2_000) { container.store[EventsCardSettings.GROUPS_KEY] == "follows,subs,raids" }
        openTab("chat")
        compose.waitUntilExactlyOneExists(hasText("None yet"), 10_000)

        openTab("settings")
        compose.onNodeWithTag("settings").performScrollToNode(hasTestTag("events-visible"))
        compose.onNodeWithTag("events-visible").performClick()
        compose.waitUntil(2_000) { container.store[EventsCardSettings.VISIBLE_KEY] == "false" }
        openTab("chat")
        compose.waitUntilExactlyOneExists(hasTestTag("chat-line-m1"), 10_000)
        compose.onNodeWithTag("events-card").assertDoesNotExist()

        // "Keep menu open" is a wide-screen setting: on a phone the hamburger stays.
        openTab("settings")
        compose.onNodeWithTag("settings").performScrollToNode(hasTestTag("menu-pinned"))
        compose.onNodeWithTag("menu-pinned").performClick()
        compose.waitUntil(2_000) { container.store[MENU_PINNED_KEY] == "true" }
        openTab("deck")
        compose.waitUntilExactlyOneExists(hasTestTag("deck-button-i1"), 10_000)
    }
}
