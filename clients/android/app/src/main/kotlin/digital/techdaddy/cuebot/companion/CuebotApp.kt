package digital.techdaddy.cuebot.companion

import android.app.Application
import android.content.Context
import androidx.datastore.preferences.preferencesDataStore
import digital.techdaddy.cuebot.companion.data.Connection
import digital.techdaddy.cuebot.companion.data.DataStoreKeyValueStore
import digital.techdaddy.cuebot.companion.data.IconCache
import digital.techdaddy.cuebot.companion.data.KeyValueStore
import digital.techdaddy.cuebot.companion.data.KeystoreTokenCipher
import digital.techdaddy.cuebot.companion.data.PairedCuebots
import digital.techdaddy.cuebot.companion.data.RemoteImages
import digital.techdaddy.cuebot.companion.data.TokenCipher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import java.io.File

private val Context.settings by preferencesDataStore(name = "settings")

/** Everything the screens share, made once per process. */
class AppContainer(
    val store: KeyValueStore,
    cipher: TokenCipher,
    iconFolder: File,
    val scope: CoroutineScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate),
) {
    val paired = PairedCuebots(store, cipher)
    val connection = Connection(paired, scope)
    val icons = IconCache(iconFolder)
    val images = RemoteImages()

    /** True while the app is on screen: live streams run only then (no background networking in v1). */
    val foreground = MutableStateFlow(false)
    private var started = 0

    /**
     * Counted, not set: when an activity is replaced, the new one starts before the old one stops, and the old
     * one's stop must not switch the streams off.
     */
    fun activityStarted() {
        started++
        foreground.value = true
    }

    fun activityStopped() {
        started = maxOf(0, started - 1)
        foreground.value = started > 0
    }
}

class CuebotApp : Application() {
    lateinit var container: AppContainer
        private set

    override fun onCreate() {
        super.onCreate()
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
        container = AppContainer(DataStoreKeyValueStore(settings, scope), KeystoreTokenCipher(), File(cacheDir, "IconifyIcons"), scope)
    }
}

val Context.container: AppContainer get() = (applicationContext as CuebotApp).container
