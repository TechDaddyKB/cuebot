package digital.techdaddy.cuebot.companion.ui.chat

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Celebration
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import digital.techdaddy.cuebot.companion.ui.dashboard.PlatformIcon
import digital.techdaddy.cuebot.companion.ui.dashboard.relativeTime
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.FeedItem
import digital.techdaddy.cuebot.core.feedKindLabel

/** Width of the events card beside chat on wide screens. */
internal val EventsPanelWidth = 320.dp

/** The events card beside chat (wide screens): every recent event, newest first. */
@Composable
fun EventsPanel(events: List<FeedItem>, modifier: Modifier = Modifier) {
    val colors = LocalCuebotColors.current
    Column(
        modifier.width(EventsPanelWidth).fillMaxHeight().padding(8.dp)
            .background(colors.card, RoundedCornerShape(16.dp)).border(0.5.dp, colors.line, RoundedCornerShape(16.dp))
            .padding(12.dp).testTag("events-card"),
        verticalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Header(events.size)
        EventList(events, Modifier.weight(1f))
    }
}

/** The events card above chat (narrow screens): one line with the newest event, opened to a short list with a tap. */
@Composable
fun EventsStrip(events: List<FeedItem>, expanded: Boolean, onToggle: () -> Unit) {
    val colors = LocalCuebotColors.current
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)
            .background(colors.card, RoundedCornerShape(12.dp)).border(0.5.dp, colors.line, RoundedCornerShape(12.dp))
            .testTag("events-card"),
    ) {
        Row(
            Modifier.fillMaxWidth().clickable(onClickLabel = if (expanded) "Collapse events" else "Expand events", onClick = onToggle)
                .padding(horizontal = 12.dp, vertical = 8.dp)
                .semantics { stateDescription = if (expanded) "Expanded" else "Collapsed" }
                .testTag("events-toggle"),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.Filled.Celebration, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Text("Events", style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold)
            Spacer(Modifier.width(8.dp))
            val newest = events.firstOrNull()
            Text(
                if (expanded) "" else newest?.let(::headline) ?: "None yet",
                color = colors.muted, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            Icon(if (expanded) Icons.Filled.ExpandLess else Icons.Filled.ExpandMore, contentDescription = null, tint = colors.muted)
        }
        if (expanded) EventList(events, Modifier.heightIn(max = 220.dp).padding(horizontal = 12.dp).padding(bottom = 8.dp))
    }
}

@Composable
private fun Header(count: Int) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Filled.Celebration, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(20.dp))
        Spacer(Modifier.width(8.dp))
        Text("Events", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
        if (count > 0) Text("$count", color = LocalCuebotColors.current.muted, style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
private fun EventList(events: List<FeedItem>, modifier: Modifier) {
    val colors = LocalCuebotColors.current
    if (events.isEmpty()) {
        Text("No events yet. Follows, subs, tips and raids from every platform show up here.", color = colors.muted,
            style = MaterialTheme.typography.bodySmall, modifier = modifier.testTag("events-empty"))
        return
    }
    LazyColumn(modifier.fillMaxWidth().testTag("events-list")) {
        itemsIndexed(events) { i, item ->
            EventRow(item, Modifier.testTag("event-$i"))
            HorizontalDivider(color = colors.line)
        }
    }
}

/** What happened: Cuebot's title ("ann cheered 500 bits"), else who and what. */
private fun headline(item: FeedItem) = item.title.ifEmpty { listOf(item.user, feedKindLabel(item.kind).lowercase()).filter { it.isNotEmpty() }.joinToString(" ") }

@Composable
private fun EventRow(item: FeedItem, modifier: Modifier) {
    val colors = LocalCuebotColors.current
    Row(modifier.fillMaxWidth().padding(vertical = 6.dp).semantics(mergeDescendants = true) {}, verticalAlignment = Alignment.Top) {
        PlatformIcon(item.platform)
        Spacer(Modifier.width(8.dp))
        Column(Modifier.weight(1f)) {
            Text(headline(item), style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val about = listOf(item.platform, feedKindLabel(item.kind), if (item.isTest) "test" else "").filter { it.isNotEmpty() }.joinToString(" · ")
            if (about.isNotEmpty()) Text(about, color = colors.muted, style = MaterialTheme.typography.labelSmall)
        }
        Spacer(Modifier.width(4.dp))
        Column(horizontalAlignment = Alignment.End) {
            if (item.amount.isNotEmpty()) Text(item.amount, style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.SemiBold)
            relativeTime(item.time)?.let { Text(it, color = colors.muted, style = MaterialTheme.typography.labelSmall) }
        }
    }
}
