package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.FakeCuebot.Companion.json
import digital.techdaddy.cuebot.companion.data.MemoryDeviceKeys
import digital.techdaddy.cuebot.companion.data.ModMemberships
import digital.techdaddy.cuebot.companion.ui.mod.ApprovalsViewModel
import digital.techdaddy.cuebot.companion.ui.mod.ModState
import digital.techdaddy.cuebot.companion.ui.mod.ModViewModel
import digital.techdaddy.cuebot.core.portal.PortalCrypto
import digital.techdaddy.cuebot.core.portal.PortalLink
import digital.techdaddy.cuebot.core.portal.PortalSession
import digital.techdaddy.cuebot.core.portal.string
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Rule
import org.junit.Test
import java.security.interfaces.ECPublicKey
import java.util.concurrent.CopyOnWriteArrayList

/** The streamer's Cuebot behind the relay: the server half of the handshake, and the sign-in ops a phone uses. */
class FakePortalCuebot {
    val server = MockWebServer()
    private val identity = PortalCrypto.newEphemeral()
    private val identityRaw = PortalCrypto.rawPublicKey(identity.public as ECPublicKey)
    val fingerprint = PortalCrypto.fingerprint(identityRaw)
    val relay = "a".repeat(32)
    val requests = CopyOnWriteArrayList<JsonObject>()
    /** Device keys Cuebot knows, as base64url, and whether `resume` should ask for a new sign-in. */
    val devices = CopyOnWriteArrayList<String>()
    @Volatile var signInAgain = false

    val origin get() = "ws://${server.hostName}:${server.port}"
    fun invite(secret: String = "s".repeat(43)) = "https://portal.example.com/#i=$secret&r=$relay&k=$fingerprint"

    fun start() {
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) = MockResponse.Builder().webSocketUpgrade(Connection()).build()
        }
        server.start()
    }

    private inner class Connection : WebSocketListener() {
        private var session: PortalSession? = null

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { webSocket.close(1000, null) }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            val s = session ?: return handshake(webSocket, bytes.toByteArray())
            val r = Json.parseToJsonElement(String(s.decrypt(bytes.toByteArray()))).jsonObject
            requests += r
            val proven = (r["device"] as? JsonObject)?.let { d ->
                PortalCrypto.verify(PortalCrypto.fromBase64Url(d.string("key")!!), PortalCrypto.deviceProof(s.binding), PortalCrypto.fromBase64Url(d.string("proof")!!))
            }
            val answer: JsonObjectBuilder.() -> Unit = when (r.string("op")) {
                "providers" -> { {
                    put("ok", true)
                    put("providers", buildJsonArray {
                        add(buildJsonObject { put("id", "google"); put("name", "Google"); put("pkce", true); put("authorize", "https://accounts.google.com/o/oauth2/v2/auth?client_id=test") })
                    })
                } }
                "signin" -> {
                    val ok = proven == true && r.string("code") == "test" && r.string("verifier")!!.length >= 43
                    if (ok) devices += (r["device"] as JsonObject).string("key")!!
                    { put("ok", ok); if (ok) { put("name", "SamTheMod"); put("role", "Lead Mod") } else put("error", "The sign-in failed.") }
                }
                "resume" -> {
                    val known = proven == true && (r["device"] as JsonObject).string("key") in devices
                    { if (signInAgain || !known) { put("ok", false); put("signIn", true) } else { put("ok", true); put("name", "SamTheMod"); put("role", "Lead Mod") } }
                }
                "ui" -> { { put("ok", true); put("js", "export function mount(){}"); put("css", "") } }
                else -> { { put("ok", true) } }
            }
            val out = buildJsonObject { answer(); put("req", r["req"]!!) }
            webSocket.send(s.encrypt(out.toString().toByteArray()).toByteString())
        }

        private fun handshake(webSocket: WebSocket, hello: ByteArray) {
            val clientEphemeral = hello.copyOfRange(1, 66)
            val ephemeral = PortalCrypto.newEphemeral()
            val serverRaw = PortalCrypto.rawPublicKey(ephemeral.public as ECPublicKey)
            val t = PortalCrypto.transcript(clientEphemeral, serverRaw, identityRaw)
            val keys = PortalCrypto.deriveKeys(PortalCrypto.sharedSecret(ephemeral.private, clientEphemeral), t)
            session = PortalSession(keys.s2c, keys.c2s, keys.binding)
            webSocket.send((byteArrayOf(1) + serverRaw + identityRaw + PortalCrypto.sign(identity.private, t)).toByteString())
        }
    }
}

class ModViewModelTest {
    @get:Rule val app = FakeCuebot()
    private val portal = FakePortalCuebot()
    private val keys = MemoryDeviceKeys()
    private lateinit var memberships: ModMemberships

    @Before fun start() {
        portal.start()
        memberships = ModMemberships(app.store, ReversingCipher, keys)
    }

    @After fun stop() = portal.server.close()

    private fun viewModel() = ModViewModel(memberships, open = { PortalLink.open(it.copy(relayOrigin = portal.origin)) })

    private fun ModViewModel.join(): String = runBlocking {
        assertNull(openInvite(portal.invite()))
        eventually("providers") { state.value is ModState.Providers }
        val providers = state.value as ModState.Providers
        assertTrue(providers.joining)
        signIn(providers.target, providers.providers.single())
        withTimeout(5_000) { browser.first() }
    }

    private fun stateOf(url: String) = url.toHttpUrl().queryParameter("state")!!

