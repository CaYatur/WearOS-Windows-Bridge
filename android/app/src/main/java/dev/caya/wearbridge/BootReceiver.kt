package dev.caya.wearbridge

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log
import androidx.core.content.ContextCompat

/**
 * Brings the bridge back after a reboot or an app update, but only once it has been set up —
 * an unpaired install has nothing to connect to.
 *
 * Android 12+ refuses some background foreground-service starts. That refusal is expected and
 * survivable: the bridge starts normally the next time the app is opened, so it is logged, never
 * thrown. Letting it escape here would crash the app during boot.
 */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        val prefs = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)
        if (prefs.getString("pairingKey", "").isNullOrBlank()) return
        try {
            ContextCompat.startForegroundService(context, Intent(context, BridgeMediaService::class.java))
            Log.i("WearBridgeBoot", "bridge restarted after ${intent.action}")
        } catch (e: Exception) {
            Log.w("WearBridgeBoot", "could not restart bridge on boot: ${e.message}")
        }
    }
}
