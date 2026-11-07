package digital.techdaddy.cuebot.companion.ui.dashboard

import android.text.format.DateUtils
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.staggeredgrid.LazyVerticalStaggeredGrid
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridCells
import androidx.compose.foundation.lazy.staggeredgrid.StaggeredGridItemSpan
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.List
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.BarChart
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.EmojiEvents
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.PushPin
import androidx.compose.material.icons.filled.RadioButtonUnchecked
import androidx.compose.material.icons.filled.SystemUpdate
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.PushPin
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.AppContainer
import digital.techdaddy.cuebot.companion.ui.ConnectionGate
import digital.techdaddy.cuebot.companion.ui.ReconnectingBar
import digital.techdaddy.cuebot.companion.ui.Unavailable
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.ConnectionInfo
import digital.techdaddy.cuebot.core.ConnectionState
import digital.techdaddy.cuebot.core.CuebotClient
import digital.techdaddy.cuebot.core.FeedItem
import digital.techdaddy.cuebot.core.PointsGoal
import digital.techdaddy.cuebot.core.ProfileInfo
import digital.techdaddy.cuebot.core.ProfileSwitch
import digital.techdaddy.cuebot.core.StreamTotals
import digital.techdaddy.cuebot.core.SubathonTimer
import digital.techdaddy.cuebot.core.TopSpenders
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonElement
import java.text.DecimalFormat
import java.time.OffsetDateTime

/** Connections, quick actions, subathon timers, goals, top spenders, stream totals and the activity feed. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(vm: DashboardViewModel, container: AppContainer, goToSettings: () -> Unit) {
    val state by vm.state.collectAsStateWithLifecycle()
    var editing by rememberSaveable { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        TopAppBar(
            title = { Text("Dashboard") },
            actions = {
                IconButton(onClick = { editing = true }, modifier = Modifier.testTag("edit-quick-actions")) {
                    Icon(Icons.Filled.Bolt, contentDescription = "Quick actions")
                }
            },
            colors = TopAppBarDefaults.topAppBarColors(containerColor = LocalCuebotColors.current.ground),
        )
        ConnectionGate(container, goToSettings) { Content(vm, state) { editing = true } }
    }
    if (editing) QuickActionsEditor(vm, state) { editing = false }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun Content(vm: DashboardViewModel, state: DashboardViewModel.UiState, edit: () -> Unit) {
    val dashboard = state.dashboard
    when {
        state.tooOld -> Unavailable(
            Icons.Filled.SystemUpdate, "Update Cuebot",
            "This Cuebot is too old for the Dashboard. The Deck tab still works; update Cuebot on your computer to see timers, goals, top spenders and the feed here.",
        )
        dashboard != null -> Column {
            if (state.isReconnecting) ReconnectingBar()
            PullToRefreshBox(isRefreshing = state.refreshing, onRefresh = vm::refresh, modifier = Modifier.fillMaxSize()) {
                LazyVerticalStaggeredGrid(
                    columns = StaggeredGridCells.Adaptive(340.dp),
                    contentPadding = PaddingValues(16.dp),
                    horizontalArrangement = Arrangement.spacedBy(16.dp),
                    verticalItemSpacing = 16.dp,
                    modifier = Modifier.fillMaxSize().testTag("dashboard"),
                ) {
                    if (state.showsProfiles) item(span = StaggeredGridItemSpan.FullLine) { ProfileCard(vm, state) }
                    item(span = StaggeredGridItemSpan.FullLine) { ConnectionsStrip(dashboard.connections) }
                    state.controlError?.let { error ->
                        item(span = StaggeredGridItemSpan.FullLine) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Icon(Icons.Filled.Warning, contentDescription = null, tint = LocalCuebotColors.current.bad, modifier = Modifier.size(16.dp))
                                Spacer(Modifier.width(6.dp))
                                Text(error, color = LocalCuebotColors.current.bad, style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    }
                    item { QuickActionsCard(vm, state, edit) }
                    items(dashboard.subathon.timers.size) { SubathonCard(vm, dashboard.subathon.timers[it]) }
                    items(dashboard.subathon.goals.size) { GoalCard(vm, dashboard.subathon.goals[it]) }
                    if (dashboard.topSpenders.periodKeys.isNotEmpty()) item { TopSpendersCard(dashboard.topSpenders) }
                    item { TotalsCard(dashboard.totals) }
                    item { FeedCard(dashboard.feed) }
                }
            }
        }
        state.error != null -> Unavailable(Icons.Filled.Warning, "Dashboard unavailable", state.error)
        else -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator()
                Spacer(Modifier.size(12.dp))
                Text("Loading the dashboard…", color = LocalCuebotColors.current.muted)
            }
        }
    }
}

/** A card: a titled, rounded group. */
@Composable
fun Card(title: String, icon: ImageVector, modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    val colors = LocalCuebotColors.current
    Column(
        modifier.fillMaxWidth().background(colors.card, RoundedCornerShape(16.dp)).border(0.5.dp, colors.line, RoundedCornerShape(16.dp)).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(8.dp))
            Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        }
        content()
    }
}

