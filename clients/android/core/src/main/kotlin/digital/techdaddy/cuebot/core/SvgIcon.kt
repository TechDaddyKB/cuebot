package digital.techdaddy.cuebot.core

import org.xml.sax.Attributes
import org.xml.sax.InputSource
import org.xml.sax.helpers.DefaultHandler
import java.io.StringReader
import javax.xml.parsers.SAXParserFactory
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.ceil
import kotlin.math.cos
import kotlin.math.sin
import kotlin.math.sqrt
import kotlin.math.tan

/**
 * A small SVG reader for icons such as the Iconify sets deck buttons name (`mdi:microphone`): `<path>`, `<circle>`,
 * `<ellipse>`, `<rect>`, `<line>`, `<polyline>`, `<polygon>` and `<g>` (whose fill and stroke settings are inherited).
 * Every shape becomes absolute move/line/curve segments in viewBox units, so the app can draw it in one color at any
 * size. Colors are ignored on purpose: the deck says which color the icon has. Gradients, masks and text are skipped.
 * The same reader as the iOS app's CuebotKit.
 */
data class SvgIcon(val minX: Double, val minY: Double, val width: Double, val height: Double, val shapes: List<Shape>) {
    data class Point(val x: Double, val y: Double)

    sealed interface Segment {
        data class Move(val p: Point) : Segment
        data class Line(val p: Point) : Segment
        data class Cubic(val c1: Point, val c2: Point, val p: Point) : Segment
        data class Quad(val c: Point, val p: Point) : Segment
        data object Close : Segment
    }

    data class Shape(
        val segments: List<Segment>, val fill: Boolean, val evenOdd: Boolean, val strokeWidth: Double?,
        val roundCaps: Boolean, val roundJoins: Boolean,
    )

    companion object {
        fun parse(svg: String): SvgIcon? = runCatching {
            val reader = Reader()
            val factory = SAXParserFactory.newInstance().apply { isNamespaceAware = false }
            factory.newSAXParser().parse(InputSource(StringReader(svg)), reader)
            val box = reader.viewBox ?: return null
            if (box[2] <= 0 || box[3] <= 0 || reader.shapes.isEmpty()) return null
            SvgIcon(box[0], box[1], box[2], box[3], reader.shapes)
        }.getOrNull()
    }

    private class Reader : DefaultHandler() {
        data class Style(
            val fill: String? = null, val stroke: String? = null, val strokeWidth: Double? = null,
            val fillRule: String? = null, val lineCap: String? = null, val lineJoin: String? = null,
        )

        var viewBox: DoubleArray? = null
        val shapes = mutableListOf<Shape>()
        private val styles = ArrayDeque(listOf(Style()))
        private var unsupportedDepth = 0

        override fun startElement(uri: String?, localName: String?, qName: String, a: Attributes) {
            val name = qName.substringAfter(':')
            if (unsupportedDepth > 0 || name in UNSUPPORTED) { unsupportedDepth++; return }
            fun attr(key: String): String? = a.getValue(key)
            val parent = styles.last()
            val style = Style(
                attr("fill") ?: parent.fill, attr("stroke") ?: parent.stroke, attr("stroke-width")?.toDoubleOrNull() ?: parent.strokeWidth,
                attr("fill-rule") ?: parent.fillRule, attr("stroke-linecap") ?: parent.lineCap, attr("stroke-linejoin") ?: parent.lineJoin,
            )
            styles.addLast(style)
            fun n(key: String) = attr(key)?.toDoubleOrNull() ?: 0.0
            val segments: List<Segment>? = when (name) {
                "svg" -> {
                    val box = attr("viewBox")?.let(PathData::numbers)
                    viewBox = if (box != null && box.size == 4) box.toDoubleArray()
                    else attr("width")?.toDoubleOrNull()?.let { w -> attr("height")?.toDoubleOrNull()?.let { h -> doubleArrayOf(0.0, 0.0, w, h) } }
                    null
                }
                "path" -> PathData.parse(attr("d") ?: "")
                "circle" -> ellipse(n("cx"), n("cy"), n("r"), n("r"))
                "ellipse" -> ellipse(n("cx"), n("cy"), n("rx"), n("ry"))
                "rect" -> rect(n("x"), n("y"), n("width"), n("height"), attr("rx")?.toDoubleOrNull() ?: attr("ry")?.toDoubleOrNull() ?: 0.0)
                "line" -> listOf(Segment.Move(Point(n("x1"), n("y1"))), Segment.Line(Point(n("x2"), n("y2"))))
                "polyline", "polygon" -> {
                    val v = PathData.numbers(attr("points") ?: "")
                    val pts = (0 until v.size - 1 step 2).mapIndexed { i, j ->
                        if (i == 0) Segment.Move(Point(v[j], v[j + 1])) else Segment.Line(Point(v[j], v[j + 1]))
                    }
                    if (name == "polygon" && pts.isNotEmpty()) pts + Segment.Close else pts
                }
                else -> null
            }
            if (segments.isNullOrEmpty()) return
            val fill = (style.fill ?: "currentColor") != "none"   // SVG fills black when nothing is said
            val stroke = style.stroke?.let { it != "none" } ?: false
            if (!fill && !stroke) return
            shapes += Shape(segments, fill, style.fillRule == "evenodd", if (stroke) style.strokeWidth ?: 1.0 else null,
                style.lineCap == "round", style.lineJoin == "round")
        }

