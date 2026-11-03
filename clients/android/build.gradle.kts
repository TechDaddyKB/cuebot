// Cuebot Companion for Android. :core is plain Kotlin/JVM (protocol, models, SSE, pairing; tested without an
// emulator), :app is the Compose app.
plugins {
    alias(libs.plugins.android.application) apply false
    alias(libs.plugins.kotlin.jvm) apply false
    alias(libs.plugins.kotlin.compose) apply false
    alias(libs.plugins.kotlin.serialization) apply false
}
