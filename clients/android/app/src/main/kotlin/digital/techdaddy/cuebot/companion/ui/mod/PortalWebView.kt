package digital.techdaddy.cuebot.companion.ui.mod

import android.annotation.SuppressLint
import android.content.Intent
import android.net.Uri
import android.view.ViewGroup
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceRequest
import android.webkit.WebResourceResponse
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.webkit.WebViewAssetLoader
import androidx.webkit.WebViewCompat
import androidx.webkit.WebViewFeature
import digital.techdaddy.cuebot.companion.BuildConfig
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put
import java.io.ByteArrayInputStream

private const val HOST = "appassets.androidplatform.net"
private const val ORIGIN = "https://$HOST"
private const val PAGE = "$ORIGIN/assets/portal/index.html"

/** Whether this phone's WebView can run the portal (a message listener needs a WebView from 2021 or later). */
fun portalWebViewSupported(): Boolean = WebViewFeature.isFeatureSupported(WebViewFeature.WEB_MESSAGE_LISTENER)

/**
 * The portal UI the streamer's Cuebot sent, in a WebView that loads only the app's own page (assets/portal) and reaches
 * nothing on the network: the UI's requests come to the app as messages and go over the encrypted link
 * ([ModState.Portal.link]); the UI itself runs from blob: URLs, as in the web loader.
 */
@SuppressLint("SetJavaScriptEnabled")
@Composable
fun PortalWebView(portal: ModState.Portal, onSignOut: () -> Unit, onFailed: (String) -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val json = remember { Json { ignoreUnknownKeys = true } }
    val chooser = remember { arrayOfNulls<ValueCallback<Array<Uri>>>(1) }
    val pickFile = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        chooser[0]?.onReceiveValue(WebChromeClient.FileChooserParams.parseResult(result.resultCode, result.data))
        chooser[0] = null
    }

    // The page the portal was on (its #hash), so turning the device reopens the same page instead of Home.
    val page = rememberSaveable(portal.membership.relay) { arrayOf("") }
    val webView = remember(portal) {
        WebView(context).apply {
            // A WebView that is not told to fill its space treats its height as "wrap content", and the portal's vh-sized
            // parts (the chat list, held messages, the user card) collapse to a sliver.
            layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            WebView.setWebContentsDebuggingEnabled(BuildConfig.DEBUG)
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = false
            settings.allowFileAccess = false
            settings.allowContentAccess = false
            settings.setSupportMultipleWindows(false)
            val assets = WebViewAssetLoader.Builder().setDomain(HOST).addPathHandler("/assets/", WebViewAssetLoader.AssetsPathHandler(context)).build()
            webViewClient = object : WebViewClient() {
                override fun shouldInterceptRequest(view: WebView, request: WebResourceRequest): WebResourceResponse =
                    assets.shouldInterceptRequest(request.url)
                        ?: WebResourceResponse("text/plain", "utf-8", 403, "Blocked", emptyMap(), ByteArrayInputStream(ByteArray(0)))

                override fun doUpdateVisitedHistory(view: WebView, url: String?, isReload: Boolean) {
                    page[0] = pageHash(url)
                }

                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val url = request.url
                    if (url.host == HOST) return false
                    // A link in the portal (help pages): the phone's browser, never this WebView.
                    if (url.scheme == "https") runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, url)) }
                    return true
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onShowFileChooser(view: WebView, callback: ValueCallback<Array<Uri>>, params: FileChooserParams): Boolean {
                    chooser[0]?.onReceiveValue(null)
                    chooser[0] = callback
                    return try {
                        pickFile.launch(params.createIntent())
                        true
                    } catch (e: Exception) {
                        chooser[0] = null
                        false
                    }
                }
            }
            WebViewCompat.addWebMessageListener(this, "cuebotApp", setOf(ORIGIN)) { _, message, origin, isMainFrame, reply ->
                if (!isMainFrame || origin.toString() != ORIGIN) return@addWebMessageListener
                val m = runCatching { json.parseToJsonElement(message.data ?: "").jsonObject }.getOrNull() ?: return@addWebMessageListener
                when ((m["type"] as? JsonPrimitive)?.content) {
                    "ready" -> reply.postMessage(buildJsonObject {
                        put("type", "mount")
                        put("js", portal.js)
                        put("css", portal.css)
                        put("me", portal.me)
                    }.toString())
                    "request" -> {
                        val id = (m["id"] as? JsonPrimitive)?.intOrNull ?: return@addWebMessageListener
                        val op = (m["op"] as? JsonPrimitive)?.content ?: return@addWebMessageListener
                        val body = m["body"] as? JsonObject ?: JsonObject(emptyMap())
                        scope.launch {
                            val answer = runCatching { portal.request(op, body) }.getOrElse { e ->
                                buildJsonObject { put("ok", false); put("error", e.message ?: "The connection to the streamer's Cuebot was lost.") }
                            }
                            reply.postMessage(buildJsonObject { put("type", "answer"); put("id", id); put("answer", answer) }.toString())
                        }
                    }
                    "signOut" -> onSignOut()
                    "failed" -> onFailed((m["error"] as? JsonPrimitive)?.content ?: "The mod portal did not start.")
                }
            }
            loadUrl(PAGE + page[0])
        }
    }
    DisposableEffect(webView) {
        onDispose {
            chooser[0]?.onReceiveValue(null)
            webView.destroy()
        }
    }
    AndroidView(factory = { webView }, modifier = modifier)
}

/** The portal page in [url] as "#id" (the UI keeps it in the hash), or "" for none. */
internal fun pageHash(url: String?): String {
    val hash = url?.substringAfter('#', "") ?: ""
    return if (Regex("[a-z][a-z-]{0,31}").matches(hash) && hash != "chat-popout") "#$hash" else ""
}
