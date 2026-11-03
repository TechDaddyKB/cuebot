package digital.techdaddy.cuebot.companion

import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.MemoryKeyValueStore
import digital.techdaddy.cuebot.companion.data.PairedCuebots
import digital.techdaddy.cuebot.companion.data.TokenCipher
import digital.techdaddy.cuebot.core.CuebotEndpoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import mockwebserver3.Dispatcher
import mockwebserver3.MockResponse
import mockwebserver3.MockWebServer
import mockwebserver3.RecordedRequest
import org.junit.rules.ExternalResource
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

/** Reverses the text, so tests can tell a sealed token from a plain one. */
object ReversingCipher : TokenCipher {
    override fun seal(plain: String) = "sealed:" + plain.reversed()
    override fun open(sealed: String) = sealed.removePrefix("sealed:").reversed().takeIf { sealed.startsWith("sealed:") }
}

/**
 * A Cuebot stand-in on MockWebServer: answers by path (method + path → handler), records requests, and gives the
 * ViewModels a real Main dispatcher (one thread) so their coroutines run as in the app.
 */
@OptIn(ExperimentalCoroutinesApi::class)
class FakeCuebot : ExternalResource() {
    val server = MockWebServer()
    val routes = ConcurrentHashMap<String, (RecordedRequest) -> MockResponse>()
    val requests = CopyOnWriteArrayList<RecordedRequest>()
    private val main = Executors.newSingleThreadExecutor().asCoroutineDispatcher()
    lateinit var scope: CoroutineScope
    val store = MemoryKeyValueStore()
    val foreground = MutableStateFlow(true)
    lateinit var paired: PairedCuebots
    lateinit var connection: Connection

    override fun before() {
        Dispatchers.setMain(main)
        scope = CoroutineScope(SupervisorJob() + main)
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse {
                requests += request
                val key = "${request.method} ${request.url.encodedPath}"
                return routes[key]?.invoke(request) ?: MockResponse.Builder().code(404).build()
            }
        }
        server.start()
        route("GET /api/v1/status", json("""{"app":{"name":"Cuebot","version":"1.0","instanceId":"inst-1","apiVersion":"v1"}}"""))
        paired = PairedCuebots(store, ReversingCipher)
        connection = Connection(paired, scope)
    }

    override fun after() {
        scope.cancel()
        server.close()
        Dispatchers.resetMain()
        main.close()
    }

    fun endpoint(deckToken: String? = "dk") = CuebotEndpoint("Test", server.hostName, server.port, server.port, "api-token", deckToken)

    fun pair() = paired.pair(endpoint())

    fun route(key: String, handler: (RecordedRequest) -> MockResponse) { routes[key] = handler }
    fun route(key: String, response: MockResponse) { routes[key] = { response } }

    fun requested(key: String) = requests.count { "${it.method} ${it.url.encodedPath}" == key }

    companion object {
        fun json(body: String, code: Int = 200) =
            MockResponse.Builder().code(code).addHeader("Content-Type", "application/json").body(body).build()

        /** An event stream that sends [body] and then stays open (an idle stream) for a while. */
        fun sse(body: String) = MockResponse.Builder().addHeader("Content-Type", "text/event-stream").body(body)
            .throttleBody(body.length.toLong(), 30, TimeUnit.SECONDS).build()
    }
}

/** Waits (up to 5 s) for [condition], then fails with [message]. */
fun eventually(message: String = "condition not met", condition: () -> Boolean) {
    val end = System.currentTimeMillis() + 5_000
    while (System.currentTimeMillis() < end) {
        if (condition()) return
        Thread.sleep(20)
    }
    throw AssertionError(message)
}
