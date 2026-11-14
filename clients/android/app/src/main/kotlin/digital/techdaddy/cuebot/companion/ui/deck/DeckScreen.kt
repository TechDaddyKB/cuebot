package digital.techdaddy.cuebot.companion.ui.deck

import android.app.Activity
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Fullscreen
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.Layers
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.AppContainer
import digital.techdaddy.cuebot.companion.ui.ConnectionGate
import digital.techdaddy.cuebot.companion.ui.ReconnectingBar
import digital.techdaddy.cuebot.companion.ui.Unavailable
import digital.techdaddy.cuebot.companion.ui.theme.CuebotTheme
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.companion.ui.theme.cuebotColor
import digital.techdaddy.cuebot.core.DeckView
import digital.techdaddy.cuebot.core.HexColor
import kotlin.math.abs

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DeckScreen(vm: DeckViewModel, container: AppContainer, streamMode: Boolean, onStreamMode: (Boolean) -> Unit, goToSettings: () -> Unit) {
    val state by vm.state.collectAsStateWithLifecycle()
    val view = state.view
    StreamModeEffects(streamMode)
    StatusBarIcons(view?.background?.let(HexColor::parse)?.isDark)
    if (streamMode) BackHandler { onStreamMode(false) }

    // Light or dark to match the deck's own background, so titles and bars stay readable on it.
    val deckDark = view?.background?.let(HexColor::parse)?.isDark
    val body: @Composable () -> Unit = {
        val colors = LocalCuebotColors.current
        Column(Modifier.fillMaxSize().background(view?.let { cuebotColor(it.background, colors.ground) } ?: colors.ground)) {
            if (!streamMode) {
                TopAppBar(
                    title = { Text(view?.name?.takeIf { it.isNotEmpty() } ?: "Deck", maxLines = 1) },
                    navigationIcon = { if (state.decks.size > 1) DeckMenu(state, vm) },
                    actions = {
                        IconButton(onClick = { onStreamMode(true) }, enabled = view != null, modifier = Modifier.testTag("stream-mode")) {
                            Icon(Icons.Filled.Fullscreen, contentDescription = "Stream mode")
                        }
                    },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = androidx.compose.ui.graphics.Color.Transparent),
                )
            }
            ConnectionGate(container, goToSettings) {
                when {
                    view != null -> DeckContent(vm, container, view, state, streamMode, onStreamMode)
                    state.decksError != null -> Unavailable(Icons.Filled.GridView, "No deck", state.decksError!!)
                    state.deckError != null -> Unavailable(Icons.Filled.GridView, "Can't open the deck", state.deckError!!,
                        if (state.deckNeedsPairing) {
                            { Button(onClick = goToSettings, modifier = Modifier.testTag("deck-pair-again")) { Text("Pair again") } }
                        } else null)
                    state.needsChoice -> DeckChooser(state, vm)
                    else -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            CircularProgressIndicator()
                            Spacer(Modifier.size(12.dp))
                            Text("Loading the deck…", color = colors.muted)
                        }
                    }
                }
            }
        }
    }
    if (deckDark != null) {
        CuebotTheme(dark = deckDark) {
            CompositionLocalProvider(LocalContentColor provides LocalCuebotColors.current.text, content = body)
        }
    } else {
        body()
    }
}

@Composable
private fun DeckContent(vm: DeckViewModel, container: AppContainer, view: DeckView, state: DeckViewModel.UiState, streamMode: Boolean, onStreamMode: (Boolean) -> Unit) {
    val swipe = with(LocalDensity.current) { 40.dp.toPx() }
    Box(Modifier.fillMaxSize()) {
        Column(Modifier.fillMaxSize()) {
            if (state.isReconnecting) ReconnectingBar()
            var dragged = 0f
            DeckGrid(
                vm, container, view, streamMode,
                Modifier.weight(1f).fillMaxWidth().pointerInput(view.id) {
                    detectHorizontalDragGestures(
                        onDragStart = { dragged = 0f },
                        onDragEnd = { if (abs(dragged) > swipe) vm.page(if (dragged < 0) 1 else -1) },
                    ) { _, amount -> dragged += amount }
                },
            )
            if (!streamMode && view.pageCount > 1) PageBar(view, vm)
        }
        NotificationBanner(state, vm, Modifier.align(Alignment.TopCenter))
        if (streamMode) {
            // Press and hold the top-right corner to leave stream mode (Back works too).
            Box(
                Modifier.align(Alignment.TopEnd).size(64.dp)
                    .semantics { contentDescription = "Leave stream mode"; role = Role.Button; onClick { onStreamMode(false); true } }
                    .pointerInput(Unit) { detectTapGestures(onLongPress = { onStreamMode(false) }) }
                    .testTag("leave-stream-mode"),
            )
        }
    }
}

