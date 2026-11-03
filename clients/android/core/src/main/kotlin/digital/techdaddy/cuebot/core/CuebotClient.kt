package digital.techdaddy.cuebot.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.Call
import okhttp3.Callback
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resumeWithException

/** Why a call to Cuebot failed, in terms the app can act on. */
sealed class CuebotException(message: String) : Exception(message) {
    /** No connection (Cuebot not running, "Allow LAN" off, another network). */
    class Unreachable(detail: String) : CuebotException(detail)
    /** 401: the token is wrong or was rotated; pair again. */
    object Unauthorized : CuebotException("Cuebot did not accept the token. Pair again.")
    /** 404: an unknown item, or an endpoint this Cuebot is too old to have. */
    object NotFound : CuebotException("Cuebot does not know that (it may need an update).")
    /** 429: too many event streams are open. */
    object TooManyStreams : CuebotException("Too many live connections are open to Cuebot.")
    class Http(val status: Int, val body: String) : CuebotException(if (body.isEmpty()) "Cuebot answered $status." else "Cuebot answered $status: $body")
    class InvalidResponse(detail: String) : CuebotException("Cuebot's answer was not understood ($detail).")

    companion object {
        /** The error for an HTTP status, or null for success. */
        fun forStatus(status: Int, body: String): CuebotException? = when {
            status in 200..299 -> null
            status == 401 -> Unauthorized
            status == 403 && body.contains("token", ignoreCase = true) -> Unauthorized
            status == 404 -> NotFound
            status == 429 -> TooManyStreams
            else -> Http(status, body.take(300).trim())
        }
    }
}

/**
 * Talks to one paired Cuebot: the REST API (`apiPort`, Bearer token) and the deck routes of the HTTP server
 * (`httpPort`, `X-Cuebot-Deck-Token` when a deck token is set, and `?token=` on the event stream).
 */
class CuebotClient(val endpoint: CuebotEndpoint, client: OkHttpClient = defaultHttp) {
    private val http = client
    private val streamHttp = client.newBuilder().readTimeout(0, TimeUnit.MILLISECONDS).build()   // idle between events is normal

    // REST API

    suspend fun status(): StatusResponse = StatusResponse.from(getJson(api("/api/v1/status")))

    suspend fun dashboard(): Dashboard = Dashboard.from(getJson(api("/api/v1/dashboard")))

    suspend fun actions(): List<ActionSummary> = listed(getJson(api("/api/v1/actions")), "actions").map(ActionSummary::from)

    suspend fun decks(): List<DeckSummary> = listed(getJson(api("/api/v1/decks")), "decks").map(DeckSummary::from)

    suspend fun runAction(id: String) { send(api("/api/v1/actions/${seg(id)}/run"), null) }

    enum class SubathonCommand { start, pause, resume, toggle, reset }

    // The subathon and goal controls answer with the whole {timers, goals}, so the app can show the result at once.

    suspend fun subathon(timer: String, command: SubathonCommand) = state(api("/api/v1/subathon/${seg(timer)}/${command.name}"), null)

    /** Adds [add] seconds (negative takes time off) or sets the time left to [set] seconds. */
    suspend fun subathonTime(timer: String, add: Double? = null, set: Double? = null) = state(api("/api/v1/subathon/${seg(timer)}/time"), addOrSet(add, set))

    suspend fun subathonMultiplier(timer: String, value: Double) = state(api("/api/v1/subathon/${seg(timer)}/multiplier"), buildJsonObject { put("value", value) })

    suspend fun goalPoints(goal: String, add: Double? = null, set: Double? = null) = state(api("/api/v1/goals/${seg(goal)}/points"), addOrSet(add, set))

    suspend fun goalMultiplier(goal: String, value: Double) = state(api("/api/v1/goals/${seg(goal)}/multiplier"), buildJsonObject { put("value", value) })

    suspend fun goalReset(goal: String) = state(api("/api/v1/goals/${seg(goal)}/reset"), null)

    // Decks (HTTP server)

    suspend fun deckState(deck: String): DeckView = DeckView.from(getJson(deckRequest(deck, "state")))
        ?: throw CuebotException.InvalidResponse("no deck view")

    suspend fun press(deck: String, item: String) { send(deckRequest(deck, "press"), buildJsonObject { put("item", item) }) }

    suspend fun page(deck: String, delta: Int) { send(deckRequest(deck, "page"), buildJsonObject { put("delta", delta) }) }

    // Event streams

