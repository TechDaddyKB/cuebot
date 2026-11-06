package digital.techdaddy.cuebot.companion.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Groups
import androidx.compose.material.icons.filled.Lan
import androidx.compose.material.icons.filled.PanTool
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material.icons.filled.WifiOff
import androidx.compose.material.icons.filled.KeyOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import digital.techdaddy.cuebot.companion.AppContainer
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.ui.dashboard.DashboardScreen
import digital.techdaddy.cuebot.companion.ui.dashboard.DashboardViewModel
import digital.techdaddy.cuebot.companion.ui.deck.DeckScreen
import digital.techdaddy.cuebot.companion.ui.deck.DeckViewModel
import digital.techdaddy.cuebot.companion.ui.mod.ApprovalsViewModel
import digital.techdaddy.cuebot.companion.ui.mod.ModScreen
import digital.techdaddy.cuebot.companion.ui.mod.ModViewModel
import digital.techdaddy.cuebot.core.portal.PortalSignIn
import digital.techdaddy.cuebot.core.portal.PortalTarget
import digital.techdaddy.cuebot.companion.ui.settings.SettingsScreen
import digital.techdaddy.cuebot.companion.ui.settings.SettingsViewModel
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.PairingLink

enum class Tab(val label: String, val icon: ImageVector) {
    Deck("Deck", Icons.Filled.GridView),
    Dashboard("Dashboard", Icons.Filled.Speed),
    Mods("Mods", Icons.Filled.Groups),
    Settings("Settings", Icons.Filled.Settings),
}

/** Whether this Android asks the user before the app may reach the LAN (Android 17+, for apps targeting it). */
private const val LOCAL_NETWORK_SDK = 37

@Composable
fun RootScreen(container: AppContainer, startTab: Tab, pendingLink: String?, onLinkHandled: () -> Unit) {
    var tab by rememberSaveable { mutableStateOf(startTab) }
    var streamMode by rememberSaveable { mutableStateOf(false) }
    val deck: DeckViewModel = viewModel(factory = viewModelFactory { initializer { DeckViewModel(container.connection, container.store, container.foreground) } })
    val dashboard: DashboardViewModel = viewModel(factory = viewModelFactory { initializer { DashboardViewModel(container.connection, container.store, container.foreground) } })
    val settings: SettingsViewModel = viewModel(factory = viewModelFactory { initializer { SettingsViewModel(container.paired, container.connection, container.store) } })
    val mods: ModViewModel = viewModel(factory = viewModelFactory { initializer { ModViewModel(container.memberships) } })
    // The approval queue is checked while the app is on screen, so the Mods tab can show how many changes wait.
    val approvals: ApprovalsViewModel = viewModel(factory = viewModelFactory { initializer { ApprovalsViewModel(container.connection, container.foreground) } })
    val colors = LocalCuebotColors.current

    LocalNetworkPermission(container)

    // The mod portal's links: the sign-in coming back from the browser, and invite links.
    val modLink = pendingLink?.takeIf { it.startsWith("${PortalSignIn.RETURN_SCHEME}:") || PortalTarget.parse(it) != null }
    LaunchedEffect(modLink) {
        if (modLink == null) return@LaunchedEffect
        tab = Tab.Mods
        if (modLink.startsWith("${PortalSignIn.RETURN_SCHEME}:")) mods.finishSignIn(modLink) else mods.openInvite(modLink)
        onLinkHandled()
    }

    Scaffold(
        containerColor = colors.ground,
        contentColor = colors.text,
        bottomBar = {
            if (!streamMode) {
                NavigationBar(containerColor = colors.card) {
                    val waiting = approvals.state.collectAsStateWithLifecycle().value.pending.size
                    Tab.entries.forEach { t ->
                        NavigationBarItem(
                            selected = tab == t,
                            onClick = { tab = t },
                            icon = {
                                if (t == Tab.Mods && waiting > 0) BadgedBox(badge = { Badge { Text("$waiting") } }) { Icon(t.icon, contentDescription = "$waiting waiting") }
                                else Icon(t.icon, contentDescription = null)
                            },
                            label = { Text(t.label) },
                            modifier = Modifier.testTag("tab-${t.name.lowercase()}"),
                        )
                    }
                }
            }
        },
    ) { padding ->
        Box(Modifier.fillMaxSize().padding(bottom = if (streamMode) 0.dp else padding.calculateBottomPadding())) {
            when (tab) {
                Tab.Deck -> DeckScreen(deck, container, streamMode, onStreamMode = { streamMode = it }, goToSettings = { tab = Tab.Settings })
                Tab.Dashboard -> DashboardScreen(dashboard, container, goToSettings = { tab = Tab.Settings })
                Tab.Mods -> ModScreen(mods, approvals)
                Tab.Settings -> SettingsScreen(settings)
            }
        }
    }

    if (pendingLink != null && modLink == null) {
        val endpoint = remember(pendingLink) { runCatching { PairingLink.parse(pendingLink) } }
        endpoint.onSuccess { e ->
            AlertDialog(
                onDismissRequest = onLinkHandled,
                title = { Text("Pair with ${e.name}?") },
                text = { Text("Cuebot Companion will control the Cuebot at ${e.host}:${e.apiPort}. Only pair with your own computer.") },
                confirmButton = {
                    TextButton(onClick = { settings.pair(pendingLink); onLinkHandled(); tab = Tab.Deck }, modifier = Modifier.testTag("link-pair")) { Text("Pair") }
                },
                dismissButton = { TextButton(onClick = onLinkHandled) { Text("Cancel") } },
            )
        }.onFailure { error ->
            AlertDialog(
                onDismissRequest = onLinkHandled,
                title = { Text("Could not pair") },
                text = { Text((error as? PairingLink.Failure)?.let(SettingsViewModel::describe) ?: "That is not a Cuebot pairing code.") },
                confirmButton = { TextButton(onClick = onLinkHandled) { Text("OK") } },
            )
        }
    }
}

