package digital.techdaddy.cuebot.companion.ui.settings

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import digital.techdaddy.cuebot.companion.BuildConfig
import digital.techdaddy.cuebot.companion.data.ConnectionStatus
import digital.techdaddy.cuebot.companion.data.PairedCuebot
import digital.techdaddy.cuebot.companion.ui.theme.Appearance
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(vm: SettingsViewModel) {
    val colors = LocalCuebotColors.current
    val cuebots by vm.cuebots.collectAsStateWithLifecycle()
    val status by vm.status.collectAsStateWithLifecycle()
    val settings by vm.settings.collectAsStateWithLifecycle()
    var pairing by rememberSaveable { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<PairedCuebot?>(null) }
    val uri = LocalUriHandler.current
    val rowColors = ListItemDefaults.colors(containerColor = colors.card)

    Column(Modifier.fillMaxSize()) {
        TopAppBar(title = { Text("Settings") }, colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground))
        LazyColumn(Modifier.fillMaxSize().testTag("settings")) {
            item { Header("Paired Cuebots") }
            items(cuebots.all, key = { it.id }) { entry ->
                var menu by remember { mutableStateOf(false) }
                ListItem(
                    headlineContent = { Text(entry.name) },
                    supportingContent = { Text("${entry.host}:${entry.apiPort}", fontFamily = FontFamily.Monospace, color = colors.muted) },
                    leadingContent = {
                        Box(Modifier.padding(start = 4.dp)) {
                            if (entry.id == cuebots.activeId) Icon(Icons.Filled.Check, contentDescription = "In use", tint = MaterialTheme.colorScheme.primary)
                            else Icon(Icons.Filled.Check, contentDescription = null, tint = colors.card)
                        }
                    },
                    trailingContent = {
                        Box {
                            IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, contentDescription = "More for ${entry.name}") }
                            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                                DropdownMenuItem(text = { Text("Rename") }, onClick = { menu = false; renaming = entry })
                                DropdownMenuItem(text = { Text("Remove", color = colors.bad) }, onClick = { menu = false; vm.remove(entry.id) })
                            }
                        }
                    },
                    colors = rowColors,
                    modifier = Modifier.clickable { vm.activate(entry.id) }.testTag("cuebot-${entry.host}"),
                )
                HorizontalDivider(color = colors.line)
            }
            item {
                ListItem(
                    headlineContent = { Text("Pair a Cuebot", color = MaterialTheme.colorScheme.primary) },
                    leadingContent = { Icon(Icons.Filled.QrCodeScanner, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clickable { pairing = true }.testTag("pair-button"),
                )
            }
            item { Footer("Tap a Cuebot to use it. Use ⋮ to rename or remove one.") }

            item { Header("Appearance") }
            item {
                val current = vm.appearance(settings)
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(horizontal = 16.dp).testTag("appearance-picker")) {
                    Appearance.entries.forEachIndexed { i, a ->
                        SegmentedButton(
                            selected = a == current, onClick = { vm.setAppearance(a) },
                            shape = SegmentedButtonDefaults.itemShape(i, Appearance.entries.size),
                            label = { Text(a.label) },
                            modifier = Modifier.testTag("appearance-${a.key}"),
                        )
                    }
                }
            }

            item { Header("About") }
            item { ListItem(headlineContent = { Text("App") }, trailingContent = { Text(BuildConfig.VERSION_NAME) }, colors = rowColors) }
            (status as? ConnectionStatus.Online)?.status?.app?.let { app ->
                item { ListItem(headlineContent = { Text("Cuebot") }, trailingContent = { Text(app.version) }, colors = rowColors) }
                item { ListItem(headlineContent = { Text("API") }, trailingContent = { Text(app.apiVersion) }, colors = rowColors) }
            }
            item {
                ListItem(
                    headlineContent = { Text("Troubleshooting", color = MaterialTheme.colorScheme.primary) },
                    trailingContent = { Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clickable { uri.openUri("https://techdaddykb.github.io/cuebot/guides/troubleshooting/") },
                )
            }
        }
    }

    if (pairing) PairingScreen(vm, close = { pairing = false })
    renaming?.let { entry ->
        var name by remember(entry.id) { mutableStateOf(entry.name) }
        AlertDialog(
            onDismissRequest = { renaming = null },
            title = { Text("Rename") },
            text = { OutlinedTextField(name, { name = it }, singleLine = true, label = { Text("Name") }) },
            confirmButton = { TextButton(onClick = { vm.rename(entry.id, name); renaming = null }) { Text("Save") } },
            dismissButton = { TextButton(onClick = { renaming = null }) { Text("Cancel") } },
        )
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
