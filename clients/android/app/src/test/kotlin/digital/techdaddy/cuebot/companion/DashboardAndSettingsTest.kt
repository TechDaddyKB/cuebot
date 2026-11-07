package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.json
import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.sse
import digital.techdaddy.cuebot.companion.ui.dashboard.DashboardViewModel
import digital.techdaddy.cuebot.companion.ui.settings.SettingsViewModel
import digital.techdaddy.cuebot.companion.ui.theme.Appearance
import digital.techdaddy.cuebot.core.ActionSummary
import digital.techdaddy.cuebot.core.ProfileSwitch
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test
import java.util.concurrent.atomic.AtomicInteger

class DashboardViewModelTest {
    @get:Rule val cuebot = FakeCuebot()

    private fun board(revision: Int, points: Int = 12) = json("""{"instanceId":"inst-1","revision":$revision,
        "subathon":{"timers":[{"id":"main","name":"Subathon","state":"running","remaining":600,"endsAt":${System.currentTimeMillis() + 600_000}}],
        "goals":[{"id":"subgoal","name":"Sub goal","points":$points,"steps":[{"name":"Emote","points":10}]}]},
        "feed":[{"platform":"Twitch","kind":"Cheer","user":"ann","isTest":true}]}""")

    private fun viewModel() = cuebot.viewModel { DashboardViewModel(cuebot.connection, cuebot.store, cuebot.foreground) }

    @Test fun loadsAndFollowsDashboardChanged() {
        val fetches = AtomicInteger()
        cuebot.route("GET /api/v1/dashboard") { if (fetches.incrementAndGet() == 1) board(1) else board(2, points = 17) }
        cuebot.route("GET /api/v1/events", sse("event: dashboard.changed\ndata: {\"type\":\"dashboard.changed\",\"args\":{\"revision\":2}}\n\n"))
        val vm = viewModel()
        cuebot.pair()
        eventually("revision 2") { vm.state.value.dashboard?.revision == 2L }
        assertEquals(17.0, vm.state.value.dashboard!!.subathon.goals.single().points, 0.0)
        assertTrue(vm.state.value.dashboard!!.feed.single().isTest)
        val stream = cuebot.requests.first { it.url.encodedPath == "/api/v1/events" }
        assertEquals("dashboard.changed", stream.url.queryParameter("types"))
    }

    private fun profilesJson(open: String) = json("""{"current":{"id":"$open","name":"${if (open == "main") "Kitsune" else "Night Owl"}","color":"#8B5CF6","open":true},
        "profiles":[{"id":"main","name":"Kitsune","color":"#8B5CF6","open":${open == "main"}},{"id":"night-owl","name":"Night Owl","color":"#EC4899","open":${open == "night-owl"}}],
        "live":[],"switching":false}""")

    @Test fun showsTheProfileAndSwitchesUntilCuebotIsBackOnIt(): Unit = runBlocking {
        var open = "main"
        cuebot.route("GET /api/v1/dashboard", board(1))
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("GET /api/v1/profiles") { profilesJson(open) }
        val live = AtomicInteger()
        cuebot.route("POST /api/v1/profiles/night-owl/activate") { r ->
            if (!r.body!!.utf8().contains("true") && live.incrementAndGet() == 1) json("""{"error":"Live on Twitch; send force true to switch anyway"}""", 409)
            else json("""{"result":"switching"}""")
        }
        val vm = viewModel()
        cuebot.pair()
        eventually("profiles") { vm.state.value.profiles?.current?.name == "Kitsune" }
        assertTrue(vm.state.value.showsProfiles)
        val owl = vm.state.value.profiles!!.profiles.single { it.id == "night-owl" }

        assertTrue(vm.switchProfile(owl).getOrThrow() is ProfileSwitch.Live)
        assertNull(vm.state.value.switchingTo)
        assertEquals(ProfileSwitch.Switching, vm.switchProfile(owl, force = true).getOrThrow())
        assertEquals("night-owl", vm.state.value.switchingTo?.id)

        open = "night-owl"   // Cuebot is back, on the other profile
        vm.refresh()
        eventually("switched") { vm.state.value.switchingTo == null && vm.state.value.profiles?.current?.id == "night-owl" }
    }

    @Test fun oneProfileOrNoneShowsNoProfileRow() {
        cuebot.route("GET /api/v1/dashboard", board(1))
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("GET /api/v1/profiles", json("""{"error":"Profiles are not available"}""", 503))
        val vm = viewModel()
        cuebot.pair()
        eventually("loaded") { vm.state.value.dashboard != null }
        assertNull(vm.state.value.profiles)
        assertTrue(!vm.state.value.showsProfiles)
    }