// Profiles (personas)

/** A profile's colour, like `#8B5CF6`; the accent when it is not one. */
@Composable
private fun profileColor(hex: String): Color =
    runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(MaterialTheme.colorScheme.primary)

/**
 * The open profile (persona) with a Switch button: pick another profile, confirm, and Cuebot restarts on it. While a
 * stream is live Cuebot refuses, and the phone asks again before switching anyway.
 */
@Composable
private fun ProfileCard(vm: DashboardViewModel, state: DashboardViewModel.UiState) {
    val colors = LocalCuebotColors.current
    val scope = rememberCoroutineScope()
    val profiles = state.profiles ?: return
    var picking by remember { mutableStateOf(false) }
    var confirming by remember { mutableStateOf<ProfileInfo?>(null) }
    var live by remember { mutableStateOf<Pair<ProfileInfo, String>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    fun switch(profile: ProfileInfo, force: Boolean) {
        scope.launch {
            error = null
            vm.switchProfile(profile, force)
                .onSuccess { outcome ->
                    when (outcome) {
                        is ProfileSwitch.Live -> live = profile to outcome.message
                        is ProfileSwitch.Busy -> error = outcome.message
                        else -> Unit
                    }
                }
                .onFailure { error = it.message }
        }
    }
    Row(
        Modifier.fillMaxWidth().background(colors.card, RoundedCornerShape(16.dp)).border(0.5.dp, colors.line, RoundedCornerShape(16.dp))
            .padding(horizontal = 16.dp, vertical = 10.dp).testTag("profile-card"),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val shown = state.switchingTo ?: profiles.current
        Box(Modifier.size(12.dp).background(profileColor(shown?.color ?: ""), RoundedCornerShape(6.dp)))
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Text("Profile", color = colors.muted, style = MaterialTheme.typography.labelSmall)
            Text(
                state.switchingTo?.let { "Switching to ${it.name}…" } ?: profiles.current?.name ?: "",
                fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
            error?.let { Text(it, color = colors.bad, style = MaterialTheme.typography.bodySmall) }
        }
        TextButton(onClick = { picking = true }, enabled = state.switchingTo == null, modifier = Modifier.testTag("profile-switch")) { Text("Switch") }
    }
    if (picking) {
        AlertDialog(
            onDismissRequest = { picking = false },
            title = { Text("Switch profile") },
            text = {
                Column {
                    profiles.profiles.forEach { p ->
                        Row(
                            Modifier.fillMaxWidth().heightIn(min = 48.dp)
                                .clickable(enabled = !p.open) { picking = false; confirming = p }.testTag("profile-${p.id}"),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Box(Modifier.size(12.dp).background(profileColor(p.color), RoundedCornerShape(6.dp)))
                            Spacer(Modifier.width(12.dp))
                            Text(p.name, modifier = Modifier.weight(1f), fontWeight = if (p.open) FontWeight.SemiBold else FontWeight.Normal)
                            if (p.open) Text("Open", color = colors.muted, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { picking = false }) { Text("Close") } },
        )
    }
    confirming?.let { p ->
        AlertDialog(
            onDismissRequest = { confirming = null },
            title = { Text("Switch to ${p.name}?") },
            text = { Text("Cuebot restarts on that profile, with its own accounts, mods and actions. It takes a few seconds.") },
            confirmButton = { TextButton(onClick = { confirming = null; switch(p, force = false) }, modifier = Modifier.testTag("profile-confirm")) { Text("Switch") } },
            dismissButton = { TextButton(onClick = { confirming = null }) { Text("Cancel") } },
        )
    }
    live?.let { (p, message) ->
        AlertDialog(
            onDismissRequest = { live = null },
            title = { Text("You are live") },
            text = { Text("$message. Switching to ${p.name} now changes your accounts and actions mid-stream.") },
            confirmButton = { TextButton(onClick = { live = null; switch(p, force = true) }, modifier = Modifier.testTag("profile-force")) { Text("Switch anyway") } },
            dismissButton = { TextButton(onClick = { live = null }) { Text("Cancel") } },
        )
    }
}

// Connections

@Composable
private fun ConnectionsStrip(connections: List<ConnectionInfo>) {
    val colors = LocalCuebotColors.current
    var shown by remember { mutableStateOf<ConnectionInfo?>(null) }
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        connections.forEach { item ->
            Row(
                Modifier.clip(CircleShape).background(colors.card, CircleShape).border(0.5.dp, colors.line, CircleShape).clickable { shown = item }
                    .padding(horizontal = 10.dp, vertical = 6.dp)
                    .semantics(mergeDescendants = true) { contentDescription = "${item.name}, ${item.state.label}" },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(Modifier.size(9.dp).background(stateColor(item.state), CircleShape))
                Spacer(Modifier.width(6.dp))
                Text(item.name, style = MaterialTheme.typography.labelMedium)
            }
        }
    }
    shown?.let { item ->
        AlertDialog(
            onDismissRequest = { shown = null },
            title = { Text(item.name) },
            text = { Text(if (item.status.isEmpty()) item.state.label else "${item.state.label}: ${item.status}") },
            confirmButton = { TextButton(onClick = { shown = null }) { Text("OK") } },
        )
    }
}

@Composable
private fun stateColor(state: ConnectionState): Color {
    val c = LocalCuebotColors.current
    return when (state) {
        ConnectionState.Connected -> c.good
        ConnectionState.Connecting -> c.warn
        ConnectionState.Error -> c.bad
        else -> c.muted
    }
}

// Subathon

private enum class TimerAsk { Minutes, Multiplier }

@Composable
private fun SubathonCard(vm: DashboardViewModel, timer: SubathonTimer) {
    val colors = LocalCuebotColors.current
    var asking by remember { mutableStateOf<TimerAsk?>(null) }
    var menu by remember { mutableStateOf(false) }
    // Counted down here from endsAt: no update from Cuebot is needed every second.
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(timer) {
        while (true) {
            now = System.currentTimeMillis()
            delay(1_000 - now % 1_000)
        }
    }
    Card(timer.name.ifEmpty { "Subathon" }, Icons.Filled.Timer, Modifier.testTag("subathon-${timer.id}")) {
        Text(
            SubathonTimer.format(timer.remainingAt(now)),
            fontSize = 44.sp, fontWeight = FontWeight.Bold, fontFamily = FontFamily.Monospace,
            textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().testTag("subathon-time")
                .semantics { contentDescription = "Time left ${SubathonTimer.format(timer.remainingAt(now))}" },
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            val stateColor = when (timer.state) {
                SubathonTimer.State.Running -> colors.good
                SubathonTimer.State.Paused -> colors.warn
                else -> colors.muted
            }
            Text(timer.state.name, color = stateColor, style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold,
                modifier = Modifier.background(stateColor.copy(alpha = 0.2f), CircleShape).padding(horizontal = 8.dp, vertical = 3.dp))
            Spacer(Modifier.weight(1f))
            Text("Added ${SubathonTimer.format(timer.added)}", color = colors.muted, style = MaterialTheme.typography.labelSmall)
            Spacer(Modifier.width(8.dp))
            Text(multiplierText(timer.multiplier), color = colors.muted, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold)
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            when (timer.state) {
                SubathonTimer.State.Running -> ControlButton("Pause", Icons.Filled.Pause) { vm.subathon(timer, CuebotClient.SubathonCommand.pause) }
                SubathonTimer.State.Paused -> ControlButton("Resume", Icons.Filled.PlayArrow) { vm.subathon(timer, CuebotClient.SubathonCommand.resume) }
                else -> ControlButton("Start", Icons.Filled.PlayArrow) { vm.subathon(timer, CuebotClient.SubathonCommand.start) }
            }
            Spacer(Modifier.weight(1f))
            OutlinedButton(onClick = { vm.addTime(timer, 60.0) }, contentPadding = PaddingValues(horizontal = 10.dp)) { Text("+1 min") }
            OutlinedButton(onClick = { vm.addTime(timer, 300.0) }, contentPadding = PaddingValues(horizontal = 10.dp), modifier = Modifier.testTag("add-5-min")) { Text("+5 min") }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More timer controls") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Add or remove minutes…") }, onClick = { menu = false; asking = TimerAsk.Minutes })
                    HorizontalDivider()
                    listOf(0.5, 1.0, 2.0).forEach { v ->
                        DropdownMenuItem(text = { Text(multiplierText(v)) }, onClick = { menu = false; vm.setMultiplier(timer, v) })
                    }
                    DropdownMenuItem(text = { Text("Other multiplier…") }, onClick = { menu = false; asking = TimerAsk.Multiplier })
                }
            }
        }
    }
    when (asking) {
        TimerAsk.Minutes -> NumberPrompt("Change the time", "Minutes to add (negative takes time off).", "10", { asking = null }) { vm.addTime(timer, it * 60) }
        TimerAsk.Multiplier -> NumberPrompt("Multiplier", "How much each event adds, e.g. 1.5.", "1.5", { asking = null }) { if (it > 0) vm.setMultiplier(timer, it) }
        null -> {}
    }
}

