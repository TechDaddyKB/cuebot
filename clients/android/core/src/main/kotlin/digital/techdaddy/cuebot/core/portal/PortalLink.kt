package digital.techdaddy.cuebot.core.portal

import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit

/** Why the link could not be opened or was lost, in words for the mod. */
class LinkClosed(message: String) : Exception(message)

/**
 * One encrypted connection from this phone to the streamer's Cuebot through the relay: the handshake pinned to the
 * invite's fingerprint, then JSON requests and answers matched by "req" (portal/web/link.ts does the same in a
 * browser). Every answer has "ok"; a lost link answers every waiting request with ok false and the reason.
 */
class PortalLink private constructor(private val session: PortalSession) {
    private lateinit var socket: WebSocket
    private var next = 1
    private val waiting = ConcurrentHashMap<Int, CompletableDeferred<JsonObject>>()
    @Volatile private var failure: String? = null
    private var onClosed: (String) -> Unit = {}

    /** SHA-256 of the handshake transcript: what a device signs to prove its key on this connection. */
    val binding: ByteArray get() = session.binding

    /** Calls [listener] once when the link is lost (not when [close] is called). */
    fun onClosed(listener: (String) -> Unit) {
        onClosed = listener
        failure?.let(listener)
    }

    /** Sends one request and waits for its answer. */
    suspend fun request(op: String, body: JsonObject = JsonObject(emptyMap())): JsonObject {
        failure?.let { throw LinkClosed(it) }
        val answer = CompletableDeferred<JsonObject>()
        // Frames carry counter nonces: encrypting and queueing them must happen in one order.
        synchronized(this) {
            val req = next++
            waiting[req] = answer
            val plain = JsonObject(body + mapOf("op" to JsonPrimitive(op), "req" to JsonPrimitive(req)))
            socket.send(session.encrypt(json.encodeToString(JsonObject.serializer(), plain).toByteArray()).toByteString())
        }
        return answer.await()
    }

    fun close() {
        onClosed = {}
        fail("Closed.", notify = false)
        socket.close(1000, null)
    }

    private fun receive(frame: ByteString) {
        val answer = try {
            json.parseToJsonElement(String(session.decrypt(frame.toByteArray()))).jsonObject
        } catch (e: Exception) {
            socket.close(1000, null)
            fail("The secure connection failed. Open the portal again.")
            return
        }
        val req = (answer["req"] as? JsonPrimitive)?.intOrNull ?: return
        waiting.remove(req)?.complete(answer)
    }

    private fun fail(reason: String, notify: Boolean = true) {
        synchronized(this) {
            if (failure != null) return
            failure = reason
        }
        val error = buildJsonObject { put("ok", false); put("error", reason) }
        waiting.values.forEach { it.complete(error) }
        waiting.clear()
        if (notify) onClosed(reason)
    }

    companion object {
        private val json = Json { ignoreUnknownKeys = true }
        private const val HELLO_TIMEOUT_MS = 15_000L

        val defaultHttp: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            // The relay drops idle sockets after a while; pings keep a page that is only being read open.
            .pingInterval(30, TimeUnit.SECONDS)
            .build()

        /** Connects to `<relay origin>/c/<relay id>` and completes the handshake; throws [LinkClosed] with words for the mod. */
        suspend fun open(target: PortalTarget, http: OkHttpClient = defaultHttp): PortalLink {
            val ephemeral = PortalCrypto.newEphemeral()
            val hello = CompletableDeferred<ByteArray>()
            var link: PortalLink? = null
            val listener = object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(PortalCrypto.clientHello(ephemeral).toByteString())
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    // OkHttp calls this on one reader thread, in order: frames are decrypted in the order they came.
                    link?.receive(bytes) ?: hello.complete(bytes.toByteArray())
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(1000, null)
                    closed(code)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = closed(0)

                private fun closed(code: Int) {
                    val words = closeReason(code)
                    link?.fail(words) ?: hello.completeExceptionally(LinkClosed(words))
                }
            }
            val socket = http.newWebSocket(Request.Builder().url("${target.relayOrigin}/c/${target.relay}").build(), listener)
            val serverHello = try {
                withTimeout(HELLO_TIMEOUT_MS) { hello.await() }
            } catch (e: TimeoutCancellationException) {
                socket.cancel()
                throw LinkClosed("The streamer's Cuebot did not answer.")
            } catch (e: LinkClosed) {
                socket.cancel()
                throw e
            }
            val session = try {
                PortalCrypto.finishHandshake(ephemeral, serverHello, target.fingerprint)
            } catch (e: PortalCryptoException) {
                socket.close(1000, null)
                throw LinkClosed("This link does not lead to the streamer's Cuebot it was made for. Ask the streamer for a new invite.")
            }
            return PortalLink(session).also {
                it.socket = socket
                link = it
            }
        }

        /** Words for the relay's close codes (portal/RELAY.md). */
        fun closeReason(code: Int): String = when (code) {
            4404, 4503 -> "The streamer's Cuebot is not online right now. Try again when it is running."
            4403 -> "The streamer has closed the mod portal."
            4429 -> "Too many people are connecting right now. Try again in a minute."
            else -> "The connection to the streamer's Cuebot was lost."
        }
    }
}

/** `{key, proof[, name]}`: this device's public key and its signature over the link's binding. */
fun PortalLink.deviceProof(key: DeviceKey, name: String? = null): JsonObject = buildJsonObject {
    put("key", PortalCrypto.base64Url(key.publicKey))
    put("proof", PortalCrypto.base64Url(key.sign(PortalCrypto.deviceProof(binding))))
    if (name != null) put("name", name)
}

val JsonObject.ok: Boolean get() = (this["ok"] as? JsonPrimitive)?.content == "true"

val JsonObject.error: String? get() = (this["error"] as? JsonPrimitive)?.takeIf { it.isString }?.content

fun JsonObject.string(name: String): String? = (this[name] as? JsonPrimitive)?.takeIf { it.isString }?.content

internal fun JsonElement?.asObject(): JsonObject? = this as? JsonObject
