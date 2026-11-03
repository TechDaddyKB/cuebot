package digital.techdaddy.cuebot.companion.data

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.LruCache
import digital.techdaddy.cuebot.core.Iconify
import digital.techdaddy.cuebot.core.SvgIcon
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * Iconify icons for deck buttons: fetched once from api.iconify.design, kept in memory and in the cache folder, so a
 * deck keeps its icons offline after the first look. Unknown or undrawable icons are remembered as missing for the
 * session (the button then shows its title only).
 */
class IconCache(private val folder: File, private val http: OkHttpClient = defaultHttp) {
    private val memory = HashMap<String, SvgIcon>()
    private val missing = HashSet<String>()
    private val inFlight = HashMap<String, CompletableDeferred<SvgIcon?>>()
    private val lock = Mutex()

    fun cached(name: String): SvgIcon? = synchronized(memory) { memory[name.lowercase()] }

    suspend fun icon(name: String): SvgIcon? {
        val key = name.lowercase()
        val (deferred, mine) = lock.withLock {
            synchronized(memory) { memory[key] }?.let { return it }
            if (key in missing) return null
            inFlight[key]?.let { return it.await() }
            CompletableDeferred<SvgIcon?>().also { inFlight[key] = it } to true
        }
        val icon = if (mine) withContext(Dispatchers.IO) { load(key) } else null
        lock.withLock {
            if (icon != null) synchronized(memory) { memory[key] = icon } else missing += key
            inFlight -= key
        }
        deferred.complete(icon)
        return icon
    }

    private fun load(key: String): SvgIcon? {
        val url = Iconify.url(key) ?: return null
        val file = File(folder, key.replace(":", "__") + ".svg")
        runCatching { SvgIcon.parse(file.readText()) }.getOrNull()?.let { return it }
        return runCatching {
            http.newCall(Request.Builder().url(url).build()).execute().use { response ->
                if (response.code != 200) return null
                val text = response.body.string()
                SvgIcon.parse(text)?.also { folder.mkdirs(); file.writeText(text) }
            }
        }.getOrNull()
    }

    companion object {
        val defaultHttp: OkHttpClient = OkHttpClient.Builder().callTimeout(15, TimeUnit.SECONDS).build()
    }
}

/** Images Cuebot serves for deck buttons (iconUrl, backgroundImage), kept in memory. */
class RemoteImages(private val http: OkHttpClient = IconCache.defaultHttp) {
    private val memory = LruCache<String, Bitmap>(64)

    fun cached(url: String): Bitmap? = memory.get(url)

    suspend fun load(url: String): Bitmap? = memory.get(url) ?: withContext(Dispatchers.IO) {
        runCatching {
            http.newCall(Request.Builder().url(url).build()).execute().use { response ->
                if (!response.isSuccessful) null else BitmapFactory.decodeStream(response.body.byteStream())
            }
        }.getOrNull()?.also { memory.put(url, it) }
    }

    companion object {
        /** Absolute http(s) links as they are; "/path" and "path" against Cuebot's HTTP server; anything else null. */
        fun url(link: String, base: String?): String? {
            val text = link.trim()
            if (text.isEmpty()) return null
            val scheme = Regex("^([a-zA-Z][a-zA-Z0-9+.-]*):").find(text)?.groupValues?.get(1)?.lowercase()
            if (scheme != null) return if (scheme == "http" || scheme == "https") text.toHttpUrlOrNull()?.toString() else null
            val root = base?.toHttpUrlOrNull() ?: return null
            return root.resolve(text)?.toString()
        }
    }
}
