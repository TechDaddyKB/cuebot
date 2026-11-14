package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.json
import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.sse
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.ui.deck.DeckLayout
import digital.techdaddy.cuebot.companion.ui.deck.DeckViewModel
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class DeckViewModelTest {
    @get:Rule val cuebot = FakeCuebot()

    private val state = """{"id":"d1","name":"Main deck","rows":2,"columns":3,"page":0,"pageCount":2,
        "items":[{"id":"i1","row":0,"column":0,"title":"Mute","value":"on"}]}""".replace("\n", "")

    private fun oneDeck() {
        cuebot.route("GET /api/v1/decks", json("""{"decks":[{"id":"d1","name":"Main deck","enabled":true},{"id":"d2","name":"Off","enabled":false}]}"""))
        cuebot.route("GET /deck/d1/events", sse("event: state\ndata: $state\n\nevent: notify\ndata: {\"title\":\"Hello\",\"description\":\"From Cuebot\"}\n\n"))
        cuebot.route("POST /deck/d1/press", json("{}"))
        cuebot.route("POST /deck/d1/page", json("{}"))
    }

    private fun viewModel() = cuebot.viewModel { DeckViewModel(cuebot.connection, cuebot.store, cuebot.foreground) }

    @Test fun opensTheOnlyEnabledDeckAndShowsItLive() {
        oneDeck()
        val vm = viewModel()
        cuebot.pair()
        eventually("deck view") { vm.state.value.view != null }
        val s = vm.state.value
        assertEquals(listOf("d1"), s.decks.map { it.id })
        assertEquals("Mute", s.view?.items?.single()?.title)
        assertEquals("d1", cuebot.store["deckId"])
        eventually("notification") { vm.state.value.notification?.title == "Hello" }
        val stream = cuebot.requests.first { it.url.encodedPath == "/deck/d1/events" }
        assertEquals("dk", stream.headers["X-Cuebot-Deck-Token"])
        assertEquals("dk", stream.url.queryParameter("token"))
    }

    @Test fun pressesAndPagesGoToTheDeck() = runBlocking {
        oneDeck()
        val vm = viewModel()
        cuebot.pair()
        eventually("deck view") { vm.state.value.view != null }
        assertEquals(DeckViewModel.PressResult.Ok, vm.press(vm.state.value.view!!.items.single()))
        val press = cuebot.requests.last { it.url.encodedPath == "/deck/d1/press" }
        assertEquals("""{"item":"i1"}""", press.body?.utf8())
        vm.page(1)
        eventually("page") { cuebot.requested("POST /deck/d1/page") == 1 }
        cuebot.route("POST /deck/d1/press", json("boom", 500))
        assertEquals(DeckViewModel.PressResult.Failed, vm.press(vm.state.value.view!!.items.single()))
    }

    @Test fun severalDecksWaitForAChoice() {
        cuebot.route("GET /api/v1/decks", json("""[{"id":"d1","name":"One"},{"id":"d2","name":"Two"}]"""))
        cuebot.route("GET /deck/d2/events", sse("event: state\ndata: {\"id\":\"d2\",\"name\":\"Two\"}\n\n"))
        val vm = viewModel()
        cuebot.pair()
        eventually("deck list") { vm.state.value.decks.size == 2 }
        assertTrue(vm.state.value.needsChoice)
        vm.open("d2")
        eventually("deck two") { vm.state.value.view?.name == "Two" }
    }

    @Test fun noDecksSaysHowToMakeOne() {
        cuebot.route("GET /api/v1/decks", json("""{"decks":[]}"""))
        val vm = viewModel()
        cuebot.pair()
        eventually("error") { vm.state.value.decksError?.contains("Input > Decks") == true }
    }

    @Test fun theDecksLoadOnceCuebotCanBeReachedAgain() {
        cuebot.route("GET /api/v1/decks", json("unreachable", 503))
        val vm = viewModel()
        cuebot.pair()
        eventually("error") { vm.state.value.decksError != null }
        eventually("connection check") { cuebot.connection.status.value is ConnectionStatus.Online }
        oneDeck()
        cuebot.connection.check()   // what Retry does
        eventually("deck view") { vm.state.value.view != null }
        assertNull(vm.state.value.decksError)
    }

    @Test fun aMissingDeckTokenAsksToPairAgain() {
        oneDeck()
        cuebot.route("GET /deck/d1/events", json("Deck token required", 401))
        val vm = viewModel()
        cuebot.pair()
        eventually("pair again") { cuebot.connection.status.value == ConnectionStatus.Unauthorized }
        assertNull(vm.state.value.view)
    }

    @Test fun theStreamClosesInTheBackground() {
        oneDeck()
        val vm = viewModel()
        cuebot.pair()
        eventually("deck view") { vm.state.value.view != null }
        cuebot.foreground.value = false
        Thread.sleep(300)
        val before = cuebot.requested("GET /deck/d1/events")
        cuebot.foreground.value = true
        eventually("reopened") { cuebot.requested("GET /deck/d1/events") > before }
    }
}

class DeckLayoutTest {
    @Test fun cellsFitAndCentre() {
        val l = DeckLayout.of(width = 400f, height = 800f, rows = 3, columns = 5, gap = 8f, padding = 16f)
        assertEquals((400f - 32f - 32f) / 5, l.cellWidth, 0.01f)
        assertEquals(l.cellWidth, l.cellHeight, 0.01f)
        assertEquals(16f, l.x(0), 0.01f)
        assertEquals(400f - 16f, l.x(4) + l.cellWidth, 0.01f)
        assertEquals((800f - (3 * l.cellHeight + 16f)) / 2, l.y(0), 0.01f)
    }

    @Test fun wideScreensKeepCellsNearlySquare() {
        val l = DeckLayout.of(width = 2000f, height = 400f, rows = 2, columns = 2, gap = 0f, padding = 0f)
        assertEquals(200f, l.cellHeight, 0.01f)
        assertEquals(250f, l.cellWidth, 0.01f)
    }

    @Test fun degenerateSizesStayAtZero() {
        val l = DeckLayout.of(width = 10f, height = 10f, rows = 0, columns = 0, gap = 50f, padding = 20f)
        assertEquals(0f, l.cellWidth, 0f)
    }
}
