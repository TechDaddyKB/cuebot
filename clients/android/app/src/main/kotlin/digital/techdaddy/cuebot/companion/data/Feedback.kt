package digital.techdaddy.cuebot.companion.data

import android.content.Context
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.os.Build
import digital.techdaddy.cuebot.companion.BuildConfig
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.security.SecureRandom
import java.util.Locale
import java.util.concurrent.TimeUnit

enum class FeedbackKind(val wire: String, val label: String, val heading: String, val prompt: String) {
    Problem("problem", "Problem", "What happened", "What happened, and what did you expect?"),
    Idea("feature", "Idea", "Idea", "What would you like the app to do?"),
}

/** The device facts in every report. Nothing here identifies a person, a Cuebot or a network. */
data class FeedbackDevice(
    val appVersion: String,
    val versionCode: Int,
    val installedFrom: String,
    val android: String,
    val sdk: Int,
    val device: String,
    val formFactor: String,
    val language: String,
    val appearance: String,
) {
    companion object {
        fun of(context: Context): FeedbackDevice {
            val config = context.resources.configuration
            val night = config.uiMode and Configuration.UI_MODE_NIGHT_MASK == Configuration.UI_MODE_NIGHT_YES
            return FeedbackDevice(
                BuildConfig.VERSION_NAME, BuildConfig.VERSION_CODE, installSource(context), Build.VERSION.RELEASE, Build.VERSION.SDK_INT,
                "${Build.MANUFACTURER} ${Build.MODEL}".trim(), if (config.smallestScreenWidthDp >= 600) "Tablet" else "Phone",
                Locale.getDefault().toLanguageTag(), if (night) "Dark" else "Light",
            )
        }

        private fun installSource(context: Context): String {
            val installer = try {
                if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) context.packageManager.getInstallSourceInfo(context.packageName).installingPackageName
                else @Suppress("DEPRECATION") context.packageManager.getInstallerPackageName(context.packageName)
            } catch (_: PackageManager.NameNotFoundException) {
                null
            }
            return if (installer == "com.android.vending") "Google Play" else "Other"
        }
    }
}

/** What a report says about the app's state: counts and versions, never addresses, names or tokens. */
data class FeedbackContext(
    val device: FeedbackDevice,
    val connection: String,
    val pairedCuebots: Int,
    val modMemberships: Int,
    val cuebotVersion: String?,
    val apiVersion: String?,
) {
    val os get() = "Companion for Android ${device.android} (${device.device})".take(100)

    fun rows(): List<Pair<String, String>> = buildList {
        add("App" to "Cuebot Companion ${device.appVersion} (${device.versionCode})")
        add("Installed from" to device.installedFrom)
        add("Android" to "${device.android} (SDK ${device.sdk})")
        add("Device" to "${device.device}, ${device.formFactor.lowercase()}")
        add("Language" to device.language)
        add("Appearance" to device.appearance)
        add("Connection" to connection)
        add("Paired Cuebots" to pairedCuebots.toString())
        add("Mod portals joined" to modMemberships.toString())
        cuebotVersion?.takeIf { it.isNotBlank() }?.let { add("Cuebot" to it) }
        apiVersion?.takeIf { it.isNotBlank() }?.let { add("Cuebot API" to it) }
    }
}

/** Reports become public GitHub issues, so what the user typed is cleaned here and again by the report intake. */
object FeedbackText {
    const val MAX_TITLE = 200
    const val MAX_DETAILS = 8_000
    private const val TITLE_PREFIX = "[Android] "

    private val rules = listOf(
        Regex("""cuebot(?:-portal)?://\S+""", RegexOption.IGNORE_CASE) to "[link removed]",
        Regex("""\beyJ[\w-]+\.[\w-]+\.[\w-]+""") to "[token removed]",
        Regex("""\bBearer\s+[\w.~+/=-]+""", RegexOption.IGNORE_CASE) to "Bearer [token removed]",
        Regex("""\b(?:gh[pousr]_|github_pat_|sk-|xox[abprs]-)[\w-]{10,}""") to "[token removed]",
        Regex("""(?i)\b([\w-]*(?:token|secret|password|api[_-]?key))\s*[=:]\s*\S+""") to "$1=[removed]",
        Regex("""[\w.+-]+@[\w-]+(?:\.[\w-]+)+""") to "[email removed]",
        Regex("""\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b""") to "[address removed]",
        Regex("""(?i)\b(?:[0-9a-f]{1,4}:){3,7}[0-9a-f]{1,4}\b""") to "[address removed]",
        Regex("""(?i)\b[\w-]+(?:\.[\w-]+)*\.(?:local|lan|home|internal)\b(?::\d+)?""") to "[address removed]",
    )