        override fun endElement(uri: String?, localName: String?, qName: String) {
            if (unsupportedDepth > 0) { unsupportedDepth--; return }
            if (styles.size > 1) styles.removeLast()
        }

        private fun ellipse(cx: Double, cy: Double, rx: Double, ry: Double): List<Segment> {
            if (rx <= 0 || ry <= 0) return emptyList()
            val k = 0.5522847498
            return listOf(
                Segment.Move(Point(cx + rx, cy)),
                Segment.Cubic(Point(cx + rx, cy + ry * k), Point(cx + rx * k, cy + ry), Point(cx, cy + ry)),
                Segment.Cubic(Point(cx - rx * k, cy + ry), Point(cx - rx, cy + ry * k), Point(cx - rx, cy)),
                Segment.Cubic(Point(cx - rx, cy - ry * k), Point(cx - rx * k, cy - ry), Point(cx, cy - ry)),
                Segment.Cubic(Point(cx + rx * k, cy - ry), Point(cx + rx, cy - ry * k), Point(cx + rx, cy)),
                Segment.Close,
            )
        }

        private fun rect(x: Double, y: Double, w: Double, h: Double, rx: Double): List<Segment> {
            if (w <= 0 || h <= 0) return emptyList()
            val r = minOf(rx, w / 2, h / 2)
            if (r <= 0) return listOf(Segment.Move(Point(x, y)), Segment.Line(Point(x + w, y)), Segment.Line(Point(x + w, y + h)),
                Segment.Line(Point(x, y + h)), Segment.Close)
            val k = r * 0.5522847498
            return listOf(
                Segment.Move(Point(x + r, y)), Segment.Line(Point(x + w - r, y)),
                Segment.Cubic(Point(x + w - r + k, y), Point(x + w, y + r - k), Point(x + w, y + r)), Segment.Line(Point(x + w, y + h - r)),
                Segment.Cubic(Point(x + w, y + h - r + k), Point(x + w - r + k, y + h), Point(x + w - r, y + h)), Segment.Line(Point(x + r, y + h)),
                Segment.Cubic(Point(x + r - k, y + h), Point(x, y + h - r + k), Point(x, y + h - r)), Segment.Line(Point(x, y + r)),
                Segment.Cubic(Point(x, y + r - k), Point(x + r - k, y), Point(x + r, y)), Segment.Close,
            )
        }

        companion object {
            val UNSUPPORTED = setOf("defs", "mask", "clipPath", "linearGradient", "radialGradient", "text", "style")
        }
    }
}

/** SVG path data (the `d` attribute): every command, relative or absolute, made absolute; arcs become cubic curves. */
internal object PathData {
    fun numbers(text: String): List<Double> {
        val s = Scanner(text)
        val out = mutableListOf<Double>()
        while (true) out += s.number() ?: break
        return out
    }

