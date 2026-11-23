package digital.techdaddy.cuebot.companion.ui.chat

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.foundation.interaction.DragInteraction
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.snapshotFlow
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.AppContainer
import digital.techdaddy.cuebot.companion.ui.ConnectionGate
import digital.techdaddy.cuebot.companion.ui.MenuButton
import digital.techdaddy.cuebot.companion.ui.ReconnectingBar
import digital.techdaddy.cuebot.companion.ui.Unavailable
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.ChatLine
import digital.techdaddy.cuebot.core.EventsCardSettings
import digital.techdaddy.cuebot.core.FeedItem
import digital.techdaddy.cuebot.core.ModAction
import digital.techdaddy.cuebot.core.PlatformCapabilities
import digital.techdaddy.cuebot.core.SendAs
import digital.techdaddy.cuebot.core.ToolCapability
import kotlinx.coroutines.launch

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ChatScreen(vm: ChatViewModel, container: AppContainer, events: List<FeedItem>, goToSettings: () -> Unit) {
    val state by vm.state.collectAsStateWithLifecycle()
    val values by container.store.values.collectAsStateWithLifecycle()
    val card = EventsCardSettings.from(values)
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("Chat") },
            navigationIcon = { MenuButton() },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = LocalCuebotColors.current.ground),
        )
        ConnectionGate(container, goToSettings) {
            // The events card (issue #45): beside chat when both fit, a strip above it otherwise; hidden in Settings.
            BoxWithConstraints(Modifier.fillMaxSize()) {
                val shown = card.filter(events)
                when {
                    !card.visible -> Content(vm, state)
                    maxWidth >= WIDE_CHAT -> Row(Modifier.fillMaxSize()) {
                        Box(Modifier.weight(1f)) { Content(vm, state) }
                        EventsPanel(shown)
                    }
                    else -> Column(Modifier.fillMaxSize()) {
                        EventsStrip(shown, card.expanded) { container.store.put(EventsCardSettings.EXPANDED_KEY, (!card.expanded).toString()) }
                        Box(Modifier.weight(1f)) { Content(vm, state) }
                    }
                }
            }
        }
    }
}

/**
 * From this width the events card sits beside chat instead of above it: only when chat still gets [CHAT_MIN_WIDTH]
 * next to the card (a tablet in landscape, a large tablet upright). A 600dp tablet held upright gets the strip above
 * chat, or chat and its "Send as" row would be squeezed into half the screen.
 */
private val CHAT_MIN_WIDTH = 480.dp
private val WIDE_CHAT = EventsPanelWidth + CHAT_MIN_WIDTH

@Composable
private fun Content(vm: ChatViewModel, state: ChatViewModel.UiState) {
    var acting by remember { mutableStateOf<ChatLine?>(null) }
    when {
        state.tooOld -> Unavailable(
            Icons.Filled.SystemUpdate, "Update Cuebot",
            "This Cuebot is too old for the Chat tab. Update Cuebot on your computer to read and moderate chat here.",
        )
        else -> Column(Modifier.fillMaxSize().imePadding()) {
            if (state.isReconnecting) ReconnectingBar()
            PlatformFilter(state, vm::showPlatform)
            Messages(state, Modifier.weight(1f)) { acting = it }
            state.error?.let { Text(it, color = LocalCuebotColors.current.bad, modifier = Modifier.padding(horizontal = 12.dp)) }
            OutcomeLine(state.outcome, vm::dismissOutcome)
            HorizontalDivider(color = LocalCuebotColors.current.line)
            SendBox(vm, state)
        }
    }
    acting?.let { line -> ModerationDialog(line, state.capabilities(line.platform), vm, onClose = { acting = null }) }
}