    /** `/api/v1/events` (optionally only some event types), reconnecting with backoff until collection stops. */
    fun apiEvents(types: List<String> = emptyList(), backoff: Backoff = Backoff.Standard, onState: (StreamState) -> Unit = {}): Flow<ServerEvent> =
        EventStream.events(streamHttp, backoff, onState) { lastId ->
            val url = endpoint.apiBase.toHttpUrl().newBuilder().encodedPath("/api/v1/events")
                .apply { if (types.isNotEmpty()) addQueryParameter("types", types.joinToString(",")) }.build()
            authorized(Request.Builder().url(url)).streamHeaders(lastId).build()
        }

    /** `/deck/{id}/events`: `state` (the full view, first on connect) and `notify` events. */
    fun deckEvents(deck: String, backoff: Backoff = Backoff.Standard, onState: (StreamState) -> Unit = {}): Flow<ServerEvent> =
        EventStream.events(streamHttp, backoff, onState) { lastId -> deckBuilder(deck, "events").streamHeaders(lastId).build() }

    // Requests

    private fun api(path: String): Request.Builder =
        authorized(Request.Builder().url(endpoint.apiBase.toHttpUrl().newBuilder().encodedPath(path).build()))

    private fun authorized(b: Request.Builder) = b.header("Authorization", "Bearer ${endpoint.token}").header("Accept", "application/json")

    private fun deckRequest(deck: String, route: String) = deckBuilder(deck, route)

    /** A deck route; with a deck token, the header on every request and also `?token=` on the event stream. */
    private fun deckBuilder(deck: String, route: String): Request.Builder {
        val url: HttpUrl.Builder = endpoint.httpBase.toHttpUrl().newBuilder().encodedPath("/deck/${seg(deck)}/$route")
        val token = endpoint.deckToken?.takeIf { it.isNotEmpty() }
        if (route == "events" && token != null) url.addQueryParameter("token", token)
        return Request.Builder().url(url.build()).apply { if (token != null) header("X-Cuebot-Deck-Token", token) }
    }

    private fun Request.Builder.streamHeaders(lastId: String?) = apply {
        header("Accept", "text/event-stream")
        header("Cache-Control", "no-cache")
        if (lastId != null) header("Last-Event-ID", lastId)
    }

    private suspend fun getJson(b: Request.Builder): JsonElement {
        val text = perform(b.get().build())
        // Lenient parsing takes a bare word (an HTML error page, say) as a string: answers are objects or arrays.
        return parseJson(text)?.takeIf { it is JsonObject || it is JsonArray } ?: throw CuebotException.InvalidResponse("not JSON")
    }

    private suspend fun send(b: Request.Builder, body: JsonObject?): String =
        perform(b.post((body?.toString() ?: "").toRequestBody(JSON)).build())

    private suspend fun state(b: Request.Builder, body: JsonObject?): SubathonState =
        SubathonState.from(parseJson(send(b, body).ifEmpty { "{}" }))

    private suspend fun perform(request: Request): String = withContext(Dispatchers.IO) {
        val response = try { http.newCall(request).await() } catch (e: IOException) { throw CuebotException.Unreachable(e.message ?: e.toString()) }
        response.use {
            val text = it.body.string()
            CuebotException.forStatus(it.code, text)?.let { error -> throw error }
            text
        }
    }

    companion object {
        private val JSON = "application/json".toMediaType()

        val defaultHttp: OkHttpClient = OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS).readTimeout(10, TimeUnit.SECONDS).writeTimeout(10, TimeUnit.SECONDS)
            .build()

        private fun seg(s: String) = java.net.URLEncoder.encode(s, Charsets.UTF_8).replace("+", "%20")

        private fun addOrSet(add: Double?, set: Double?) = buildJsonObject {
            if (set != null) put("set", set) else put("add", add ?: 0.0)
        }

        /** A list answer: a bare array or an object holding it under [key] (`{"actions": [...], "count": 3}`). */
        internal fun listed(e: JsonElement, key: String): List<JsonObject> =
            (e.arr() ?: e.obj()?.get(key).arr())?.mapNotNull { it.obj() } ?: emptyList()
    }
}

/** OkHttp's call as a cancellable suspension. */
internal suspend fun Call.await(): Response = suspendCancellableCoroutine { cont ->
    cont.invokeOnCancellation { cancel() }
    enqueue(object : Callback {
        override fun onResponse(call: Call, response: Response) { cont.resume(response) { _, value, _ -> value.close() } }
        override fun onFailure(call: Call, e: IOException) { cont.resumeWithException(e) }
    })
}

