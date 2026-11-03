package digital.techdaddy.cuebot.core

import java.net.URI
import java.net.URLDecoder

/**
 * Where a paired Cuebot is and how to talk to it: the REST API (Bearer token) and the HTTP server that serves decks
 * (optional deck token). The app keeps the tokens encrypted; this value only carries them.
 */
data class CuebotEndpoint(
    val name: String = "Cuebot",
    val host: String,
    val apiPort: Int = DEFAULT_API_PORT,
    val httpPort: Int = DEFAULT_HTTP_PORT,
    val token: String,
    val deckToken: String? = null,
    val instanceId: String? = null,
) {
    /** `http://host:apiPort` (IPv6 literals in brackets), without a trailing slash. */
    val apiBase: String get() = base(apiPort)

    /** `http://host:httpPort`, which also resolves the deck's relative image links. */
    val httpBase: String get() = base(httpPort)

    private fun base(port: Int): String {
        val h = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
        return "http://$h:$port"
    }

    override fun toString(): String = "CuebotEndpoint(name=$name, host=$host, apiPort=$apiPort, httpPort=$httpPort)"   // no tokens in logs

    companion object {
        const val DEFAULT_API_PORT = 7475
        const val DEFAULT_HTTP_PORT = 7474
    }
}

/**
 * The pairing link Cuebot shows as a QR code:
 * `cuebot://pair?v=1&name=…&host=…&api=7475&http=7474&token=…&deckToken=…&id=…`.
 */
object PairingLink {
    sealed class Failure(message: String) : Exception(message) {
        object NotAPairingLink : Failure("That is not a Cuebot pairing code.")
        data class UnsupportedVersion(val version: String) : Failure("This pairing code is from a newer Cuebot. Update Cuebot Companion.")
        data class Missing(val field: String) : Failure("The pairing code has no $field.")
        data class BadPort(val field: String) : Failure("The pairing code's $field port is not valid.")
    }

    @Throws(Failure::class)
    fun parse(text: String): CuebotEndpoint {
        val uri = runCatching { URI(text.trim()) }.getOrNull() ?: throw Failure.NotAPairingLink
        if (!uri.scheme.equals("cuebot", ignoreCase = true) || !uri.host.equals("pair", ignoreCase = true)) throw Failure.NotAPairingLink
        val query = mutableMapOf<String, String>()
        for (part in (uri.rawQuery ?: "").split('&')) {
            if (part.isEmpty()) continue
            val eq = part.indexOf('=')
            val key = decode(if (eq < 0) part else part.substring(0, eq))
            val value = if (eq < 0) "" else decode(part.substring(eq + 1))
            query.putIfAbsent(key, value)
        }
        query["v"]?.let { if (it != "1") throw Failure.UnsupportedVersion(it) }
        val host = nonEmpty(query["host"]) ?: throw Failure.Missing("host")
        val token = nonEmpty(query["token"]) ?: throw Failure.Missing("token")
        return CuebotEndpoint(
            name = nonEmpty(query["name"]) ?: "Cuebot",
            host = host,
            apiPort = port(query["api"], "api", CuebotEndpoint.DEFAULT_API_PORT),
            httpPort = port(query["http"], "http", CuebotEndpoint.DEFAULT_HTTP_PORT),
            token = token,
            deckToken = nonEmpty(query["deckToken"]),
            instanceId = nonEmpty(query["id"]),
        )
    }

    private fun decode(s: String) = URLDecoder.decode(s.replace("+", "%2B"), Charsets.UTF_8)

    private fun nonEmpty(value: String?) = value?.trim()?.takeIf { it.isNotEmpty() }

    private fun port(value: String?, name: String, fallback: Int): Int {
        val text = nonEmpty(value) ?: return fallback
        val port = text.toIntOrNull() ?: throw Failure.BadPort(name)
        if (port !in 1..65535) throw Failure.BadPort(name)
        return port
    }
}