    fun scrub(text: String): String = rules.fold(text) { current, (pattern, replacement) -> pattern.replace(current, replacement) }

    fun title(text: String): String = (TITLE_PREFIX + scrub(text).replace(Regex("""\s+"""), " ").trim()).take(MAX_TITLE)

    fun body(kind: FeedbackKind, details: String, context: FeedbackContext): String = buildString {
        append("### ").append(kind.heading).append("\n\n")
        append(scrub(details).trim().take(MAX_DETAILS)).append("\n\n")
        append("### App and device\n\n| | |\n| --- | --- |\n")
        context.rows().forEach { (name, value) -> append("| ").append(name).append(" | ").append(value.replace("|", "/").replace("\n", " ")).append(" |\n") }
    }
}

sealed interface FeedbackResult {
    data class Sent(val number: Int, val url: String) : FeedbackResult
    data class Failed(val message: String) : FeedbackResult
}

/** Files feedback through Cuebot's report intake, which holds the GitHub credentials. The app has none. */
class FeedbackReporter(
    private val store: KeyValueStore,
    private val endpoint: String = ENDPOINT,
    private val http: OkHttpClient = OkHttpClient.Builder().callTimeout(30, TimeUnit.SECONDS).build(),
) {
    /** A random id that only lets the intake limit how often one install sends. Issues show a hash of it. */
    private fun installId(): String = store[INSTALL_ID_KEY]?.takeIf { it.matches(ID_PATTERN) } ?: run {
        val bytes = ByteArray(16).also(SecureRandom()::nextBytes)
        bytes.joinToString("") { "%02x".format(Locale.ROOT, it) }.also { store.put(INSTALL_ID_KEY, it) }
    }

    suspend fun send(kind: FeedbackKind, title: String, details: String, context: FeedbackContext): FeedbackResult {
        val payload = buildJsonObject {
            put("kind", kind.wire)
            put("title", FeedbackText.title(title))
            put("body", FeedbackText.body(kind, details, context))
            put("version", context.device.appVersion)
            put("os", context.os)
            put("installId", installId())
        }.toString()
        val request = Request.Builder().url(endpoint).post(payload.toRequestBody(JSON)).build()
        return withContext(Dispatchers.IO) {
            try {
                http.newCall(request).execute().use { response ->
                    val answer = runCatching { Json.parseToJsonElement(response.body.string()).jsonObject }.getOrNull()
                    when {
                        response.isSuccessful -> {
                            val number = answer?.get("number")?.jsonPrimitive?.int
                            val url = answer?.get("url")?.jsonPrimitive?.content
                            if (number != null && url != null) FeedbackResult.Sent(number, url) else FeedbackResult.Failed(TRY_LATER)
                        }
                        response.code == 429 -> {
                            val seconds = answer?.get("retryAfter")?.jsonPrimitive?.content?.toIntOrNull() ?: 3600
                            FeedbackResult.Failed("Too many reports were sent recently. Try again in about ${maxOf(1, (seconds + 59) / 60)} minutes.")
                        }
                        else -> FeedbackResult.Failed(TRY_LATER)
                    }
                }
            } catch (_: IOException) {
                FeedbackResult.Failed("Could not reach the report service. Check your internet connection and try again.")
            }
        }
    }

    companion object {
        const val ENDPOINT = "https://cuebot-report-intake.kris-barrantes.workers.dev/v1/reports"
        const val INSTALL_ID_KEY = "feedback.installId"
        private const val TRY_LATER = "The report service could not take this right now. Please try again later."
        private val ID_PATTERN = Regex("^[0-9a-f]{32}$")
        private val JSON = "application/json".toMediaType()
    }
}
