package digital.techdaddy.cuebot.companion.ui.mod

import android.Manifest
import android.content.ClipboardManager
import android.content.pm.PackageManager
import android.net.Uri
import android.text.format.DateUtils
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.browser.customtabs.CustomTabsIntent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.Groups
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.ui.settings.QrImageReader
import digital.techdaddy.cuebot.companion.ui.settings.QrScanner
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.PendingChange
import kotlinx.coroutines.launch

/**
 * The Mods tab. For a mod: the streamers this phone mods for, joining with an invite, and the portal itself. For the
 * streamer: their mods' changes waiting for an OK on the paired Cuebot.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ModScreen(vm: ModViewModel, approvals: ApprovalsViewModel) {
    val state by vm.state.collectAsStateWithLifecycle()
    val context = LocalContext.current
    LaunchedEffect(vm) {
        vm.browser.collect { url -> CustomTabsIntent.Builder().setShowTitle(true).build().launchUrl(context, Uri.parse(url)) }
    }
    when (val s = state) {
        ModState.Home -> Home(vm, approvals)
        is ModState.Portal -> PortalScreen(vm, s)
        else -> Flow(vm, s)
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Home(vm: ModViewModel, approvals: ApprovalsViewModel) {
    val colors = LocalCuebotColors.current
    val memberships by vm.all.collectAsStateWithLifecycle()
    val notice by vm.notice.collectAsStateWithLifecycle()
    val queue by approvals.state.collectAsStateWithLifecycle()
    var joining by rememberSaveable { mutableStateOf(false) }
    val rowColors = ListItemDefaults.colors(containerColor = colors.card)

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Mods") }, colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground))
        LazyColumn(Modifier.fillMaxSize().testTag("mods")) {
            notice?.let { n -> item { Note(n) { vm.notice.value = null } } }

            if (queue.pending.isNotEmpty() || queue.message != null) {
                item { Header("Waiting for your OK") }
                queue.message?.let { m -> item { Note(m, approvals::clearMessage) } }
                items(queue.pending, key = { it.id }) { change ->
                    PendingRow(change, conflict = change.id in queue.conflicts, approve = { approvals.approve(change) }, reject = { approvals.reject(change) })
                }
                item { Footer("Your mods' changes that need your OK, on the Cuebot this phone is paired with. Unanswered changes expire after 7 days.") }
            }

            item { Header("Streamers you mod for") }
            items(memberships, key = { it.relay }) { m ->
                var menu by remember { mutableStateOf(false) }
                ListItem(
                    headlineContent = { Text(m.member.ifEmpty { "Mod portal" }) },
                    supportingContent = { Text(m.role.ifEmpty { "Mod" }, color = colors.muted) },
                    leadingContent = { Icon(Icons.Filled.Groups, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    trailingContent = {
                        Box {
                            IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More for ${m.member}") }
                            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                                DropdownMenuItem(text = { Text("Forget on this phone", color = colors.bad) }, onClick = { menu = false; vm.forget(m) })
                            }
                        }
                    },
                    colors = rowColors,
                    modifier = Modifier.clickable { vm.resume(m) }.testTag("membership-${m.relay}"),
                )
                HorizontalDivider(color = colors.line)
            }
            item {
                ListItem(
                    headlineContent = { Text("Join with an invite", color = MaterialTheme.colorScheme.primary) },
                    leadingContent = { Icon(Icons.Filled.QrCodeScanner, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clickable { joining = true }.testTag("join-button"),
                )
            }
            item {
                Footer(if (memberships.isEmpty()) "When a streamer invites you to help with their stream, scan the invite's QR code or paste its link here. You sign in with Google, Twitch or Discord so their Cuebot knows it's you."
                else "Tap a streamer to open their mod portal.")
            }
        }
    }
    if (joining) InviteDialog(open = { link -> vm.openInvite(link).also { if (it == null) joining = false } }, close = { joining = false })
}

@Composable
private fun PendingRow(change: PendingChange, conflict: Boolean, approve: () -> Unit, reject: () -> Unit) {
    val colors = LocalCuebotColors.current
    Card(
        colors = CardDefaults.cardColors(containerColor = colors.card),
        modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp).testTag("pending-${change.id}"),
    ) {
        Column(Modifier.padding(14.dp)) {
            Text(change.summary, fontWeight = FontWeight.SemiBold)
            val ago = if (change.at > 0) DateUtils.getRelativeTimeSpanString(change.at).toString() else ""
            Text(ago, color = colors.muted, style = MaterialTheme.typography.bodySmall)
            Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Button(onClick = approve, modifier = Modifier.testTag("approve-${change.id}")) { Text(if (conflict) "Approve anyway" else "Approve") }
                OutlinedButton(onClick = reject, modifier = Modifier.testTag("reject-${change.id}")) { Text("Reject") }
            }
        }
    }
}

/** Scanning an invite's QR code, a picture of it, or its link from the clipboard. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun InviteDialog(open: (String) -> String?, close: () -> Unit) {
    val context = LocalContext.current
    val colors = LocalCuebotColors.current
    val scope = rememberCoroutineScope()
    val hasCamera = context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)
    var cameraAllowed by rememberSaveable {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    var error by rememberSaveable { mutableStateOf<String?>(null) }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { cameraAllowed = it }
    LaunchedEffect(Unit) { if (hasCamera && !cameraAllowed) cameraPermission.launch(Manifest.permission.CAMERA) }
    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            val codes = QrImageReader.codes(context, uri)
            error = when {
                codes == null -> "That image could not be opened."
                codes.isEmpty() -> "No QR code was found in that image."
                else -> open(codes.first())
            }
        }
    }

    Dialog(onDismissRequest = close, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(
            containerColor = colors.ground,
            contentColor = colors.text,
            topBar = {
                TopAppBar(
                    title = { Text("Join with an invite") },
                    navigationIcon = { IconButton(onClick = close) { Icon(Icons.Filled.Close, contentDescription = "Cancel") } },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground),
                )
            },
        ) { padding ->
            Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp)) {
                if (hasCamera && cameraAllowed) {
                    QrScanner(onCode = { error = open(it) }, modifier = Modifier.fillMaxWidth().height(300.dp).clip(RoundedCornerShape(12.dp)).testTag("invite-scanner"))
                    Spacer(Modifier.height(12.dp))
                }
                val rowColors = ListItemDefaults.colors(containerColor = colors.card)
                ListItem(
                    headlineContent = { Text("Paste the invite link") },
                    leadingContent = { Icon(Icons.Filled.ContentPaste, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clip(RoundedCornerShape(topStart = 12.dp, topEnd = 12.dp)).testTag("invite-paste").clickable {
                        val clip = context.getSystemService(ClipboardManager::class.java)?.primaryClip
                        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString()?.trim()
                        error = if (text.isNullOrEmpty()) "There is no link on the clipboard. Copy the invite link the streamer sent first." else open(text)
                    },
                )
                ListItem(
                    headlineContent = { Text("Choose a picture of the QR code") },
                    leadingContent = { Icon(Icons.Filled.Image, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clip(RoundedCornerShape(bottomStart = 12.dp, bottomEnd = 12.dp)).testTag("invite-image")
                        .clickable { pickImage.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                )
                Text("The streamer makes invites on Cuebot's Mod portal page. An invite works once, for one account.",
                    color = colors.muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(vertical = 8.dp))
                error?.let { Text(it, color = colors.bad, modifier = Modifier.padding(top = 8.dp).testTag("invite-error")) }
            }
        }
    }
}

/** The steps between an invite and the portal: connecting, choosing a provider, the browser, a problem. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Flow(vm: ModViewModel, state: ModState) {
    val colors = LocalCuebotColors.current
    BackHandler { vm.home() }
    val title = when (state) {
        is ModState.Busy -> state.title
        is ModState.Providers -> if (state.joining) "Join as a mod" else "Sign in again"
        is ModState.InBrowser -> "Signing in"
        else -> "Something went wrong"
    }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text(title) },
            navigationIcon = { IconButton(onClick = vm::home) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back") } },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground),
        )
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp).testTag("mod-flow"), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            when (state) {
                is ModState.Busy -> Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.size(12.dp))
                    Text(state.text)
                }
                is ModState.Providers -> {
                    Text(if (state.joining) "The streamer invited you to help with their stream. Sign in so their Cuebot knows it's you. Your account is only used to recognise you; Cuebot does not post or read anything with it."
                    else "This phone's sign-in has expired. Sign in with the same account as before.")
                    state.providers.forEach { p ->
                        OutlinedButton(onClick = { vm.signIn(state.target, p) }, modifier = Modifier.fillMaxWidth().testTag("provider-${p.id}")) { Text("Sign in with ${p.name}") }
                    }
                }
                is ModState.InBrowser -> {
                    Text("Finish signing in with ${state.provider} in the browser. You come back here when it's done.")
                    TextButton(onClick = vm::home) { Text("Cancel") }
                }
                is ModState.Problem -> {
                    Text(state.text, color = colors.bad, modifier = Modifier.testTag("mod-problem"))
                    state.retry?.let { retry -> Button(onClick = retry) { Text("Try again") } }
                }
                else -> Unit
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PortalScreen(vm: ModViewModel, portal: ModState.Portal) {
    val colors = LocalCuebotColors.current
    var menu by remember { mutableStateOf(false) }
    var failed by remember(portal) { mutableStateOf<String?>(null) }
    BackHandler { vm.home() }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text(portal.membership.member.ifEmpty { "Mod portal" }) },
            navigationIcon = { IconButton(onClick = vm::home) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Leave the portal") } },
            actions = {
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More") }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text("Sign out on this phone", color = colors.bad) }, onClick = { menu = false; vm.signOut() })
                    }
                }
            },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground),
        )
        val problem = failed ?: when {
            portal.js == null || portal.css == null -> "The streamer's Cuebot needs an update for the full mod portal. This phone stays signed in until then."
            !portalWebViewSupported() -> "This phone's Android System WebView is too old for the mod portal. Update it from the Play Store."
            else -> null
        }
        if (problem != null) {
            Text(problem, textAlign = TextAlign.Center, color = colors.muted, modifier = Modifier.fillMaxWidth().padding(32.dp).testTag("portal-unavailable"))
        } else {
            PortalWebView(portal, onSignOut = vm::signOut, onFailed = { failed = "The mod portal did not start: $it" }, modifier = Modifier.fillMaxSize().testTag("portal-web"))
        }
    }
}

@Composable
private fun Note(text: String, dismiss: () -> Unit) {
    val colors = LocalCuebotColors.current
    Card(colors = CardDefaults.cardColors(containerColor = colors.card), modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).testTag("mod-note")) {
        Row(Modifier.padding(start = 14.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(text, modifier = Modifier.weight(1f))
            IconButton(onClick = dismiss) { Icon(Icons.Filled.Close, contentDescription = "Dismiss") }
        }
    }
}

@Composable
private fun Header(text: String) {
    Text(text.uppercase(), color = LocalCuebotColors.current.muted, style = MaterialTheme.typography.labelMedium,
        modifier = Modifier.padding(start = 16.dp, top = 24.dp, bottom = 8.dp))
}

@Composable
private fun Footer(text: String) {
    Text(text, color = LocalCuebotColors.current.muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
}