    @Test fun `a mod joins by invite, signs in through the browser and gets the portal`() {
        val vm = viewModel()
        val url = vm.join()
        val state = stateOf(url)
        assertTrue(state.startsWith("app."))
        assertTrue(vm.state.value is ModState.InBrowser)
        // The verifier waits sealed in the store while the browser is open.
        assertTrue(app.store["modPendingSignIn"]!!.startsWith("sealed:"))

        vm.finishSignIn("cuebot-portal://signin?code=test&state=$state")
        eventually("portal") { vm.state.value is ModState.Portal }
        val open = vm.state.value as ModState.Portal
        assertEquals("export function mount(){}", open.js)
        val m = memberships.all.value.single()
        assertEquals(Triple("SamTheMod", "Lead Mod", null), Triple(m.member, m.role, m.target.invite))
        assertEquals("Mod portal", m.name)
        assertEquals(portal.fingerprint, m.target.fingerprint)
        val signin = portal.requests.single { it.string("op") == "signin" }
        assertEquals("s".repeat(43), signin.string("invite"))
        assertEquals(ModViewModel.DEVICE_NAME, (signin["device"] as JsonObject).string("name"))
        assertNull(memberships.pending)
        assertNotNull(keys.open(m.keyAlias))
        vm.home()
    }

    @Test fun `a returning mod resumes with the same key, and signs in again when Cuebot asks`() {
        val vm = viewModel()
        vm.finishSignIn("cuebot-portal://signin?code=test&state=${stateOf(vm.join())}")
        eventually("portal") { vm.state.value is ModState.Portal }
        vm.home()
        val m = memberships.all.value.single()

        vm.rename(m, "  Kris's stream ")
        vm.resume(memberships.all.value.single())
        eventually("resumed") { vm.state.value is ModState.Portal }
        // Signing in again keeps the name the mod gave it.
        assertEquals("Kris's stream", memberships.all.value.single().name)
        assertEquals(1, portal.requests.count { it.string("op") == "resume" })
        vm.home()

        portal.signInAgain = true
        vm.resume(m)
        eventually("asked to sign in") { vm.state.value is ModState.Providers }
        assertTrue(!(vm.state.value as ModState.Providers).joining)
    }

    @Test fun `an answer for another sign-in, a refused code or a cancel does not sign in`() {
        val vm = viewModel()
        vm.join()
        vm.finishSignIn("cuebot-portal://signin?code=test&state=app.someone-else")
        assertEquals("This sign-in did not come from this app. Start again.", (vm.state.value as ModState.Problem).text)
        assertNull(memberships.pending)

        val state = stateOf(vm.join())
        vm.finishSignIn("cuebot-portal://signin?code=wrong&state=$state")
        eventually("refused") { vm.state.value is ModState.Problem }
        assertTrue(memberships.all.value.isEmpty())
        assertTrue("the unused key is deleted", keys.keys.isEmpty())

        vm.join()
        vm.home()
        assertNull(memberships.pending)
        assertEquals("That is not a mod portal invite. Ask the streamer for the invite link or its QR code.", vm.openInvite("https://example.com/"))
    }

    @Test fun `signing out forgets the membership and its key`() {
        val vm = viewModel()
        vm.finishSignIn("cuebot-portal://signin?code=test&state=${stateOf(vm.join())}")
        eventually("portal") { vm.state.value is ModState.Portal }
        vm.signOut()
        eventually("signed out") { vm.state.value == ModState.Home }
        assertTrue(memberships.all.value.isEmpty())
        assertTrue(keys.keys.isEmpty())
        assertEquals(1, portal.requests.count { it.string("op") == "signout" })
        assertTrue(vm.notice.value!!.startsWith("Signed out"))
    }
}

class ApprovalsViewModelTest {
    @get:Rule val cuebot = FakeCuebot()

    private val change = """{"id":"c1","member":"Sam","area":"command","kind":"create","target":"!hi","summary":"Sam created the command '!hi'","at":1,"expires":2}"""

    @Test fun `the queue is checked while visible and conflicts need a second approve`() {
        cuebot.route("GET /api/v1/portal/changes", json("""{"changes":[$change]}"""))
        val forced = CopyOnWriteArrayList<String>()
        cuebot.route("POST /api/v1/portal/changes/c1/approve") { r ->
            val body = r.body!!.utf8()
            forced += body
            if (body.contains("true")) json("""{"result":"applied","changes":[]}""") else json("""{"result":"conflict","changes":[$change]}""")
        }
        val visible = MutableStateFlow(false)
        val vm = ApprovalsViewModel(cuebot.connection, visible, interval = 100)
        cuebot.pair()
        Thread.sleep(300)
        assertEquals("not checked while hidden", 0, cuebot.requested("GET /api/v1/portal/changes"))
        visible.value = true
        eventually("listed") { vm.state.value.pending.size == 1 }

        vm.approve(vm.state.value.pending.single())
        eventually("conflict") { "c1" in vm.state.value.conflicts }
        assertTrue(vm.state.value.message!!.contains("changed since Sam asked"))
        vm.approve(vm.state.value.pending.single())
        eventually("applied") { vm.state.value.message == "Approved: it is live now." }
        assertEquals(listOf("""{"force":false}""", """{"force":true}"""), forced)
        visible.value = false
    }

    @Test fun `an older Cuebot says so`() {
        cuebot.route("GET /api/v1/portal/changes", json("", 404))
        val vm = ApprovalsViewModel(cuebot.connection, MutableStateFlow(true), interval = 100)
        cuebot.pair()
        eventually("too old") { vm.state.value.tooOld }
    }
}
