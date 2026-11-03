package digital.techdaddy.cuebot.companion.ui.settings

import android.Manifest
import android.content.ClipboardManager
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentPaste
import androidx.compose.material.icons.filled.Image
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.core.content.ContextCompat
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.core.CuebotEndpoint
import kotlinx.coroutines.launch

/** Pairing: scan Cuebot's QR code, pick an image of it, paste the link, or type the details. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PairingScreen(vm: SettingsViewModel, close: () -> Unit) {
    val context = LocalContext.current
    val colors = LocalCuebotColors.current
    val scope = rememberCoroutineScope()
    val hasCamera = context.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)
    var scanning by rememberSaveable { mutableStateOf(hasCamera) }
    var cameraAllowed by rememberSaveable {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    var error by rememberSaveable { mutableStateOf<String?>(null) }
    var host by rememberSaveable { mutableStateOf("") }
    var apiPort by rememberSaveable { mutableStateOf(CuebotEndpoint.DEFAULT_API_PORT.toString()) }
    var httpPort by rememberSaveable { mutableStateOf(CuebotEndpoint.DEFAULT_HTTP_PORT.toString()) }
    var token by rememberSaveable { mutableStateOf("") }
    var deckToken by rememberSaveable { mutableStateOf("") }
    var name by rememberSaveable { mutableStateOf("") }

    fun pair(link: String) {
        error = vm.pair(link)
        if (error == null) close()
    }

    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        cameraAllowed = granted
        if (!granted) {
            scanning = false
            error = "Camera access is off. Pick an image of the code, paste the link, or type the details."
        }
    }
    LaunchedEffect(scanning) { if (scanning && !cameraAllowed) cameraPermission.launch(Manifest.permission.CAMERA) }

    val pickImage = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        scope.launch {
            val codes = QrImageReader.codes(context, uri)
            when {
                codes == null -> error = "That image could not be opened."
                codes.isEmpty() -> error = "No QR code was found in that image."
                else -> pair(codes.first())
            }
        }
    }

    Dialog(onDismissRequest = close, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(
            containerColor = colors.ground,
            contentColor = colors.text,
            topBar = {
                TopAppBar(
                    title = { Text("Pair a Cuebot") },
                    navigationIcon = { IconButton(onClick = close) { Icon(Icons.Filled.Close, contentDescription = "Cancel") } },
                    actions = {
                        if (!scanning) {
                            TextButton(
                                onClick = { error = vm.pairManually(host, apiPort, httpPort, token, deckToken, name); if (error == null) close() },
                                enabled = host.isNotBlank(),
                                modifier = Modifier.testTag("pair-confirm"),
                            ) { Text("Pair") }
                        }
                    },
                    colors = TopAppBarDefaults.topAppBarColors(containerColor = colors.ground),
                )
            },
        ) { padding ->
            Column(Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(16.dp)) {
                if (scanning && cameraAllowed) {
                    QrScanner(onCode = ::pair, modifier = Modifier.fillMaxWidth().height(300.dp).clip(RoundedCornerShape(12.dp)).testTag("qr-scanner"))
                    TextButton(onClick = { scanning = false }) { Text("Enter the details instead") }
                    Hint("In Cuebot on your computer: Servers page → REST API → Pair a phone.")
                } else {
                    Field(host, { host = it }, "Computer address (e.g. 192.168.1.20)", "pair-host", KeyboardType.Uri)
                    Field(apiPort, { apiPort = it }, "API port", "pair-api-port", KeyboardType.Number)
                    Field(httpPort, { httpPort = it }, "Deck port", "pair-http-port", KeyboardType.Number)
                    Field(token, { token = it }, "API token", "pair-token", KeyboardType.Password, secret = true)
                    Field(deckToken, { deckToken = it }, "Deck token (if Cuebot shows one)", "pair-deck-token", KeyboardType.Password, secret = true)
                    Field(name, { name = it }, "Name (optional)", "pair-name", KeyboardType.Text)
                    Hint("Cuebot shows these on its Servers page, in the REST API card under \"Pair a phone\".")
                    if (hasCamera) TextButton(onClick = { error = null; scanning = true }) { Text("Scan the QR code instead") }
                }
                Spacer(Modifier.height(12.dp))
                val rowColors = ListItemDefaults.colors(containerColor = colors.card)
                ListItem(
                    headlineContent = { Text("Choose an image of the code") },
                    leadingContent = { Icon(Icons.Filled.Image, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clip(RoundedCornerShape(topStart = 12.dp, topEnd = 12.dp)).testTag("pair-image")
                        .clickable { pickImage.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                )
                ListItem(
                    headlineContent = { Text("Paste a pairing link") },
                    leadingContent = { Icon(Icons.Filled.ContentPaste, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    colors = rowColors,
                    modifier = Modifier.clip(RoundedCornerShape(bottomStart = 12.dp, bottomEnd = 12.dp)).testTag("pair-paste").clickable {
                        val clip = context.getSystemService(ClipboardManager::class.java)?.primaryClip
                        val text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString()?.trim()
                        if (text.isNullOrEmpty()) error = "There is no link on the clipboard. Copy the cuebot://pair link from Cuebot first."
                        else pair(text)
                    },
                )
                Hint("A screenshot of the code works too, for example one sent from the computer.")
                error?.let { Text(it, color = colors.bad, modifier = Modifier.padding(top = 12.dp).testTag("pair-error")) }
            }
        }
    }
}

@Composable
private fun Field(value: String, onChange: (String) -> Unit, label: String, tag: String, type: KeyboardType, secret: Boolean = false) {
    OutlinedTextField(
        value, onChange, label = { Text(label) }, singleLine = true,
        keyboardOptions = KeyboardOptions(keyboardType = type, autoCorrectEnabled = false),
        visualTransformation = if (secret) PasswordVisualTransformation() else androidx.compose.ui.text.input.VisualTransformation.None,
        modifier = Modifier.fillMaxWidth().padding(bottom = 8.dp).testTag(tag),
    )
}

@Composable
private fun Hint(text: String) {
    Text(text, color = LocalCuebotColors.current.muted, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(vertical = 6.dp))
}
