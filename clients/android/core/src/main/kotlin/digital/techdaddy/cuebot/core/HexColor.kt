package digital.techdaddy.cuebot.core

import kotlin.math.pow

/** A color from Cuebot's CSS-style strings ("#fff", "#16161c", "#16161cff", "rgb(1,2,3)"), as 0..1 components. */
data class HexColor(val red: Double, val green: Double, val blue: Double, val alpha: Double = 1.0) {
    /** ARGB as an Int, for Compose's Color(Int). */
    val argb: Int get() = ((alpha * 255).toInt() shl 24) or ((red * 255).toInt() shl 16) or ((green * 255).toInt() shl 8) or (blue * 255).toInt()

    /** True for colors where white text reads better than black (relative luminance below 0.4). */
    val isDark: Boolean get() {
        fun linear(c: Double) = if (c <= 0.04045) c / 12.92 else ((c + 0.055) / 1.055).pow(2.4)
        return 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue) < 0.4
    }

    companion object {
        fun parse(text: String): HexColor? {
            val s = text.trim().lowercase()
            if (s.startsWith("rgb")) {
                val inside = s.substringAfter('(', "").substringBefore(')')
                val n = inside.split(',').mapNotNull { it.trim().toDoubleOrNull() }
                if (n.size < 3) return null
                return HexColor(n[0] / 255, n[1] / 255, n[2] / 255, if (n.size > 3) n[3] else 1.0)
            }
            if (!s.startsWith("#")) return null
            var hex = s.substring(1)
            if (hex.length == 3 || hex.length == 4) hex = hex.map { "$it$it" }.joinToString("")
            if (hex.length != 6 && hex.length != 8) return null
            val value = hex.toLongOrNull(16) ?: return null
            val hasAlpha = hex.length == 8
            val rgb = if (hasAlpha) value shr 8 else value
            return HexColor(((rgb shr 16) and 0xff) / 255.0, ((rgb shr 8) and 0xff) / 255.0, (rgb and 0xff) / 255.0,
                if (hasAlpha) (value and 0xff) / 255.0 else 1.0)
        }
    }
}
