# kotlinx.serialization: keep the generated serializers of the app's @Serializable classes.
-keepclassmembers @kotlinx.serialization.Serializable class digital.techdaddy.cuebot.** {
    *** Companion;
    kotlinx.serialization.KSerializer serializer(...);
}
-keepclasseswithmembers class digital.techdaddy.cuebot.** {
    kotlinx.serialization.KSerializer serializer(...);
}
-dontwarn org.slf4j.**

# ML Kit (the QR pairing scanner) finds its parts through ComponentRegistrar classes named only in the merged manifest's
# meta-data, which R8 does not count as a use: without these rules it deletes VisionCommonRegistrar and renames
# getComponents(), BarcodeScanning.getClient() gets nothing back, and opening the scanner crashes the app.
-keep interface com.google.firebase.components.ComponentRegistrar { *; }
-keep class * implements com.google.firebase.components.ComponentRegistrar {
    <init>();
    java.util.List getComponents();
}
