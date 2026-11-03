package digital.techdaddy.cuebot.companion.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import digital.techdaddy.cuebot.core.HexColor

/** Cuebot's palette (the desktop app's Styles/Cuebot.axaml): dark from the desktop app, light to match it. */
@Immutable
data class CuebotColors(
    val ground: Color, val card: Color, val raised: Color, val line: Color, val text: Color, val muted: Color,
    val good: Color, val bad: Color, val warn: Color, val accent: Color,
    val twitch: Color, val youtube: Color, val kick: Color, val rumble: Color,
) {
    /** A platform's brand colour, or the muted grey for others. */
    fun platform(name: String): Color = when (name.lowercase()) {
        "twitch" -> twitch
        "youtube" -> youtube
        "kick" -> kick
        "rumble" -> rumble
        else -> muted
    }
}

val DarkCuebot = CuebotColors(
    ground = Color(0xFF0B1120), card = Color(0xFF111A2B), raised = Color(0xFF16213A), line = Color(0xFF22304A),
    text = Color(0xFFE6ECF5), muted = Color(0xFF9AA8BF), good = Color(0xFF00B361), bad = Color(0xFFF04B5A),
    warn = Color(0xFFF5A524), accent = Color(0xFF3DB4EE),
    twitch = Color(0xFF9146FF), youtube = Color(0xFFFF0033), kick = Color(0xFF53FC18), rumble = Color(0xFF85C742),
)

val LightCuebot = CuebotColors(
    ground = Color(0xFFF2F2F7), card = Color(0xFFFFFFFF), raised = Color(0xFFE5E5EA), line = Color(0xFFC6C6C8),
    text = Color(0xFF000000), muted = Color(0xFF6C6C70), good = Color(0xFF008A4B), bad = Color(0xFFD7263D),
    warn = Color(0xFFB86E00), accent = Color(0xFF0079BC),
    twitch = Color(0xFF9146FF), youtube = Color(0xFFFF0033), kick = Color(0xFF2FB500), rumble = Color(0xFF5E9E1E),
)

val LocalCuebotColors = staticCompositionLocalOf { DarkCuebot }

/** The Appearance setting: follow the system, or always dark or light. */
enum class Appearance(val label: String) {
    System("System"), Dark("Dark"), Light("Light");

    val key: String get() = name.lowercase()

    companion object {
        const val STORAGE_KEY = "appearance"
        fun of(key: String?) = entries.firstOrNull { it.key == key } ?: System
    }
}

@Composable
fun Appearance.isDark(): Boolean = when (this) {
    Appearance.System -> isSystemInDarkTheme()
    Appearance.Dark -> true
    Appearance.Light -> false
}

/** Material 3 with Cuebot's colours (no dynamic colour: the app looks like Cuebot, not the wallpaper). */
@Composable
fun CuebotTheme(dark: Boolean, content: @Composable () -> Unit) {
    val c = if (dark) DarkCuebot else LightCuebot
    val scheme = if (dark) {
        darkColorScheme(
            primary = c.accent, onPrimary = Color(0xFF00121D), primaryContainer = c.raised, onPrimaryContainer = c.text,
            secondary = c.accent, onSecondary = Color(0xFF00121D), secondaryContainer = c.raised, onSecondaryContainer = c.text,
            tertiary = c.good, background = c.ground, onBackground = c.text, surface = c.ground, onSurface = c.text,
            surfaceVariant = c.raised, onSurfaceVariant = c.muted, surfaceContainerLowest = c.ground, surfaceContainerLow = c.card,
            surfaceContainer = c.card, surfaceContainerHigh = c.raised, surfaceContainerHighest = c.raised,
            outline = c.line, outlineVariant = c.line, error = c.bad, onError = Color.White, inverseSurface = c.text,
            inverseOnSurface = c.ground, inversePrimary = LightCuebot.accent,
        )
    } else {
        lightColorScheme(
            primary = c.accent, onPrimary = Color.White, primaryContainer = Color(0xFFD6ECF8), onPrimaryContainer = Color(0xFF00324D),
            secondary = c.accent, onSecondary = Color.White, secondaryContainer = Color(0xFFD6ECF8), onSecondaryContainer = Color(0xFF00324D),
            tertiary = c.good, background = c.ground, onBackground = c.text, surface = c.ground, onSurface = c.text,
            surfaceVariant = c.raised, onSurfaceVariant = c.muted, surfaceContainerLowest = c.card, surfaceContainerLow = c.card,
            surfaceContainer = c.card, surfaceContainerHigh = c.raised, surfaceContainerHighest = c.raised,
            outline = c.line, outlineVariant = c.line, error = c.bad, onError = Color.White, inverseSurface = Color(0xFF1C1C1E),
            inverseOnSurface = Color.White, inversePrimary = DarkCuebot.accent,
        )
    }
    CompositionLocalProvider(LocalCuebotColors provides c) {
        MaterialTheme(colorScheme = scheme, content = content)
    }
}

/** A Cuebot colour string ("#16161c", "rgb(…)"), or [fallback] when it is empty or not understood. */
fun cuebotColor(text: String, fallback: Color): Color = HexColor.parse(text)?.let { Color(it.argb) } ?: fallback
