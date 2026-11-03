package digital.techdaddy.cuebot.core

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.flow
import kotlinx.coroutines.flow.flowOn
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException
import kotlin.math.max

/** What a live stream is doing, for the "Reconnecting…" bar. */
sealed interface StreamState {
    data object Connecting : StreamState
    data object Connected : StreamState
    /** Waiting [retryIn] seconds after [error] before the next attempt. */
    data class Reconnecting(val retryIn: Double, val error: String) : StreamState
}

/**
 * Server-sent events over OkHttp, reconnecting with backoff until the collector stops. Ends with an error only when
 * retrying cannot help: 401 (pair again) or 404 (no such stream). Sends Last-Event-ID after a reconnect.
 */
internal object EventStream {
    fun events(http: OkHttpClient, backoff: Backoff, onState: (StreamState) -> Unit, request: (String?) -> Request): Flow<ServerEvent> = flow {
        var lastEventId: String? = null
        var attempt = 0
        while (true) {
            currentCoroutineContext().ensureActive()
            onState(StreamState.Connecting)
            val parser = ServerSentEventParser(lastEventId)
            var problem: String
            val call = http.newCall(request(lastEventId))
            try {
                coroutineScope {
                    // The body read below blocks on the socket, so cancellation cannot reach it: this watcher
                    // cancels the call instead, which makes the read fail at once.
                    val watcher = launch(start = CoroutineStart.UNDISPATCHED) {
                        try { awaitCancellation() } finally { call.cancel() }
                    }
                    try {
                        call.await().use {
                            CuebotException.forStatus(it.code, "")?.let { error ->
                                if (error is CuebotException.Unauthorized || error is CuebotException.NotFound) throw error
                                throw IOException(error.message)
                            }
                            onState(StreamState.Connected)
                            attempt = 0
                            val source = it.body.source()
                            val buffer = ByteArray(8192)
                            while (true) {
                                val n = source.read(buffer)   // blocks until bytes arrive; returns as soon as some do
                                if (n < 0) break
                                for (event in parser.feed(buffer, 0, n)) emit(event)
                            }
                        }
                    } finally {
                        watcher.cancel()
                    }
                }
                problem = "the stream ended"
            } catch (e: CancellationException) {
                throw e
            } catch (e: CuebotException.Unauthorized) {
                throw e
            } catch (e: CuebotException.NotFound) {
                throw e
            } catch (e: Exception) {
                currentCoroutineContext().ensureActive()
                problem = e.message ?: e.toString()
            }
            lastEventId = parser.lastEventId
            val wait = parser.retryMilliseconds?.let { max(it / 1000.0, backoff.delay(attempt)) } ?: backoff.delay(attempt)
            attempt++
            onState(StreamState.Reconnecting(wait, problem))
            delay((wait * 1000).toLong())
        }
    }.flowOn(Dispatchers.IO)
}
