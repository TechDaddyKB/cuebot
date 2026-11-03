package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.json
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.data.MemoryKeyValueStore
import digital.techdaddy.cuebot.companion.data.PairedCuebots
import digital.techdaddy.cuebot.core.CuebotEndpoint
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

class PairedCuebotsTest {
    private val store = MemoryKeyValueStore()
    private val paired = PairedCuebots(store, ReversingCipher)
    private val studio = CuebotEndpoint("Studio", "192.168.1.20", token = "tok-1", deckToken = "deck-1", instanceId = "inst-1")

    @Test fun storesTokensSealedAndReadsThemBack() {
        paired.pair(studio)
        assertFalse(store.values.value.values.any { "tok-1" in it || "deck-1" in it })
        val again = PairedCuebots(store, ReversingCipher)
        assertEquals(studio, again.activeEndpoint())
    }

    @Test fun pairingTheSameCuebotAgainUpdatesTheEntry() {
        val first = paired.pair(studio)
        val revision = paired.state.value.revision
        paired.pair(studio.copy(host = "192.168.1.30", token = "tok-2"))   // same instance id, new address
        paired.pair(studio.copy(instanceId = null, host = "192.168.1.30", token = "tok-3"))   // same address
        assertEquals(listOf(first.id), paired.state.value.all.map { it.id })
        assertEquals("tok-3", paired.activeEndpoint()?.token)
        assertTrue(paired.state.value.revision > revision)
    }

    @Test fun removingTheActiveOneActivatesAnother() {
        val a = paired.pair(studio)
        val b = paired.pair(CuebotEndpoint("Laptop", "10.0.0.5", token = "t"))
        assertEquals(b.id, paired.state.value.activeId)
        paired.activate(a.id)
        paired.remove(a.id)
        assertEquals(b.id, paired.state.value.activeId)
        paired.remove(b.id)
        assertNull(paired.activeEndpoint())
    }

    @Test fun learningTheInstanceIdDoesNotReconnect() {
        val a = paired.pair(studio.copy(instanceId = null))
        val revision = paired.state.value.revision
        paired.learnInstanceId("inst-9", a.id)
        assertEquals("inst-9", paired.state.value.active?.instanceId)
        assertEquals(revision, paired.state.value.revision)
    }

    @Test fun renamingIgnoresBlankNames() {
        val a = paired.pair(studio)
        paired.rename(a.id, "  ")
        paired.rename(a.id, " Desk PC ")
        assertEquals("Desk PC", paired.state.value.active?.name)
    }

    @Test fun anUnopenableTokenMeansNotPaired() {
        paired.pair(studio)
        val other = PairedCuebots(store, object : digital.techdaddy.cuebot.companion.data.TokenCipher {
            override fun seal(plain: String) = plain
            override fun open(sealed: String): String? = null
        })
        assertNull(other.activeEndpoint())
    }
}

class ConnectionTest {
    @get:Rule val cuebot = FakeCuebot()

    @Test fun unpairedUntilPairedThenOnline() {
        assertEquals(ConnectionStatus.Unpaired, cuebot.connection.status.value)
        cuebot.pair()
        eventually("online") { cuebot.connection.status.value is ConnectionStatus.Online }
        assertEquals("1.0", (cuebot.connection.status.value as ConnectionStatus.Online).status.app.version)
        assertEquals("inst-1", cuebot.paired.state.value.active?.instanceId)
        assertEquals("Bearer api-token", cuebot.requests.first().headers["Authorization"])
    }

    @Test fun aRejectedTokenAsksToPairAgain() {
        cuebot.route("GET /api/v1/status", json("""{"error":"bad token"}""", 401))
        cuebot.pair()
        eventually("unauthorized") { cuebot.connection.status.value == ConnectionStatus.Unauthorized }
    }

    @Test fun aServerThatIsGoneIsUnreachable() {
        cuebot.server.close()
        cuebot.pair()
        eventually("unreachable") { cuebot.connection.status.value is ConnectionStatus.Unreachable }
    }
}
