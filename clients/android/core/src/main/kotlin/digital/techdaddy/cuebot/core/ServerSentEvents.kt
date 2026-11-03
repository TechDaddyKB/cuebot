package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject

/** One server-sent event: its `event:` type (default "message"), its `data:` lines joined with "\n", and its `id:`. */
data class ServerEvent(val type: String = "message", val data: String, val id: String? = null) {
    /** The data as JSON, or null when it is not JSON. */
    val json: JsonElement? get() = parseJson(data)

    /** This event as a `/api/v1/events` message (the `event:` type is used when the data lacks one). */
    val apiEvent: ApiEvent get() {
        val o = json.obj()
        return ApiEvent(o?.str("type")?.ifEmpty { null } ?: type, o?.str("time") ?: "", o?.get("args").obj() ?: JsonObject(emptyMap()))
    }
}

/**
 * The text/event-stream format (WHATWG HTML, "Server-sent events"), fed with raw bytes: lines end with CR, LF or CRLF;
 * a blank line dispatches the event; lines starting with ":" are comments (Cuebot's ": connected" and ": keep-alive");
 * `retry:` is honoured by the stream. Not thread-safe: one parser per connection.
 */
class ServerSentEventParser(lastEventId: String? = null) {
    private val line = java.io.ByteArrayOutputStream()
    private var lastWasCR = false
    private var type = ""
    private val data = mutableListOf<String>()
    private var hasData = false

    /** The last `id:` seen (kept across events), sent back as Last-Event-ID on reconnect. */
    var lastEventId: String? = lastEventId; private set

    /** A `retry:` value in milliseconds, if the server sent one. */
    var retryMilliseconds: Long? = null; private set

    /** Comment lines seen, without the colon (": connected" → "connected"); the last 16. */
    val comments = ArrayDeque<String>()

    fun feed(bytes: ByteArray, offset: Int = 0, length: Int = bytes.size): List<ServerEvent> {
        val events = mutableListOf<ServerEvent>()
        for (i in offset until offset + length) {
            val b = bytes[i]
            if (b == LF && lastWasCR) { lastWasCR = false; continue }   // the LF of a CRLF
            lastWasCR = b == CR
            if (b == LF || b == CR) endLine()?.let(events::add) else line.write(b.toInt())
        }
        return events
    }

    private fun endLine(): ServerEvent? {
        val text = line.toString(Charsets.UTF_8)
        line.reset()
        if (text.isEmpty()) return dispatch()
        if (text.startsWith(":")) {
            comments.addLast(text.substring(1).trim())
            if (comments.size > 16) comments.removeFirst()
            return null
        }
        val colon = text.indexOf(':')
        val field = if (colon < 0) text else text.substring(0, colon)
        var value = if (colon < 0) "" else text.substring(colon + 1)
        if (value.startsWith(" ")) value = value.substring(1)
        when (field) {
            "event" -> type = value
            "data" -> { data.add(value); hasData = true }
            "id" -> if (!value.contains('\u0000')) lastEventId = value
            "retry" -> value.toLongOrNull()?.takeIf { it >= 0 }?.let { retryMilliseconds = it }
        }
        return null
    }

    private fun dispatch(): ServerEvent? {
        val event = if (hasData) ServerEvent(type.ifEmpty { "message" }, data.joinToString("\n"), lastEventId) else null
        type = ""; data.clear(); hasData = false
        return event
    }

    private companion object {
        const val LF: Byte = 0x0A
        const val CR: Byte = 0x0D
    }
}

/** Waits between reconnects: 1, 2, 4 and 8 seconds, then every 15 seconds (reset after a connection that worked). */
data class Backoff(val steps: List<Double>, val then: Double) {
    /** Seconds to wait before attempt [attempt] (0 = the first retry). */
    fun delay(attempt: Int): Double = if (attempt < steps.size) steps[maxOf(0, attempt)] else then

    companion object {
        val Standard = Backoff(listOf(1.0, 2.0, 4.0, 8.0), 15.0)
    }
}
