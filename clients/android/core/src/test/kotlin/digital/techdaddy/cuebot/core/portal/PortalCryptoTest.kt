package digital.techdaddy.cuebot.core.portal

import digital.techdaddy.cuebot.core.portal.PortalCrypto.base64Url
import digital.techdaddy.cuebot.core.portal.PortalCrypto.fromBase64Url
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPair
import java.security.spec.ECPrivateKeySpec

/** The phone's half of the portal encryption against portal/test/vectors.json, which .NET and TypeScript check too. */
class PortalCryptoTest {
    private val v: JsonObject = Json.parseToJsonElement(File(System.getProperty("cuebot.portalVectors")).readText()).jsonObject

    private fun b(name: String) = fromBase64Url(v[name]!!.jsonPrimitive.content)

    private fun pair(name: String): KeyPair {
        val jwk = v[name]!!.jsonObject
        fun part(k: String) = fromBase64Url(jwk[k]!!.jsonPrimitive.content)
        val public = PortalCrypto.publicKey(byteArrayOf(4) + part("x") + part("y"))
        val private = KeyFactory.getInstance("EC").generatePrivate(ECPrivateKeySpec(BigInteger(1, part("d")), PortalCrypto.p256))
        return KeyPair(public, private)
    }

    private fun serverHello(signature: String) = byteArrayOf(1) +
        PortalCrypto.rawPublicKey(pair("serverEphemeral").public as java.security.interfaces.ECPublicKey) +
        b("identityPublic") + fromBase64Url(v[signature]!!.jsonPrimitive.content)

    @Test
    fun `fingerprint, hello, transcript and keys match the vectors`() {
        assertEquals(v["fingerprint"]!!.jsonPrimitive.content, PortalCrypto.fingerprint(b("identityPublic")))
        val client = pair("clientEphemeral")
        assertArrayEquals(b("clientHello"), PortalCrypto.clientHello(client))
        val serverRaw = PortalCrypto.rawPublicKey(pair("serverEphemeral").public as java.security.interfaces.ECPublicKey)
        val transcript = PortalCrypto.transcript(b("clientHello").copyOfRange(1, 66), serverRaw, b("identityPublic"))
        assertArrayEquals(b("transcript"), transcript)
        val shared = PortalCrypto.sharedSecret(client.private, serverRaw)
        assertArrayEquals(b("shared"), shared)
        val keys = PortalCrypto.deriveKeys(shared, transcript)
        assertArrayEquals(b("c2s"), keys.c2s)
        assertArrayEquals(b("s2c"), keys.s2c)
    }

    @Test
    fun `the handshake accepts browser and dotnet signatures and the frames match`() {
        for (signature in listOf("jsSignature", "dotnetSignature")) {
            val session = PortalCrypto.finishHandshake(pair("clientEphemeral"), serverHello(signature), v["fingerprint"]!!.jsonPrimitive.content)
            for (frame in v["frames"]!!.jsonArray.map { it.jsonObject }) {
                val text = frame["text"]!!.jsonPrimitive.content
                val bytes = fromBase64Url(frame["frame"]!!.jsonPrimitive.content)
                if (frame["from"]!!.jsonPrimitive.content == "client") assertArrayEquals(bytes, session.encrypt(text.toByteArray()))
                else assertEquals(text, String(session.decrypt(bytes)))
            }
        }
    }

    @Test
    fun `a wrong fingerprint, a bad signature or a changed frame is refused`() {
        val pinned = v["fingerprint"]!!.jsonPrimitive.content
        assertThrows(PortalCryptoException::class.java) { PortalCrypto.finishHandshake(pair("clientEphemeral"), serverHello("jsSignature"), "A".repeat(43)) }
        val bad = serverHello("jsSignature").also { it[it.size - 1] = (it[it.size - 1] + 1).toByte() }
        assertThrows(PortalCryptoException::class.java) { PortalCrypto.finishHandshake(pair("clientEphemeral"), bad, pinned) }
        assertThrows(PortalCryptoException::class.java) { PortalCrypto.finishHandshake(pair("clientEphemeral"), serverHello("jsSignature").copyOf(100), pinned) }

        val session = PortalCrypto.finishHandshake(pair("clientEphemeral"), serverHello("jsSignature"), pinned)
        val serverFrame = fromBase64Url(v["frames"]!!.jsonArray.first { it.jsonObject["from"]!!.jsonPrimitive.content == "server" }.jsonObject["frame"]!!.jsonPrimitive.content)
        serverFrame[0] = (serverFrame[0] + 1).toByte()
        assertThrows(PortalCryptoException::class.java) { session.decrypt(serverFrame) }
        // One failure breaks the session for good, even for a frame that would have been fine.
        assertThrows(PortalCryptoException::class.java) { session.decrypt(ByteArray(16)) }
    }

    @Test
    fun `points off the curve are refused`() {
        val raw = b("identityPublic").copyOf()
        raw[64] = (raw[64] + 1).toByte()
        assertThrows(PortalCryptoException::class.java) { PortalCrypto.publicKey(raw) }
        assertThrows(PortalCryptoException::class.java) { PortalCrypto.publicKey(raw.copyOf(64)) }
    }

    @Test
    fun `device keys sign in the format Cuebot checks`() {
        val key = SoftwareDeviceKey()
        val data = PortalCrypto.deviceProof(ByteArray(32) { it.toByte() })
        val signature = key.sign(data)
        assertEquals(64, signature.size)
        assertTrue(PortalCrypto.verify(key.publicKey, data, signature))
        assertFalse(PortalCrypto.verify(key.publicKey, data + 1, signature))
        assertEquals("cuebot-portal-device-v1", String(data.copyOfRange(0, 23)))
        // DER and r || s convert both ways, including short integers.
        val short = ByteArray(64).also { it[31] = 5; it[63] = 7 }
        assertArrayEquals(short, PortalCrypto.p1363FromDer(PortalCrypto.derFromP1363(short)))
        assertEquals("AQID", base64Url(byteArrayOf(1, 2, 3)))
    }
}
