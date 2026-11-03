package digital.techdaddy.cuebot.companion

import android.content.Intent
import android.os.Bundle
import android.graphics.Color
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.ui.RootScreen
import digital.techdaddy.cuebot.companion.ui.Tab
import digital.techdaddy.cuebot.companion.ui.theme.Appearance
import digital.techdaddy.cuebot.companion.ui.theme.CuebotTheme
import digital.techdaddy.cuebot.companion.ui.theme.isDark

class MainActivity : ComponentActivity() {
    /** A cuebot://pair link this activity was opened with, waiting for the user to confirm it. */
    private var pendingLink by mutableStateOf<String?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val container = container
        // Extras for screenshots and tests: `-e tab dashboard`, `-e appearance dark`.
        val startTab = Tab.entries.firstOrNull { it.name.equals(intent.getStringExtra("tab"), ignoreCase = true) } ?: Tab.Deck
        intent.getStringExtra("appearance")?.let { Appearance.of(it) }?.let { container.store.put(Appearance.STORAGE_KEY, it.key) }
        if (savedInstanceState == null) takeLink(intent)
        setContent {
            val settings by container.store.values.collectAsStateWithLifecycle()
            val dark = Appearance.of(settings[Appearance.STORAGE_KEY]).isDark()
            // System bar icons follow the app's Appearance, not the system's.
            DisposableEffect(dark) {
                enableEdgeToEdge(
                    statusBarStyle = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT) { dark },
                    navigationBarStyle = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT) { dark },
                )
                onDispose {}
            }
            CuebotTheme(dark = dark) {
                RootScreen(container, startTab, pendingLink, onLinkHandled = { pendingLink = null })
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        takeLink(intent)
    }

    override fun onStart() {
        super.onStart()
        container.activityStarted()
        container.connection.check()
    }

    override fun onStop() {
        container.activityStopped()
        super.onStop()
    }

    private fun takeLink(intent: Intent?) {
        if (intent?.action == Intent.ACTION_VIEW) intent.dataString?.let { pendingLink = it }
    }
}
