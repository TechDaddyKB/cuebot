package digital.techdaddy.cuebot.companion.ui.deck

import androidx.compose.animation.Animatable
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Fill
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import digital.techdaddy.cuebot.companion.AppContainer
import digital.techdaddy.cuebot.companion.data.RemoteImages
import digital.techdaddy.cuebot.companion.ui.theme.LocalCuebotColors
import digital.techdaddy.cuebot.companion.ui.theme.cuebotColor
import digital.techdaddy.cuebot.core.DeckItem
import digital.techdaddy.cuebot.core.DeckView
import digital.techdaddy.cuebot.core.SvgIcon
import kotlinx.coroutines.launch
import kotlin.math.max
import kotlin.math.min

/** Where the cells go: pure geometry, so it can be checked at any screen size. Sizes are in dp. */
data class DeckLayout(val cellWidth: Float, val cellHeight: Float, val originX: Float, val originY: Float, val gap: Float) {
    fun x(column: Int) = originX + column * (cellWidth + gap)
    fun y(row: Int) = originY + row * (cellHeight + gap)

    companion object {
        /** `columns × rows` cells with [gap], as large as fit and a little wider than tall at most, centred. */
        fun of(width: Float, height: Float, rows: Int, columns: Int, gap: Float, padding: Float): DeckLayout {
            val r = max(1, rows).toFloat()
            val c = max(1, columns).toFloat()
            val w = max(0f, (width - 2 * padding - gap * (c - 1)) / c)
            val h = max(0f, (height - 2 * padding - gap * (r - 1)) / r)
            val side = min(w, h * 1.25f)
            val cellHeight = min(h, side)
            val usedWidth = side * c + gap * (c - 1)
            val usedHeight = cellHeight * r + gap * (r - 1)
            return DeckLayout(side, cellHeight, (width - usedWidth) / 2, (height - usedHeight) / 2, gap)
        }
    }
}

/** The deck's grid: `columns × rows` cells with the deck's gap. Empty cells are left blank. */
@Composable
fun DeckGrid(vm: DeckViewModel, container: AppContainer, view: DeckView, streamMode: Boolean, modifier: Modifier = Modifier) {
    BoxWithConstraints(modifier) {
        val layout = DeckLayout.of(maxWidth.value, maxHeight.value, view.rows, view.columns, view.gap.toFloat(), if (streamMode) 8f else 16f)
        view.items.filter { it.row < view.rows && it.column < view.columns }.forEach { item ->
            DeckButton(
                vm, container, item, layout.cellWidth.dp, layout.cellHeight.dp,
                Modifier.offset(layout.x(item.column).dp, layout.y(item.row).dp),
            )
        }
    }
}

