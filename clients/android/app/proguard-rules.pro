# kotlinx.serialization: keep the generated serializers of the app's @Serializable classes.
-keepclassmembers @kotlinx.serialization.Serializable class digital.techdaddy.cuebot.** {
    *** Companion;
    kotlinx.serialization.KSerializer serializer(...);
}
-keepclasseswithmembers class digital.techdaddy.cuebot.** {
    kotlinx.serialization.KSerializer serializer(...);
}
-dontwarn org.slf4j.**
