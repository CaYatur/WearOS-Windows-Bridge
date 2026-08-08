package dev.caya.wearbridge

import android.content.Context
import android.os.Build
import android.util.Log
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Asks the PC for the pairing key. The PC shows an approval dialog, so the read timeout has to
 * outlast a human deciding — but a rejected or ignored prompt must not wedge the connect loop, and
 * repeated prompts must not pile up while someone is looking at the first one.
 */
object AutoPairing {
    private const val TAG = "WearBridgePairing"
    const val PORT = 38473
    private const val CONNECT_TIMEOUT_MS = 4_000
    private const val APPROVAL_TIMEOUT_MS = 70_000

    /** One pairing attempt at a time, and not more often than this after a refusal. */
    private const val RETRY_AFTER_DENIAL_MS = 30_000L

    private val gate = Any()
    @Volatile private var attemptInFlight = false
    @Volatile private var nextAttemptAt = 0L

    fun ensurePaired(context: Context, host: String): Boolean {
        val prefs = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)
        if (!prefs.getString("pairingKey", "").isNullOrBlank()) return true

        synchronized(gate) {
            if (attemptInFlight || System.currentTimeMillis() < nextAttemptAt) return false
            attemptInFlight = true
        }
        try {
            Socket().use { socket ->
                socket.connect(InetSocketAddress(host, PORT), CONNECT_TIMEOUT_MS)
                socket.soTimeout = APPROVAL_TIMEOUT_MS
                val out = PrintWriter(socket.getOutputStream(), true)
                val input = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.UTF_8))
                val name = "${Build.MANUFACTURER} ${Build.MODEL}"
                out.println(JSONObject().put("type", "pair_request").put("name", name).toString())
                Log.i(TAG, "pairing request sent to $host, waiting for approval")

                val line = input.readLine()
                if (line == null) { Log.w(TAG, "PC closed the pairing connection"); return deny() }
                val response = JSONObject(line)
                if (response.optString("type") != "pair_ok") { Log.w(TAG, "pairing was denied on the PC"); return deny() }
                val key = response.optString("key")
                if (BridgeProtocol.decodeKey(key) == null) { Log.w(TAG, "PC sent an unusable pairing key"); return deny() }

                prefs.edit().putString("pairingKey", key).apply()
                Log.i(TAG, "paired with $host")
                return true
            }
        } catch (e: Exception) {
            Log.w(TAG, "pairing with $host failed: ${e.message}")
            return deny()
        } finally {
            attemptInFlight = false
        }
    }

    private fun deny(): Boolean {
        nextAttemptAt = System.currentTimeMillis() + RETRY_AFTER_DENIAL_MS
        return false
    }
}
