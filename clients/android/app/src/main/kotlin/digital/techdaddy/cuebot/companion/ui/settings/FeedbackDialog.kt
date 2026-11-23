package digital.techdaddy.cuebot.companion.ui.settings

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import digital.techdaddy.cuebot.companion.data.FeedbackDevice
import digital.techdaddy.cuebot.companion.data.FeedbackKind
import digital.techdaddy.cuebot.companion.data.FeedbackResult
import digital.techdaddy.cuebot.companion.data.FeedbackText
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import kotlinx.coroutines.launch

/** Send feedback: what the user writes and the listed app facts become a public GitHub issue, after they tap Send. */
@Composable
fun FeedbackDialog(vm: SettingsViewModel, close: () -> Unit) {
    val colors = LocalCuebotColors.current
    val androidContext = LocalContext.current
    val uri = LocalUriHandler.current
    val scope = rememberCoroutineScope()
    var kind by rememberSaveable { mutableStateOf(FeedbackKind.Problem) }
    var title by rememberSaveable { mutableStateOf("") }
    var details by rememberSaveable { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var sent by remember { mutableStateOf<FeedbackResult.Sent?>(null) }
    val context = remember { vm.feedbackContext(FeedbackDevice.of(androidContext)) }

    sent?.let { issue ->
        AlertDialog(
            onDismissRequest = close,
            title = { Text("Thanks for the feedback") },
            text = { Text("It was posted as issue #${issue.number}.", modifier = Modifier.testTag("feedback-sent")) },
            confirmButton = { TextButton(onClick = close) { Text("Done") } },
            dismissButton = { TextButton(onClick = { uri.openUri(issue.url) }) { Text("View issue") } },
        )
        return
    }

    AlertDialog(
        onDismissRequest = { if (!sending) close() },
        title = { Text("Send feedback") },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()).testTag("feedback-form"), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    FeedbackKind.entries.forEachIndexed { i, k ->
                        SegmentedButton(
                            selected = k == kind, onClick = { kind = k },
                            shape = SegmentedButtonDefaults.itemShape(i, FeedbackKind.entries.size),
                            label = { Text(k.label) }, modifier = Modifier.testTag("feedback-kind-${k.wire}"),
                        )
                    }
                }
                OutlinedTextField(
                    title, { title = it.take(FeedbackText.MAX_TITLE - 20) }, singleLine = true, label = { Text("Short summary") },
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                    modifier = Modifier.fillMaxWidth().testTag("feedback-title"),
                )
                OutlinedTextField(
                    details, { details = it.take(FeedbackText.MAX_DETAILS) }, minLines = 4, maxLines = 8, label = { Text(kind.prompt) },
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                    modifier = Modifier.fillMaxWidth().testTag("feedback-details"),
                )
                Text(
                    "This is posted publicly on Cuebot's GitHub issues. Don't include personal details. Email addresses, IP addresses, links and tokens are removed.",
                    style = MaterialTheme.typography.bodySmall,
                )
                Text("Also included:", style = MaterialTheme.typography.labelMedium, color = colors.muted)
                Text(
                    context.rows().joinToString("\n") { (name, value) -> "$name: $value" },
                    style = MaterialTheme.typography.bodySmall, color = colors.muted, modifier = Modifier.testTag("feedback-included"),
                )
                error?.let {
                    Text(it, color = colors.bad, style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.semantics { liveRegion = LiveRegionMode.Polite }.testTag("feedback-error"))
                }
            }
        },
        confirmButton = {
            TextButton(
                enabled = !sending && title.trim().length >= 3 && details.isNotBlank(),
                modifier = Modifier.testTag("feedback-send"),
                onClick = {
                    sending = true
                    error = null
                    scope.launch {
                        when (val result = vm.sendFeedback(kind, title, details, context)) {
                            is FeedbackResult.Sent -> sent = result
                            is FeedbackResult.Failed -> error = result.message
                        }
                        sending = false
                    }
                },
            ) { if (sending) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) else Text("Send") }
        },
        dismissButton = { TextButton(enabled = !sending, onClick = close) { Text("Cancel") } },
    )
}
