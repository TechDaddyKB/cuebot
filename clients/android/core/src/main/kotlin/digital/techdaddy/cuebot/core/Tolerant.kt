package digital.techdaddy.cuebot.core

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.longOrNull

/**
 * Reading Cuebot's JSON tolerantly: a missing field, null, or a value of another type falls back to a default instead
 * of failing the whole response, because Cuebot may add, drop or change fields between versions (the same rule as the
 * iOS app's CuebotKit).
 */
internal val CuebotJson = Json { ignoreUnknownKeys = true; isLenient = true }

internal fun parseJson(text: String): JsonElement? = runCatching { CuebotJson.parseToJsonElement(text) }.getOrNull()

internal fun JsonElement?.obj(): JsonObject? = this as? JsonObject
internal fun JsonElement?.arr(): JsonArray? = this as? JsonArray

internal fun JsonObject.str(key: String, fallback: String = ""): String =
    (this[key] as? JsonPrimitive)?.takeIf { it.isString }?.content ?: fallback

internal fun JsonObject.num(key: String): Double? =
    (this[key] as? JsonPrimitive)?.takeIf { !it.isString }?.doubleOrNull

internal fun JsonObject.dbl(key: String, fallback: Double = 0.0): Double = num(key) ?: fallback

internal fun JsonObject.int(key: String, fallback: Int = 0): Int =
    (this[key] as? JsonPrimitive)?.takeIf { !it.isString }?.let { it.longOrNull?.toInt() ?: it.doubleOrNull?.toInt() } ?: fallback

internal fun JsonObject.bool(key: String, fallback: Boolean = false): Boolean =
    (this[key] as? JsonPrimitive)?.takeIf { !it.isString }?.booleanOrNull ?: fallback

internal fun <T> JsonObject.list(key: String, item: (JsonObject) -> T): List<T> =
    (this[key] as? JsonArray)?.mapNotNull { (it as? JsonObject)?.let(item) } ?: emptyList()

/** Text for a JSON value as a person would read it: strings as they are, whole numbers without ".0". */
fun JsonElement?.displayText(): String? = when (this) {
    null, JsonNull -> null
    is JsonPrimitive -> when {
        isString -> content
        doubleOrNull != null -> doubleOrNull!!.let { if (it % 1.0 == 0.0 && kotlin.math.abs(it) < 1e15) it.toLong().toString() else it.toString() }
        else -> content
    }
    else -> null
}

/** A number in a JSON value (also from a numeric string). */
fun JsonElement?.numberValue(): Double? = (this as? JsonPrimitive)?.let { it.doubleOrNull ?: it.content.toDoubleOrNull() }
