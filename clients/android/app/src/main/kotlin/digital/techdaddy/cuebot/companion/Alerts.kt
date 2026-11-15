package digital.techdaddy.cuebot.companion

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

/** Something waiting for a yes: a mod's change, or Cuebot's own request (e.g. "Switch the category to Helldivers 2?"). */
data class Alert(val key: String, val title: String, val text: String)

/**
 * Shows [Alert]s as notifications, so a request is seen even while the deck fills the screen. Tapping one opens the
 * Mods tab. The app checks for requests only while it runs (no background networking), so these come while it is
 * open or was just put aside. Without the notification permission (Android 13+), nothing is shown.
 */
class AndroidAlerts(private val context: Context) {
    init {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(CHANNEL, "Requests and approvals", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Cuebot asks for a yes: a mod's change, or switching the category to the game you play."
            }
            context.getSystemService(NotificationManager::class.java)?.createNotificationChannel(channel)
        }
    }

    fun show(alert: Alert) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return
        val open = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            .putExtra("tab", "mods")
        val tap = PendingIntent.getActivity(context, alert.key.hashCode(), open, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val notification = NotificationCompat.Builder(context, CHANNEL)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle(alert.title)
            .setContentText(alert.text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(alert.text))
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setContentIntent(tap)
            .setAutoCancel(true)
            .build()
        try {
            NotificationManagerCompat.from(context).notify(alert.key.hashCode(), notification)
        } catch (_: SecurityException) {
            // The permission was taken back between the check and now.
        }
    }

    private companion object {
        const val CHANNEL = "requests"
    }
}

/** Remembers which ids were already announced, so each request alerts once. */
class AlertMemory(private val alert: (Alert) -> Unit) {
    private val seen = mutableSetOf<String>()

    /** Alerts for the items not seen before; forgets ids that are gone (answered elsewhere or expired). */
    fun <T> update(items: List<T>, id: (T) -> String, toAlert: (T) -> Alert) {
        val ids = items.map(id).toSet()
        seen.retainAll(ids)
        for (item in items) if (seen.add(id(item))) alert(toAlert(item))
    }
}