    @Test fun anOlderCuebotSaysUpdate() {
        cuebot.route("GET /api/v1/dashboard", json("", 404))
        val vm = viewModel()
        cuebot.pair()
        eventually("too old") { vm.state.value.tooOld }
        Thread.sleep(200)
        assertEquals(0, cuebot.requested("GET /api/v1/events"))
    }

    @Test fun controlsApplyTheReply() {
        cuebot.route("GET /api/v1/dashboard", board(1))
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("POST /api/v1/goals/subgoal/points", json("""{"timers":[],"goals":[{"id":"subgoal","name":"Sub goal","points":22,"steps":[]}]}"""))
        cuebot.route("POST /api/v1/subathon/main/time", json("boom", 500))
        val vm = viewModel()
        cuebot.pair()
        eventually("loaded") { vm.state.value.dashboard != null }
        val goal = vm.state.value.dashboard!!.subathon.goals.single()
        val timer = vm.state.value.dashboard!!.subathon.timers.single()
        vm.addPoints(goal, 10.0)
        eventually("points") { vm.state.value.dashboard!!.subathon.goals.single().points == 22.0 }
        assertEquals("""{"add":10.0}""", cuebot.requests.last { it.url.encodedPath.endsWith("/points") }.body?.utf8())
        vm.addTime(timer, 300.0)
        eventually("control error") { vm.state.value.controlError?.contains("500") == true }
    }

    @Test fun pinnedActionsAreKeptPerCuebot() = runBlocking {
        cuebot.route("GET /api/v1/dashboard", board(1))
        cuebot.route("GET /api/v1/events", sse(": hi\n\n"))
        cuebot.route("GET /api/v1/actions", json("""{"actions":[{"id":"a1","name":"Phone test","group":"","enabled":true}],"count":1}"""))
        cuebot.route("POST /api/v1/actions/a1/run", MockAccepted)
        val vm = viewModel()
        val entry = cuebot.pair()
        eventually("loaded") { vm.state.value.dashboard != null }
        vm.fetchActions()
        eventually("actions") { vm.state.value.available.isNotEmpty() }
        vm.toggle(ActionSummary("a1", "Phone test"))
        vm.setAskFirst("a1", true)
        assertTrue(cuebot.store["quickActions.${entry.id}"]!!.contains("\"askFirst\":true"))
        assertEquals(DashboardViewModel.RunResult.Started, vm.run(vm.state.value.pinned.single()))
        cuebot.route("POST /api/v1/actions/a1/run", json("", 404))
        assertEquals(DashboardViewModel.RunResult.Failed("Phone test no longer exists in Cuebot."), vm.run(vm.state.value.pinned.single()))
    }

    private companion object {
        val MockAccepted = json("", 202)
    }
}

class SettingsViewModelTest {
    @get:Rule val cuebot = FakeCuebot()
    private val vm by lazy { cuebot.viewModel { SettingsViewModel(cuebot.paired, cuebot.connection, cuebot.store) } }

    @Test fun pairsFromALinkOrExplainsWhy() {
        assertEquals("That is not a Cuebot pairing code.", vm.pair("https://example.com"))
        assertEquals("The pairing code has no token.", vm.pair("cuebot://pair?host=pc"))
        assertNull(vm.pair("cuebot://pair?v=1&name=PC&host=192.168.1.20&token=t&deckToken=d"))
        assertEquals("PC", cuebot.paired.state.value.active?.name)
        assertEquals("d", cuebot.paired.activeEndpoint()?.deckToken)
    }

    @Test fun checksTypedDetails() {
        assertEquals("The ports must be numbers between 1 and 65535.", vm.pairManually("pc", "0", "7474", "t", "", ""))
        assertEquals("Enter the API token Cuebot shows.", vm.pairManually("pc", "7475", "7474", " ", "", ""))
        assertNull(vm.pairManually(" pc.local ", "7475", "7474", "tok", "", ""))
        val e = cuebot.paired.activeEndpoint()!!
        assertTrue(e.host == "pc.local" && e.name == "Cuebot" && e.deckToken == null)
        assertNull(vm.pairManually("cuebot://pair?host=other&token=x", "", "", "", "", ""))
        assertEquals("other", cuebot.paired.activeEndpoint()?.host)
    }

    @Test fun remembersTheAppearance() {
        assertEquals(Appearance.System, vm.appearance(cuebot.store.values.value))
        vm.setAppearance(Appearance.Dark)
        assertEquals("dark", cuebot.store[Appearance.STORAGE_KEY])
        assertEquals(Appearance.Dark, vm.appearance(cuebot.store.values.value))
    }
}