@Composable
private fun DeckButton(vm: DeckViewModel, container: AppContainer, item: DeckItem, width: Dp, height: Dp, modifier: Modifier) {
    val colors = LocalCuebotColors.current
    val haptics = LocalHapticFeedback.current
    val scope = rememberCoroutineScope()
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.92f else 1f, tween(120), label = "press")
    val flash = remember { Animatable(Color.Transparent) }
    val side = min(width.value, height.value)
    val shape = RoundedCornerShape((side * 0.14f).dp)
    val titleColor = cuebotColor(item.titleColor, colors.text)
    val base = container.connection.endpoint?.httpBase

    Box(
        modifier
            .size(width, height)
            .scale(scale)
            .clip(shape)
            .background(cuebotColor(item.background, colors.raised))
            .clickable(interactionSource = interaction, indication = null) {
                scope.launch {
                    val result = vm.press(item)
                    haptics.performHapticFeedback(if (result == DeckViewModel.PressResult.Ok) HapticFeedbackType.Confirm else HapticFeedbackType.Reject)
                    if (result == DeckViewModel.PressResult.Failed) {
                        flash.animateTo(colors.bad.copy(alpha = 0.55f), tween(150))
                        flash.animateTo(Color.Transparent, tween(500, delayMillis = 300))
                    }
                }
            }
            .semantics(mergeDescendants = true) {
                contentDescription = item.accessibilityName
                if (item.value.isNotEmpty()) stateDescription = item.value
            }
            .testTag("deck-button-${item.id}"),
    ) {
        RemoteImages.url(item.backgroundImage, base)?.let { RemoteImage(container, it, ContentScale.Crop, Modifier.fillMaxSize()) }
        Column(Modifier.fillMaxSize().padding(6.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = androidx.compose.foundation.layout.Arrangement.Center) {
            val iconUrl = RemoteImages.url(item.iconUrl, base)
            if (iconUrl != null) {
                RemoteImage(container, iconUrl, ContentScale.Fit, Modifier.size((side * 0.38f).dp))
            } else if (item.icon.isNotEmpty()) {
                DeckIcon(container, item.icon, cuebotColor(item.iconColor, titleColor), (side * if (item.title.isEmpty()) 0.5f else 0.34f).dp)
            }
            if (item.title.isNotEmpty()) {
                Spacer(Modifier.height(4.dp))
                Text(
                    item.title,
                    color = titleColor,
                    fontSize = max(10f, side * 0.15f).sp,
                    lineHeight = (max(10f, side * 0.15f) * 1.1f).sp,
                    fontWeight = FontWeight.SemiBold,
                    textAlign = TextAlign.Center,
                    maxLines = 3,
                    overflow = TextOverflow.Ellipsis,
                )
            }
        }
        if (item.value.isNotEmpty()) {
            Text(
                item.value,
                color = Color.White,
                fontSize = max(9f, side * 0.12f).sp,
                fontWeight = FontWeight.Bold,
                maxLines = 1,
                modifier = Modifier.align(Alignment.TopEnd).padding(5.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.45f))
                    .padding(horizontal = 5.dp, vertical = 1.dp),
            )
        }
        Box(Modifier.fillMaxSize().background(flash.value))
    }
}

/** An Iconify icon drawn in one colour, once it has loaded (nothing until then, or when it cannot be drawn). */
@Composable
private fun DeckIcon(container: AppContainer, name: String, color: Color, size: Dp) {
    val icon by produceState(container.icons.cached(name), name) { value = container.icons.icon(name) }
    icon?.let { SvgIconView(it, color, Modifier.size(size)) }
}

@Composable
fun SvgIconView(icon: SvgIcon, color: Color, modifier: Modifier) {
    Canvas(modifier) {
        val scale = min(size.width / icon.width.toFloat(), size.height / icon.height.toFloat())
        val dx = (size.width - icon.width.toFloat() * scale) / 2 - icon.minX.toFloat() * scale
        val dy = (size.height - icon.height.toFloat() * scale) / 2 - icon.minY.toFloat() * scale
        fun p(point: SvgIcon.Point) = Offset(point.x.toFloat() * scale + dx, point.y.toFloat() * scale + dy)
        for (shape in icon.shapes) {
            val path = Path()
            for (segment in shape.segments) {
                when (segment) {
                    is SvgIcon.Segment.Move -> p(segment.p).let { path.moveTo(it.x, it.y) }
                    is SvgIcon.Segment.Line -> p(segment.p).let { path.lineTo(it.x, it.y) }
                    is SvgIcon.Segment.Cubic -> { val a = p(segment.c1); val b = p(segment.c2); val c = p(segment.p); path.cubicTo(a.x, a.y, b.x, b.y, c.x, c.y) }
                    is SvgIcon.Segment.Quad -> { val a = p(segment.c); val b = p(segment.p); path.quadraticTo(a.x, a.y, b.x, b.y) }
                    SvgIcon.Segment.Close -> path.close()
                }
            }
            if (shape.fill) {
                path.fillType = if (shape.evenOdd) PathFillType.EvenOdd else PathFillType.NonZero
                drawPath(path, color, style = Fill)
            }
            shape.strokeWidth?.let { w ->
                drawPath(path, color, style = Stroke(
                    width = (w * scale).toFloat(),
                    cap = if (shape.roundCaps) StrokeCap.Round else StrokeCap.Butt,
                    join = if (shape.roundJoins) StrokeJoin.Round else StrokeJoin.Miter,
                ))
            }
        }
    }
}

@Composable
private fun RemoteImage(container: AppContainer, url: String, scale: ContentScale, modifier: Modifier) {
    val bitmap by produceState(container.images.cached(url), url) { value = container.images.load(url) }
    bitmap?.let { Image(it.asImageBitmap(), contentDescription = null, contentScale = scale, modifier = modifier) }
}