@Composable
private fun DeckMenu(state: DeckViewModel.UiState, vm: DeckViewModel) {
    var open by remember { mutableStateOf(false) }
    IconButton(onClick = { open = true }) { Icon(Icons.Filled.Layers, contentDescription = "Choose deck") }
    DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
        state.decks.forEach { d ->
            DropdownMenuItem(
                text = { Text(d.name) },
                onClick = { vm.open(d.id); open = false },
                trailingIcon = { if (d.id == state.deckId) Icon(Icons.Filled.Check, contentDescription = "Current") },
            )
        }
    }
}

@Composable
private fun DeckChooser(state: DeckViewModel.UiState, vm: DeckViewModel) {
    val colors = LocalCuebotColors.current
    Column {
        Text("Choose a deck", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(16.dp))
        LazyColumn {
            items(state.decks, key = { it.id }) { d ->
                ListItem(
                    headlineContent = { Text(d.name, fontWeight = FontWeight.SemiBold) },
                    supportingContent = { Text("${d.columns} × ${d.rows}" + if (d.pageCount > 1) ", ${d.pageCount} pages" else "", color = colors.muted) },
                    modifier = Modifier.clickable { vm.open(d.id) },
                )
                HorizontalDivider(color = colors.line)
            }
        }
    }
}

@Composable
private fun PageBar(view: DeckView, vm: DeckViewModel) {
    Surface(color = LocalCuebotColors.current.card.copy(alpha = 0.92f), contentColor = LocalCuebotColors.current.text) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { vm.page(-1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, contentDescription = "Previous page") }
            Text(
                if (view.pageName.isEmpty()) "Page ${view.page + 1} of ${view.pageCount}" else "${view.pageName} · ${view.page + 1}/${view.pageCount}",
                style = MaterialTheme.typography.labelLarge,
                modifier = Modifier.weight(1f).testTag("page-label"),
                textAlign = androidx.compose.ui.text.style.TextAlign.Center,
            )
            IconButton(onClick = { vm.page(1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = "Next page") }
        }
    }
}

@Composable
private fun NotificationBanner(state: DeckViewModel.UiState, vm: DeckViewModel, modifier: Modifier) {
    val note = state.notification
    AnimatedVisibility(note != null, modifier = modifier, enter = slideInVertically() + fadeIn(), exit = slideOutVertically() + fadeOut()) {
        val shown = note ?: return@AnimatedVisibility
        Surface(
            shape = RoundedCornerShape(12.dp),
            color = MaterialTheme.colorScheme.surfaceContainerHigh,
            shadowElevation = 6.dp,
            modifier = Modifier.padding(16.dp).widthIn(max = 480.dp).fillMaxWidth().clickable { vm.dismissNotification() }.testTag("deck-notification"),
        ) {
            Column(Modifier.padding(12.dp)) {
                Text(shown.title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.Bold)
                if (shown.description.isNotEmpty()) Text(shown.description, style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

/** Stream mode: the deck alone, without system bars, and the screen kept on. */
@Composable
private fun StreamModeEffects(on: Boolean) {
    val view = LocalView.current
    DisposableEffect(on) {
        val window = (view.context as? Activity)?.window
        val insets = window?.let { WindowCompat.getInsetsController(it, view) }
        if (on) {
            insets?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            insets?.hide(WindowInsetsCompat.Type.systemBars())
            view.keepScreenOn = true
        }
        onDispose {
            if (on) {
                insets?.show(WindowInsetsCompat.Type.systemBars())
                view.keepScreenOn = false
            }
        }
    }
}

/** Light status bar icons over a dark deck and dark ones over a light deck; back to the app's own when it goes. */
@Composable
private fun StatusBarIcons(deckDark: Boolean?) {
    val view = LocalView.current
    val appDark = !androidx.compose.material3.MaterialTheme.colorScheme.background.let { it.red + it.green + it.blue > 1.5f }
    DisposableEffect(deckDark, appDark) {
        val window = (view.context as? Activity)?.window
        val insets = window?.let { WindowCompat.getInsetsController(it, view) }
        insets?.isAppearanceLightStatusBars = !(deckDark ?: appDark)
        onDispose { insets?.isAppearanceLightStatusBars = !appDark }
    }
}