    fun parse(d: String): List<SvgIcon.Segment> {
        val s = Scanner(d)
        val out = mutableListOf<SvgIcon.Segment>()
        var current = SvgIcon.Point(0.0, 0.0)
        var start = current
        var lastControl: SvgIcon.Point? = null
        var lastQuadControl: SvgIcon.Point? = null
        var command = ' '
        while (true) {
            s.skipSeparators()
            val c = s.peek() ?: break
            if (c.isLetter()) { command = c; s.advance() } else if (command == ' ') return out
            val relative = command.isLowerCase()
            fun pt(x: Double, y: Double) = if (relative) SvgIcon.Point(current.x + x, current.y + y) else SvgIcon.Point(x, y)
            var cubicControl: SvgIcon.Point? = null
            var quadControl: SvgIcon.Point? = null
            when (command.lowercaseChar()) {
                'm' -> {
                    val x = s.number() ?: return out; val y = s.number() ?: return out
                    current = pt(x, y); start = current; out += SvgIcon.Segment.Move(current)
                    command = if (relative) 'l' else 'L'   // further pairs are line-tos
                }
                'l' -> { val x = s.number() ?: return out; val y = s.number() ?: return out; current = pt(x, y); out += SvgIcon.Segment.Line(current) }
                'h' -> { val x = s.number() ?: return out; current = SvgIcon.Point(if (relative) current.x + x else x, current.y); out += SvgIcon.Segment.Line(current) }
                'v' -> { val y = s.number() ?: return out; current = SvgIcon.Point(current.x, if (relative) current.y + y else y); out += SvgIcon.Segment.Line(current) }
                'c' -> {
                    val v = s.numbers(6) ?: return out
                    val c1 = pt(v[0], v[1]); val c2 = pt(v[2], v[3]); val end = pt(v[4], v[5])
                    out += SvgIcon.Segment.Cubic(c1, c2, end); cubicControl = c2; current = end
                }
                's' -> {
                    val v = s.numbers(4) ?: return out
                    val c1 = lastControl?.let { SvgIcon.Point(2 * current.x - it.x, 2 * current.y - it.y) } ?: current
                    val c2 = pt(v[0], v[1]); val end = pt(v[2], v[3])
                    out += SvgIcon.Segment.Cubic(c1, c2, end); cubicControl = c2; current = end
                }
                'q' -> {
                    val v = s.numbers(4) ?: return out
                    val ctl = pt(v[0], v[1]); val end = pt(v[2], v[3])
                    out += SvgIcon.Segment.Quad(ctl, end); quadControl = ctl; current = end
                }
                't' -> {
                    val x = s.number() ?: return out; val y = s.number() ?: return out
                    val ctl = lastQuadControl?.let { SvgIcon.Point(2 * current.x - it.x, 2 * current.y - it.y) } ?: current
                    val end = pt(x, y)
                    out += SvgIcon.Segment.Quad(ctl, end); quadControl = ctl; current = end
                }
                'a' -> {
                    val rx = s.number() ?: return out; val ry = s.number() ?: return out; val rot = s.number() ?: return out
                    val large = s.flag() ?: return out; val sweep = s.flag() ?: return out
                    val x = s.number() ?: return out; val y = s.number() ?: return out
                    val end = pt(x, y)
                    out += arc(current, end, rx, ry, rot, large, sweep); current = end
                }
                'z' -> { out += SvgIcon.Segment.Close; current = start }
                else -> return out
            }
            lastControl = cubicControl
            lastQuadControl = quadControl
        }
        return out
    }