@Composable
private fun ControlButton(label: String, icon: ImageVector, onClick: () -> Unit) {
    Button(onClick = onClick, contentPadding = PaddingValues(horizontal = 12.dp)) {
        Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(4.dp))
        Text(label)
    }
}

private val numberFormat = DecimalFormat("#,##0.##")
fun multiplierText(value: Double): String = numberFormat.format(value) + "×"
fun points(value: Double): String = numberFormat.format(value)

// Goals

private enum class GoalAsk { Add, Set, Multiplier }

@Composable
private fun GoalCard(vm: DashboardViewModel, goal: PointsGoal) {
    val colors = LocalCuebotColors.current
    var asking by remember { mutableStateOf<GoalAsk?>(null) }
    var menu by remember { mutableStateOf(false) }
    var confirmReset by remember { mutableStateOf(false) }
    Card(goal.name.ifEmpty { "Goal" }, Icons.Filled.Flag, Modifier.testTag("goal-${goal.id}")) {
        Row(verticalAlignment = Alignment.Bottom) {
            Text(points(goal.points), fontSize = 34.sp, fontWeight = FontWeight.Bold, fontFamily = FontFamily.Monospace, modifier = Modifier.testTag("goal-points"))
            Spacer(Modifier.width(6.dp))
            Text("points", color = colors.muted, modifier = Modifier.padding(bottom = 6.dp))
            Spacer(Modifier.weight(1f))
            if (goal.multiplier != 1.0) Text(multiplierText(goal.multiplier), color = colors.muted, style = MaterialTheme.typography.labelSmall, fontWeight = FontWeight.Bold)
        }
        LinearProgressIndicator(progress = { goal.progressToNext.toFloat() }, modifier = Modifier.fillMaxWidth(), trackColor = colors.raised)
        val next = goal.next
        if (next != null) Text("Next: ${next.name} at ${points(next.points)}", style = MaterialTheme.typography.bodyMedium)
        else if (goal.steps.isNotEmpty()) Text("Every step reached", color = colors.good, style = MaterialTheme.typography.bodyMedium)
        if (goal.steps.isNotEmpty()) {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                goal.steps.forEach { step ->
                    val reached = step.reached || goal.points >= step.points
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Icon(if (reached) Icons.Filled.CheckCircle else Icons.Filled.RadioButtonUnchecked, contentDescription = if (reached) "Reached" else null,
                            tint = if (reached) colors.good else colors.muted, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(6.dp))
                        Text("${step.name} · ${points(step.points)}", style = MaterialTheme.typography.bodySmall)
                    }
                }
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(1.0, 5.0, 10.0).forEach { n ->
                OutlinedButton(onClick = { vm.addPoints(goal, n) }, contentPadding = PaddingValues(horizontal = 10.dp), modifier = Modifier.testTag("goal-add-${n.toInt()}")) { Text("+${n.toInt()}") }
            }
            Spacer(Modifier.weight(1f))
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More goal controls") }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Add points…") }, onClick = { menu = false; asking = GoalAsk.Add })
                    DropdownMenuItem(text = { Text("Set points…") }, onClick = { menu = false; asking = GoalAsk.Set })
                    DropdownMenuItem(text = { Text("Multiplier…") }, onClick = { menu = false; asking = GoalAsk.Multiplier })
                    HorizontalDivider()
                    DropdownMenuItem(text = { Text("Reset", color = colors.bad) }, onClick = { menu = false; confirmReset = true })
                }
            }
        }
    }
    when (asking) {
        GoalAsk.Add -> NumberPrompt("Add points", "Negative takes points off.", "25", { asking = null }) { vm.addPoints(goal, it) }
        GoalAsk.Set -> NumberPrompt("Set points", "The goal's new total.", "100", { asking = null }) { vm.setPoints(goal, it) }
        GoalAsk.Multiplier -> NumberPrompt("Multiplier", "How much each event counts, e.g. 2.", "2", { asking = null }) { if (it > 0) vm.setMultiplier(goal, it) }
        null -> {}
    }
    if (confirmReset) {
        AlertDialog(
            onDismissRequest = { confirmReset = false },
            title = { Text("Reset ${goal.name}?") },
            text = { Text("The points go back to 0 and the steps start over.") },
            confirmButton = { TextButton(onClick = { confirmReset = false; vm.reset(goal) }) { Text("Reset to 0", color = colors.bad) } },
            dismissButton = { TextButton(onClick = { confirmReset = false }) { Text("Cancel") } },
        )
    }
}

