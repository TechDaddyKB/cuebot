package digital.techdaddy.cuebot.core.portal

import kotlinx.coroutines.runBlocking
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.security.interfaces.ECPublicKey
import java.util.concurrent.CopyOnWriteArrayList

/** The link against a stand-in for the streamer's Cuebot on a local WebSocket: the server half of the handshake. */
class PortalLinkTest {
    private lateinit var server: MockWebServer
    private val identity = PortalCrypto.newEphemeral()
    private val identityRaw = PortalCrypto.rawPublicKey(identity.public as ECPublicKey)
    private val seen = CopyOnWriteArrayList<JsonObject>()
    private var binding = ByteArray(0)

    /** Answers each request: the op echoed, and whether a device proof checks out. */
    private inner class FakeCuebot(private val signWith: java.security.KeyPair = identity) : WebSocketListener() {
        private var session: PortalSession? = null

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
        }

        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            val s = session
            if (s == null) {
                val clientEphemeral = bytes.toByteArray().copyOfRange(1, 66)
                val ephemeral = PortalCrypto.newEphemeral()
                val serverRaw = PortalCrypto.rawPublicKey(ephemeral.public as ECPublicKey)
                val t = PortalCrypto.transcript(clientEphemeral, serverRaw, identityRaw)
                val keys = PortalCrypto.deriveKeys(PortalCrypto.sharedSecret(ephemeral.private, clientEphemeral), t)
                binding = keys.binding
                session = PortalSession(keys.s2c, keys.c2s, keys.binding)
                webSocket.send((byteArrayOf(1) + serverRaw + identityRaw + PortalCrypto.sign(signWith.private, t)).toByteString())
                return
            }
            val request = Json.parseToJsonElement(String(s.decrypt(bytes.toByteArray()))).jsonObject
            seen += request
            val op = request.string("op")
            if (op == "drop") {
                webSocket.close(4403, "closed")
                return
            }
            val answer = buildJsonObject {
                put("ok", true)
                put("op", op)
                put("req", request["req"]!!)
                (request["device"] as? JsonObject)?.let { d ->
                    put("proven", PortalCrypto.verify(PortalCrypto.fromBase64Url(d.string("key")!!), PortalCrypto.deviceProof(binding),
                        PortalCrypto.fromBase64Url(d.string("proof")!!)))
                }
            }
            webSocket.send(s.encrypt(answer.toString().toByteArray()).toByteString())
        }
    }

    @Before fun start() { server = MockWebServer(); server.start() }
    @After fun stop() { server.close() }

    private fun target(pinned: String = PortalCrypto.fingerprint(identityRaw)) =
        PortalTarget("a".repeat(32), pinned, "ws://${server.hostName}:${server.port}")

    private fun serve(cuebot: WebSocketListener = FakeCuebot()) = server.enqueue(MockResponse.Builder().webSocketUpgrade(cuebot).build())

    @Test fun `requests are encrypted, answered in order and matched by req`(): Unit = runBlocking {
        serve()
        val link = PortalLink.open(target())
        assertEquals("/c/${"a".repeat(32)}", server.takeRequest().url.encodedPath)
        val answers = (1..5).map { link.request("op$it", buildJsonObject { put("n", it) }) }
        assertEquals((1..5).map { "op$it" }, answers.map { it.string("op") })
        assertTrue(answers.all { it.ok })
        assertEquals(listOf(1, 2, 3, 4, 5), seen.map { (it["n"] as JsonPrimitive).content.toInt() })
        link.close()
    }

    @Test fun `the device proof is bound to this connection`(): Unit = runBlocking {
        serve()
        val link = PortalLink.open(target())
        val key = SoftwareDeviceKey()
        val answer = link.request("resume", buildJsonObject { put("device", link.deviceProof(key, "Cuebot app on Android")) })
        assertEquals(JsonPrimitive(true), answer["proven"])
        assertEquals("Cuebot app on Android", (seen.single()["device"] as JsonObject).string("name"))
        link.close()
    }

    @Test fun `a Cuebot that is not the pinned one is refused`(): Unit = runBlocking {
        serve()
        val e = assertThrows(LinkClosed::class.java) { runBlocking { PortalLink.open(target(pinned = "B".repeat(43))) } }
        assertTrue(e.message!!.contains("does not lead to the streamer's Cuebot"))
        // The right fingerprint but a signature from another key.
        serve(FakeCuebot(signWith = PortalCrypto.newEphemeral()))
        assertThrows(LinkClosed::class.java) { runBlocking { PortalLink.open(target()) } }
    }

    @Test fun `a lost link answers waiting requests and says why`(): Unit = runBlocking {
        serve()
        val link = PortalLink.open(target())
        var reason: String? = null
        link.onClosed { reason = it }
        val answer = link.request("drop")
        assertFalse(answer.ok)
        assertEquals("The streamer has closed the mod portal.", answer.error)
        assertEquals(answer.error, reason)
        assertThrows(LinkClosed::class.java) { runBlocking { link.request("again") } }
    }

    @Test fun `an offline Cuebot gets words for the mod`() {
        server.enqueue(MockResponse.Builder().code(404).build())
        val e = assertThrows(LinkClosed::class.java) { runBlocking { PortalLink.open(target()) } }
        assertEquals("The connection to the streamer's Cuebot was lost.", e.message)
        assertEquals("The streamer's Cuebot is not online right now. Try again when it is running.", PortalLink.closeReason(4404))
    }
}