/** On Android 17+ asks for local network access once a Cuebot is paired, and checks again when it is granted. */
@Composable
private fun LocalNetworkPermission(container: AppContainer) {
    if (Build.VERSION.SDK_INT < LOCAL_NETWORK_SDK) return
    val context = LocalContext.current
    val cuebots by container.paired.state.collectAsStateWithLifecycle()
    val launcher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) container.connection.check()
    }
    LaunchedEffect(cuebots.activeId) {
        if (cuebots.activeId != null && ContextCompat.checkSelfPermission(context, LOCAL_NETWORK) != PackageManager.PERMISSION_GRANTED) {
            launcher.launch(LOCAL_NETWORK)
        }
    }
}

private const val LOCAL_NETWORK = "android.permission.ACCESS_LOCAL_NETWORK"

/** Shown instead of a tab's content when there is nothing to talk to yet, or Cuebot cannot be reached. */
@Composable
fun ConnectionGate(container: AppContainer, goToSettings: () -> Unit, content: @Composable () -> Unit) {
    val status by container.connection.status.collectAsStateWithLifecycle()
    when (val s = status) {
        ConnectionStatus.Unpaired -> Unavailable(
            Icons.Filled.QrCodeScanner, "Pair with Cuebot",
            "In Cuebot on your computer, open the Servers page and tick \"Show pairing code\" under \"Pair a phone\" in the REST API card. Then scan the code from the Settings tab here.",
        ) { Button(onClick = goToSettings, modifier = Modifier.testTag("gate-pair")) { Text("Pair a Cuebot") } }
        ConnectionStatus.Unauthorized -> Unavailable(
            Icons.Filled.KeyOff, "Pair again",
            "Cuebot did not accept this phone's token (it may have been changed). Scan the pairing code again in Settings.",
        ) { Button(onClick = goToSettings) { Text("Open Settings") } }
        is ConnectionStatus.Unreachable -> UnreachableCard(container, s.message)
        ConnectionStatus.Checking, is ConnectionStatus.Online -> content()
    }
}

@Composable
fun Unavailable(icon: ImageVector, title: String, message: String, action: (@Composable () -> Unit)? = null) {
    val colors = LocalCuebotColors.current
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(32.dp),
        verticalArrangement = Arrangement.Center,
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Icon(icon, contentDescription = null, tint = colors.muted, modifier = Modifier.size(48.dp))
        Spacer(Modifier.size(12.dp))
        Text(title, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold, textAlign = TextAlign.Center)
        Spacer(Modifier.size(8.dp))
        Text(message, color = colors.muted, textAlign = TextAlign.Center, modifier = Modifier.widthIn(max = 420.dp))
        if (action != null) {
            Spacer(Modifier.size(20.dp))
            action()
        }
    }
}

@Composable
private fun UnreachableCard(container: AppContainer, message: String) {
    val colors = LocalCuebotColors.current
    val context = LocalContext.current
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp).widthIn(max = 560.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.WifiOff, contentDescription = null, tint = colors.bad)
            Spacer(Modifier.size(10.dp))
            Text("Can't reach Cuebot", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold, modifier = Modifier.testTag("unreachable"))
        }
        container.connection.endpoint?.let { Text("Tried ${it.apiBase}", fontFamily = FontFamily.Monospace, color = colors.muted, style = MaterialTheme.typography.bodySmall) }
        Text(message, color = colors.muted, style = MaterialTheme.typography.bodyMedium)
        Check(Icons.Filled.Computer, "Is Cuebot running on your computer?")
        Check(Icons.Filled.Lan, "Is \"Allow LAN\" on in Cuebot (Servers page)?")
        Check(Icons.Filled.Wifi, "Are the phone and the computer on the same Wi-Fi?")
        if (Build.VERSION.SDK_INT >= LOCAL_NETWORK_SDK) Check(Icons.Filled.PanTool, "Is local network access allowed for Cuebot Companion (Nearby devices)?")
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Button(onClick = { container.connection.check() }, modifier = Modifier.testTag("retry")) { Text("Retry") }
            OutlinedButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", context.packageName, null)))
            }) { Text("App settings") }
        }
    }
}

@Composable
private fun Check(icon: ImageVector, text: String) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
        Spacer(Modifier.size(10.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}

/** The thin "Reconnecting…" bar over a live view. */
@Composable
fun ReconnectingBar() {
    Row(
        Modifier.fillMaxWidth().background(LocalCuebotColors.current.warn).padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        CircularProgressIndicator(Modifier.size(12.dp), color = Color.Black, strokeWidth = 2.dp)
        Spacer(Modifier.size(8.dp))
        // Black on Cuebot's amber reads in light and dark.
        Text("Reconnecting…", color = Color.Black, style = MaterialTheme.typography.labelMedium, modifier = Modifier.testTag("reconnecting"))
    }
}