// Top spenders

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TopSpendersCard(spenders: TopSpenders) {
    val colors = LocalCuebotColors.current
    var period by rememberSaveable { mutableStateOf("stream") }
    var platform by rememberSaveable { mutableStateOf("all") }
    val periods = spenders.periodKeys
    val current = if (period in periods) period else periods.firstOrNull() ?: "stream"
    val platforms = spenders.platformKeys(current)
    val shownPlatform = if (platform in platforms) platform else "all"
    val board = spenders.board(current, shownPlatform)
    Card("Top spenders", Icons.Filled.EmojiEvents, Modifier.testTag("top-spenders")) {
        // Up to six periods: chips that scroll sideways fit a phone where segments would cut the names off.
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            periods.forEach { key -> FilterChip(selected = key == current, onClick = { period = key }, label = { Text(periodName(key)) }) }
        }
        if (platforms.size > 1) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                platforms.forEach { key ->
                    FilterChip(selected = key == shownPlatform, onClick = { platform = key },
                        label = { Text(if (key == "all") "All platforms" else key.replaceFirstChar { it.uppercase() }) })
                }
            }
        }
        if (board.spenders.isEmpty()) {
            Text("Nobody yet", color = colors.muted, modifier = Modifier.testTag("spenders-empty"))
        } else {
            board.spenders.take(5).forEach { s ->
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.semantics(mergeDescendants = true) {}) {
                    Text("${s.rank}", fontWeight = FontWeight.Bold, modifier = Modifier.width(24.dp))
                    PlatformIcon(s.platform)
                    Spacer(Modifier.width(8.dp))
                    Text(s.name, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Text(s.formatted, fontWeight = FontWeight.SemiBold, fontFamily = FontFamily.Monospace)
                }
            }
        }
        if (board.formattedTotal.isNotEmpty()) Text("Total ${board.formattedTotal}", color = colors.muted, style = MaterialTheme.typography.labelSmall)
    }
}