    /** An elliptical arc (SVG endpoint form) as cubic Béziers, per the SVG spec's conversion to center form. */
    fun arc(p0: SvgIcon.Point, p1: SvgIcon.Point, rxIn: Double, ryIn: Double, rotation: Double, large: Boolean, sweep: Boolean): List<SvgIcon.Segment> {
        if (p0 == p1) return emptyList()
        var rx = abs(rxIn); var ry = abs(ryIn)
        if (rx == 0.0 || ry == 0.0) return listOf(SvgIcon.Segment.Line(p1))
        val phi = Math.toRadians(rotation); val cosPhi = cos(phi); val sinPhi = sin(phi)
        val dx = (p0.x - p1.x) / 2; val dy = (p0.y - p1.y) / 2
        val x1p = cosPhi * dx + sinPhi * dy; val y1p = -sinPhi * dx + cosPhi * dy
        val lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry)
        if (lambda > 1) { rx *= sqrt(lambda); ry *= sqrt(lambda) }
        val num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p
        val den = rx * rx * y1p * y1p + ry * ry * x1p * x1p
        var coef = sqrt(maxOf(0.0, num) / den)
        if (large == sweep) coef = -coef
        val cxp = coef * rx * y1p / ry; val cyp = -coef * ry * x1p / rx
        val cx = cosPhi * cxp - sinPhi * cyp + (p0.x + p1.x) / 2
        val cy = sinPhi * cxp + cosPhi * cyp + (p0.y + p1.y) / 2
        fun angle(ux: Double, uy: Double, vx: Double, vy: Double) = atan2(ux * vy - uy * vx, ux * vx + uy * vy)
        val theta1 = angle(1.0, 0.0, (x1p - cxp) / rx, (y1p - cyp) / ry)
        var delta = angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry)
        if (!sweep && delta > 0) delta -= 2 * Math.PI else if (sweep && delta < 0) delta += 2 * Math.PI
        val pieces = maxOf(1, ceil(abs(delta) / (Math.PI / 2)).toInt())
        val step = delta / pieces
        val t = 4.0 / 3.0 * tan(step / 4)
        fun point(a: Double): SvgIcon.Point {
            val ex = rx * cos(a); val ey = ry * sin(a)
            return SvgIcon.Point(cx + cosPhi * ex - sinPhi * ey, cy + sinPhi * ex + cosPhi * ey)
        }
        fun derivative(a: Double): SvgIcon.Point {
            val ex = -rx * sin(a); val ey = ry * cos(a)
            return SvgIcon.Point(cosPhi * ex - sinPhi * ey, sinPhi * ex + cosPhi * ey)
        }
        val segments = mutableListOf<SvgIcon.Segment>()
        var a = theta1
        for (i in 0 until pieces) {
            val b = a + step
            val st = point(a); val d1 = derivative(a); val d2 = derivative(b)
            val e = if (i == pieces - 1) p1 else point(b)
            segments += SvgIcon.Segment.Cubic(SvgIcon.Point(st.x + t * d1.x, st.y + t * d1.y), SvgIcon.Point(e.x - t * d2.x, e.y - t * d2.y), e)
            a = b
        }
        return segments
    }

    /** Numbers and flags as SVG writes them: "1-2" is two numbers, ".5.5" two, and arc flags may touch ("011 1"). */
    class Scanner(private val text: String) {
        private var i = 0

        fun peek(): Char? = text.getOrNull(i)

        /** [count] numbers in a row, or null when the data ends early. */
        fun numbers(count: Int): DoubleArray? {
            val out = DoubleArray(count)
            for (k in 0 until count) out[k] = number() ?: return null
            return out
        }
        fun advance() { i++ }

        fun skipSeparators() {
            while (i < text.length && (text[i] == ' ' || text[i] == ',' || text[i] == '\t' || text[i] == '\n' || text[i] == '\r')) i++
        }

        fun flag(): Boolean? {
            skipSeparators()
            val c = peek() ?: return null
            if (c != '0' && c != '1') return null
            advance()
            return c == '1'
        }

        fun number(): Double? {
            skipSeparators()
            val begin = i
            if (peek() == '+' || peek() == '-') advance()
            var digits = false; var dot = false
            while (true) {
                val c = peek() ?: break
                if (c.isDigit()) { digits = true; advance() } else if (c == '.' && !dot) { dot = true; advance() } else break
            }
            if (digits && (peek() == 'e' || peek() == 'E')) {
                val mark = i
                advance()
                if (peek() == '+' || peek() == '-') advance()
                var expDigits = false
                while (peek()?.isDigit() == true) { expDigits = true; advance() }
                if (!expDigits) i = mark
            }
            if (!digits) { i = begin; return null }
            return text.substring(begin, i).toDoubleOrNull()
        }
    }
}

/** Iconify icon names as decks use them ("mdi:microphone") and where the SVG comes from. */
object Iconify {
    /** `https://api.iconify.design/<prefix>/<name>.svg`, or null when [icon] is not a "prefix:name" Iconify name. */
    fun url(icon: String): String? {
        val parts = icon.trim().split(':', limit = 2)
        if (parts.size != 2 || parts.any { p -> p.isEmpty() || !p.all { it.isLetterOrDigit() || it == '-' } }) return null
        return "https://api.iconify.design/${parts[0].lowercase()}/${parts[1].lowercase()}.svg"
    }
}