@Composable
private fun PlatformFilter(state: ChatViewModel.UiState, choose: (String?) -> Unit) {
    if (state.platforms.size < 2) return
    LazyRow(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        item {
            FilterChip(selected = state.filter == null, onClick = { choose(null) }, label = { Text("All") }, modifier = Modifier.testTag("chat-filter-all"))
        }
        items(state.platforms) { p ->
            FilterChip(selected = state.filter == p, onClick = { choose(p) }, label = { Text(p) }, modifier = Modifier.testTag("chat-filter-$p"))
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun Messages(state: ChatViewModel.UiState, modifier: Modifier, onAct: (ChatLine) -> Unit) {
    val colors = LocalCuebotColors.current
    val rows = state.visibleRows
    val list = rememberLazyListState()
    // Follow new lines while the list is at (or near) the bottom. Whether it follows is decided by the reader's own
    // scrolling, not by what is on screen, so a list that is laid out afresh (turning the device, the events card
    // moving between beside and above chat) opens at the newest line instead of the oldest.
    var follow by rememberSaveable { mutableStateOf(true) }
    var dragged by remember { mutableStateOf(false) }
    LaunchedEffect(list) {
        list.interactionSource.interactions.collect { if (it is DragInteraction.Start) dragged = true }
    }
    LaunchedEffect(list) {
        // When a scroll the reader started comes to rest: follow again only if they left it at the bottom.
        snapshotFlow { list.isScrollInProgress }.collect { scrolling ->
            if (!scrolling && dragged) {
                dragged = false
                follow = !list.canScrollForward
            }
        }
    }
    val count by rememberUpdatedState(rows.size)
    LaunchedEffect(list) {
        // New lines, or a new height (turning the device, the keyboard): stay on the newest line while following.
        // requestScrollToItem, not scrollToItem: the viewport height changes during layout, and this can run before
        // that layout pass ends; scrollToItem forces a remeasure then and Compose throws ("performMeasureAndLayout
        // called during measure layout"). The request is applied on the next measure instead.
        snapshotFlow { count to list.layoutInfo.viewportSize.height }.collect { (n, _) -> if (n > 0 && follow) list.requestScrollToItem(n - 1) }
    }
    if (state.loaded && rows.isEmpty()) {
        Box(modifier.fillMaxWidth(), contentAlignment = Alignment.Center) {
            Text("No chat yet. Messages from every connected platform show up here.", color = colors.muted, modifier = Modifier.padding(24.dp).testTag("chat-empty"))
        }
        return
    }
    LazyColumn(modifier.fillMaxWidth().testTag("chat-list"), state = list, verticalArrangement = Arrangement.spacedBy(2.dp)) {
        items(rows, key = { it.key }) { row ->
            when (row) {
                is ChatViewModel.Row.Notice -> Text(
                    row.text, color = colors.muted, fontStyle = FontStyle.Italic, style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.padding(horizontal = 12.dp, vertical = 2.dp),
                )
                is ChatViewModel.Row.Message -> {
                    val line = row.line
                    // Chat is shown as plain text only: no links, markup or images from viewers.
                    val text = buildAnnotatedString {
                        withStyle(SpanStyle(color = colors.platform(line.platform), fontWeight = FontWeight.Bold)) { append(line.platform.take(2).uppercase()) }
                        append("  ")
                        withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(line.user) }
                        append(": ")
                        withStyle(SpanStyle(textDecoration = if (line.deleted) TextDecoration.LineThrough else null, color = if (line.deleted) colors.muted else colors.text)) {
                            append(line.text)
                        }
                        if (line.deleted) withStyle(SpanStyle(color = colors.muted, fontStyle = FontStyle.Italic)) { append("  (deleted)") }
                    }
                    Text(
                        text,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.fillMaxWidth()
                            .combinedClickable(onClick = { onAct(line) }, onLongClick = { onAct(line) }, onClickLabel = "Moderate")
                            .padding(horizontal = 12.dp, vertical = 4.dp)
                            .testTag("chat-line-${line.id}"),
                    )
                }
            }
        }
    }
}

@Composable
private fun OutcomeLine(outcome: ChatViewModel.Outcome?, dismiss: () -> Unit) {
    if (outcome == null) return
    val colors = LocalCuebotColors.current
    val uri = LocalUriHandler.current
    Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(outcome.text, color = if (outcome.ok) colors.good else colors.warn, style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.weight(1f).testTag("chat-outcome"))
        if (outcome.websiteUrl.startsWith("https://")) TextButton(onClick = { runCatching { uri.openUri(outcome.websiteUrl) } }) { Text("On the website") }
        TextButton(onClick = dismiss) { Text("OK") }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SendBox(vm: ChatViewModel, state: ChatViewModel.UiState) {
    var text by rememberSaveable { mutableStateOf("") }
    var picking by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxWidth().padding(8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Box {
                OutlinedButton(onClick = { picking = true }, modifier = Modifier.testTag("chat-send-to")) {
                    Text(state.sendTo)
                    Icon(Icons.Filled.ArrowDropDown, contentDescription = "Choose where to send")
                }
                DropdownMenu(expanded = picking, onDismissRequest = { picking = false }) {
                    state.sendTargets.forEach { p ->
                        DropdownMenuItem(text = { Text(p) }, onClick = { vm.sendTo(p); picking = false }, modifier = Modifier.testTag("chat-send-to-$p"))
                    }
                }
            }
            Text("Send as", style = MaterialTheme.typography.labelMedium)
            SingleChoiceSegmentedButtonRow {
                val options = if (state.canSendAsBot) listOf(SendAs.Broadcaster, SendAs.Bot) else listOf(SendAs.Broadcaster)
                options.forEachIndexed { i, option ->
                    SegmentedButton(
                        selected = state.sendAs == option || (option == SendAs.Broadcaster && state.sendAs == SendAs.Default),
                        onClick = { vm.sendAs(option) },
                        shape = SegmentedButtonDefaults.itemShape(i, options.size),
                        modifier = Modifier.testTag("chat-as-${option.wire}"),
                    ) { Text(if (option == SendAs.Bot) "Bot" else "Broadcaster", maxLines = 1, softWrap = false) }
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(
                value = text, onValueChange = { text = it }, placeholder = { Text("Send a message") }, singleLine = true,
                modifier = Modifier.weight(1f).testTag("chat-input"),
            )
            IconButton(
                onClick = { scope.launch { if (vm.send(text)) text = "" } },
                enabled = text.isNotBlank() && !state.sending,
                modifier = Modifier.testTag("chat-send"),
            ) { Icon(Icons.AutoMirrored.Filled.Send, contentDescription = "Send") }
        }
    }
}

/** The tools for one message and its author, each enabled as the platform allows, with the reason when it is not. */
@Composable
private fun ModerationDialog(line: ChatLine, caps: PlatformCapabilities, vm: ChatViewModel, onClose: () -> Unit) {
    val colors = LocalCuebotColors.current
    val uri = LocalUriHandler.current
    var confirmBan by remember { mutableStateOf(false) }
    var warning by remember { mutableStateOf<String?>(null) }

    @Composable
    fun Tool(action: ModAction, label: String = action.label, tag: String = "mod-${action.wire}", enabled: Boolean = true, note: String? = null, run: () -> Unit) {
        val tool = caps.tool(action)
        val canRun = tool.canRun && enabled
        Column(Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = run, enabled = canRun, modifier = Modifier.testTag(tag)) { Text(label) }
                if (tool.state == ToolCapability.State.OnWebsite && tool.websiteUrl.startsWith("https://")) {
                    TextButton(onClick = { runCatching { uri.openUri(tool.websiteUrl) } }, modifier = Modifier.testTag("$tag-website")) { Text("On the website") }
                }
            }
            val why = note ?: tool.note.takeIf { it.isNotEmpty() && (!tool.canRun || tool.state == ToolCapability.State.Closest) }
            if (why != null) Text(why, color = colors.muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(start = 12.dp))
        }
    }

    AlertDialog(
        onDismissRequest = onClose,
        title = { Text(line.user.ifEmpty { "Message" }) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()).testTag("mod-dialog")) {
                Text("${line.platform} · ${line.userId}", color = colors.muted, style = MaterialTheme.typography.bodySmall)
                Text(line.text, modifier = Modifier.padding(vertical = 6.dp))
                Tool(ModAction.Delete, enabled = line.canDelete && !line.deleted,
                    note = if (!line.canDelete) "${line.platform} gave this message no id, so it cannot be deleted." else null) {
                    vm.moderate(line, ModAction.Delete); onClose()
                }
                val presets = caps.timeoutPresets
                if (presets.isEmpty()) Tool(ModAction.Timeout, "Timeout 10 min", "mod-timeout-600") { vm.moderate(line, ModAction.Timeout, 600); onClose() }
                presets.forEach { preset ->
                    Tool(ModAction.Timeout, "Timeout ${preset.label}", "mod-timeout-${preset.requestedSeconds}",
                        note = preset.note.ifEmpty { null }) { vm.moderate(line, ModAction.Timeout, preset.requestedSeconds); onClose() }
                }
                Tool(ModAction.Ban) { confirmBan = true }
                Tool(ModAction.Unban) { vm.moderate(line, ModAction.Unban); onClose() }
                Tool(ModAction.Warn) { warning = "" }
            }
        },
        confirmButton = { TextButton(onClick = onClose) { Text("Close") } },
    )

    if (confirmBan) {
        AlertDialog(
            onDismissRequest = { confirmBan = false },
            title = { Text("Ban ${line.user}?") },
            text = { Text("${line.user} can no longer chat on ${line.platform} until you unban them.") },
            confirmButton = {
                TextButton(onClick = { vm.moderate(line, ModAction.Ban); confirmBan = false; onClose() }, modifier = Modifier.testTag("mod-ban-confirm")) {
                    Text("Ban", color = colors.bad)
                }
            },
            dismissButton = { TextButton(onClick = { confirmBan = false }) { Text("Cancel") } },
        )
    }

    warning?.let { reason ->
        val max = caps.tool(ModAction.Warn).maxReasonLength
        AlertDialog(
            onDismissRequest = { warning = null },
            title = { Text("Warn ${line.user}") },
            text = {
                Column {
                    OutlinedTextField(value = reason, onValueChange = { warning = if (max != null) it.take(max) else it },
                        label = { Text("Warning") }, modifier = Modifier.testTag("mod-warn-reason").semantics { contentDescription = "Warning text" })
                    Spacer(Modifier.size(4.dp))
                    caps.tool(ModAction.Warn).note.takeIf { it.isNotEmpty() }?.let { Text(it, color = colors.muted, style = MaterialTheme.typography.bodySmall) }
                }
            },
            confirmButton = {
                TextButton(onClick = { vm.moderate(line, ModAction.Warn, reason = reason); warning = null; onClose() }, enabled = reason.isNotBlank(),
                    modifier = Modifier.testTag("mod-warn-confirm")) { Text("Warn") }
            },
            dismissButton = { TextButton(onClick = { warning = null }) { Text("Cancel") } },
        )
    }
}