private fun periodName(key: String) = when (key) {
    "stream" -> "Stream"
    "day" -> "Day"
    "week" -> "Week"
    "month" -> "Month"
    "year" -> "Year"
    "alltime", "all" -> "All time"
    else -> key.replaceFirstChar { it.uppercase() }
}

// Totals

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TotalsCard(totals: Map<String, JsonElement>) {
    val colors = LocalCuebotColors.current
    val rows = StreamTotals.rows(totals)
    Card("This stream", Icons.Filled.BarChart, Modifier.testTag("totals")) {
        if (rows.isEmpty()) {
            Text("Nothing counted yet this stream", color = colors.muted)
        } else {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                rows.forEach { row ->
                    Column(Modifier.widthIn(min = 100.dp).semantics(mergeDescendants = true) {}) {
                        Text(row.value, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        Text(row.label, style = MaterialTheme.typography.labelMedium)
                        if (row.detail.isNotEmpty()) Text(row.detail, color = colors.muted, style = MaterialTheme.typography.labelSmall, modifier = Modifier.testTag("total-detail-${row.type}"))
                    }
                }
            }
        }
    }
}

// Feed

@Composable
private fun FeedCard(feed: List<FeedItem>) {
    val colors = LocalCuebotColors.current
    Card("Activity", Icons.AutoMirrored.Filled.List, Modifier.testTag("feed")) {
        if (feed.isEmpty()) {
            Text("No events yet", color = colors.muted)
        } else {
            feed.take(50).forEach { item ->
                Row(Modifier.fillMaxWidth().semantics(mergeDescendants = true) {}, verticalAlignment = Alignment.Top) {
                    PlatformIcon(item.platform)
                    Spacer(Modifier.width(10.dp))
                    Column(Modifier.weight(1f)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(item.title.ifEmpty { "${item.user} · ${item.kind}" }, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold,
                                maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                            if (item.isTest) {
                                Spacer(Modifier.width(6.dp))
                                Text("TEST", color = colors.warn, fontSize = 10.sp, fontWeight = FontWeight.Bold,
                                    modifier = Modifier.background(colors.warn.copy(alpha = 0.22f), CircleShape).padding(horizontal = 5.dp, vertical = 1.dp).testTag("test-badge"))
                            }
                        }
                        if (item.detail.isNotEmpty()) Text(item.detail, color = colors.muted, style = MaterialTheme.typography.bodySmall, maxLines = 3, overflow = TextOverflow.Ellipsis)
                    }
                    Spacer(Modifier.width(4.dp))
                    Column(horizontalAlignment = Alignment.End) {
                        if (item.amount.isNotEmpty()) Text(item.amount, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.SemiBold)
                        relativeTime(item.time)?.let { Text(it, color = colors.muted, style = MaterialTheme.typography.labelSmall) }
                    }
                }
                HorizontalDivider(color = colors.line)
            }
        }
    }
}

