plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

kotlin {
    jvmToolchain(17)
}

dependencies {
    api(libs.kotlinx.coroutines.core)
    api(libs.kotlinx.serialization.json)
    api(libs.okhttp)
    testImplementation(libs.junit)
    testImplementation(libs.kotlinx.coroutines.test)
    testImplementation(libs.okhttp.mockwebserver)
}

// The portal's shared crypto test vectors (portal/test/vectors.json), which the .NET and TypeScript tests check too.
tasks.test {
    systemProperty("cuebot.portalVectors", rootProject.file("../../portal/test/vectors.json").absolutePath)
}
