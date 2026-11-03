package digital.techdaddy.cuebot.companion.ui.settings

import android.content.Context
import android.net.Uri
import androidx.annotation.OptIn
import androidx.camera.core.CameraSelector
import androidx.camera.core.ExperimentalGetImage
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.lifecycle.compose.LocalLifecycleOwner
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.suspendCancellableCoroutine
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean
import kotlin.coroutines.resume

private fun qrScanner(): BarcodeScanner =
    BarcodeScanning.getClient(BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build())

/** The camera, reporting the first Cuebot pairing code it sees (ML Kit's bundled model: works offline). */
@Composable
fun QrScanner(onCode: (String) -> Unit, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current
    val previewView = remember { PreviewView(context).apply { scaleType = PreviewView.ScaleType.FILL_CENTER } }
    DisposableEffect(lifecycle) {
        val scanner = qrScanner()
        val executor = Executors.newSingleThreadExecutor()
        val found = AtomicBoolean(false)
        val providerFuture = ProcessCameraProvider.getInstance(context)
        var provider: ProcessCameraProvider? = null
        providerFuture.addListener({
            val p = providerFuture.get().also { provider = it }
            val preview = Preview.Builder().build().also { it.surfaceProvider = previewView.surfaceProvider }
            val analysis = ImageAnalysis.Builder().setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST).build()
            analysis.setAnalyzer(executor, Analyzer(scanner) { code ->
                if (found.compareAndSet(false, true)) ContextCompat.getMainExecutor(context).execute { onCode(code) }
            })
            runCatching {
                p.unbindAll()
                p.bindToLifecycle(lifecycle, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
            }.recoverCatching {
                p.bindToLifecycle(lifecycle, CameraSelector.DEFAULT_FRONT_CAMERA, preview, analysis)
            }
        }, ContextCompat.getMainExecutor(context))
        onDispose {
            provider?.unbindAll()
            scanner.close()
            executor.shutdown()
        }
    }
    AndroidView({ previewView }, modifier)
}

private class Analyzer(private val scanner: BarcodeScanner, private val found: (String) -> Unit) : ImageAnalysis.Analyzer {
    @OptIn(ExperimentalGetImage::class)
    override fun analyze(proxy: androidx.camera.core.ImageProxy) {
        val image = proxy.image ?: return proxy.close()
        scanner.process(InputImage.fromMediaImage(image, proxy.imageInfo.rotationDegrees))
            .addOnSuccessListener { codes ->
                codes.mapNotNull { it.rawValue }.firstOrNull { it.trim().lowercase().startsWith("cuebot://") }?.let(found)
            }
            .addOnCompleteListener { proxy.close() }
    }
}

/** QR codes in a picked image (a photo or screenshot of Cuebot's pairing code). Null when it cannot be read. */
object QrImageReader {
    suspend fun codes(context: Context, uri: Uri): List<String>? {
        val image = runCatching { InputImage.fromFilePath(context, uri) }.getOrNull() ?: return null
        val scanner = qrScanner()
        return try {
            suspendCancellableCoroutine { cont ->
                scanner.process(image)
                    .addOnSuccessListener { codes -> cont.resume(codes.mapNotNull { it.rawValue }) }
                    .addOnFailureListener { cont.resume(null) }
            }
        } finally {
            scanner.close()
        }
    }
}