private fun relativeTime(text: String): String? = runCatching {
    val millis = OffsetDateTime.parse(text).toInstant().toEpochMilli()
    DateUtils.getRelativeTimeSpanString(millis, System.currentTimeMillis(), DateUtils.SECOND_IN_MILLIS).toString()
}.getOrNull()

/** A small round badge in the platform's colour with its initial ($ for donation services, a dot for none). */
@Composable
fun PlatformIcon(platform: String) {
    val colors = LocalCuebotColors.current
    val known = platform.lowercase() in setOf("twitch", "youtube", "kick", "rumble")
    val color = when {
        platform.isEmpty() -> colors.muted
        known -> colors.platform(platform)
        else -> MaterialTheme.colorScheme.primary
    }
    Box(
        Modifier.size(22.dp).background(color, CircleShape).semantics { contentDescription = platform },
        contentAlignment = Alignment.Center,
    ) {
        val letter = when {
            platform.isEmpty() -> ""
            known -> platform.take(1).uppercase()
            else -> "$"
        }
        Text(letter, color = if (platform.lowercase() == "kick") Color.Black else Color.White, fontSize = 11.sp, fontWeight = FontWeight.Bold)
    }
}

// Quick actions

@Composable
private fun QuickActionsCard(vm: DashboardViewModel, state: DashboardViewModel.UiState, edit: () -> Unit) {
    val colors = LocalCuebotColors.current
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    var confirming by remember { mutableStateOf<DashboardViewModel.Pinned?>(null) }
    var result by remember { mutableStateOf<Pair<String, DashboardViewModel.RunResult>?>(null) }
    fun run(p: DashboardViewModel.Pinned) {
        scope.launch {
            val outcome = vm.run(p)
            result = p.id to outcome
            haptics.performHapticFeedback(if (outcome is DashboardViewModel.RunResult.Failed) HapticFeedbackType.Reject else HapticFeedbackType.Confirm)
            delay(1_500)
            if (result?.first == p.id && outcome == DashboardViewModel.RunResult.Started) result = null
        }
    }
    Card("Quick actions", Icons.Filled.Bolt, Modifier.testTag("quick-actions")) {
        if (state.pinned.isEmpty()) {
            TextButton(onClick = edit) { Text("Pin actions to run from here…") }
        } else {
            state.pinned.chunked(2).forEach { pair ->
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    pair.forEach { p ->
                        val tint = when (result?.takeIf { it.first == p.id }?.second) {
                            null -> MaterialTheme.colorScheme.primary
                            is DashboardViewModel.RunResult.Failed -> colors.bad
                            DashboardViewModel.RunResult.Started -> colors.good
                        }
                        Button(
                            onClick = { if (p.askFirst) confirming = p else run(p) },
                            colors = ButtonDefaults.buttonColors(containerColor = tint),
                            shape = RoundedCornerShape(10.dp),
                            modifier = Modifier.weight(1f).heightIn(min = 48.dp).testTag("quick-action-${p.id}"),
                        ) { Text(p.name, maxLines = 2, overflow = TextOverflow.Ellipsis, textAlign = TextAlign.Center, fontWeight = FontWeight.SemiBold) }
                    }
                    if (pair.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
        (result?.second as? DashboardViewModel.RunResult.Failed)?.let { Text(it.message, color = colors.bad, style = MaterialTheme.typography.bodySmall) }
    }
    confirming?.let { p ->
        AlertDialog(
            onDismissRequest = { confirming = null },
            title = { Text("Run ${p.name}?") },
            confirmButton = { TextButton(onClick = { confirming = null; run(p) }) { Text("Run") } },
            dismissButton = { TextButton(onClick = { confirming = null }) { Text("Cancel") } },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun QuickActionsEditor(vm: DashboardViewModel, state: DashboardViewModel.UiState, close: () -> Unit) {
    val colors = LocalCuebotColors.current
    LaunchedEffect(Unit) { vm.fetchActions() }
    ModalBottomSheet(onDismissRequest = close, containerColor = colors.ground, contentColor = colors.text) {
        Text("Quick actions", style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(horizontal = 16.dp))
        LazyColumn(Modifier.fillMaxWidth().testTag("quick-actions-editor")) {
            if (state.pinned.isNotEmpty()) {
                item { SectionHeader("Pinned") }
                items(state.pinned, key = { "p-" + it.id }) { p ->
                    ListItem(
                        headlineContent = { Text(p.name) },
                        supportingContent = { Text("Ask before running", color = colors.muted) },
                        leadingContent = {
                            Row {
                                IconButton(onClick = { vm.move(p.id, -1) }) { Icon(Icons.Filled.ArrowUpward, contentDescription = "Move up") }
                                IconButton(onClick = { vm.move(p.id, 1) }) { Icon(Icons.Filled.ArrowDownward, contentDescription = "Move down") }
                            }
                        },
                        trailingContent = { Switch(checked = p.askFirst, onCheckedChange = { vm.setAskFirst(p.id, it) }) },
                        colors = ListItemDefaults.colors(containerColor = colors.card),
                    )
                }
                item { Text("Turn on \"Ask before running\" for actions that can't be undone.", color = colors.muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp)) }
            }
            item { SectionHeader("All actions") }
            state.actionsError?.let { item { Text(it, color = colors.bad, modifier = Modifier.padding(16.dp)) } }
            items(state.available.filter { it.enabled }, key = { "a-" + it.id }) { action ->
                val pinned = state.pinned.any { it.id == action.id }
                ListItem(
                    headlineContent = { Text(action.name) },
                    supportingContent = if (action.group.isNotEmpty()) ({ Text(action.group, color = colors.muted) }) else null,
                    trailingContent = { Icon(if (pinned) Icons.Filled.PushPin else Icons.Outlined.PushPin, contentDescription = if (pinned) "Pinned" else "Not pinned", tint = MaterialTheme.colorScheme.primary) },
                    colors = ListItemDefaults.colors(containerColor = colors.card),
                    modifier = Modifier.clickable { vm.toggle(action) }.testTag("action-${action.id}"),
                )
            }
            item { Spacer(Modifier.size(32.dp)) }
        }
    }
}

@Composable
private fun SectionHeader(text: String) {
    Text(text.uppercase(), color = LocalCuebotColors.current.muted, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(start = 16.dp, top = 16.dp, bottom = 6.dp))
}

/** A dialog asking for one number. */
@Composable
fun NumberPrompt(title: String, message: String, placeholder: String, dismiss: () -> Unit, done: (Double) -> Unit) {
    var text by remember { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = dismiss,
        title = { Text(title) },
        text = {
            Column {
                Text(message)
                Spacer(Modifier.size(8.dp))
                OutlinedTextField(text, { text = it }, placeholder = { Text(placeholder) }, singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.testTag("number-field"))
            }
        },
        confirmButton = {
            TextButton(onClick = {
                text.replace(',', '.').trim().toDoubleOrNull()?.let(done)
                dismiss()
            }, modifier = Modifier.testTag("number-ok")) { Text("OK") }
        },
        dismissButton = { TextButton(onClick = dismiss) { Text("Cancel") } },
    )
}
