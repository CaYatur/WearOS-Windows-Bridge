package dev.caya.wearbridge

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.ViewGroup
import android.widget.*

class MainActivity : Activity() {
    private val ui = Handler(Looper.getMainLooper())
    private var statusView: TextView? = null
    private val refresh = object : Runnable {
        override fun run() {
            statusView?.text = statusText()
            ui.postDelayed(this, 1_000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestRuntimePermissions()
        buildUi()
    }

    override fun onResume() { super.onResume(); ui.post(refresh) }
    override fun onPause() { super.onPause(); ui.removeCallbacks(refresh) }

    /** Bluetooth needs consent on 12+, and 13+ suppresses the service notification without consent. */
    private fun requestRuntimePermissions() {
        val wanted = buildList {
            if (Build.VERSION.SDK_INT >= 31) add(Manifest.permission.BLUETOOTH_CONNECT)
            if (Build.VERSION.SDK_INT >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
        }.filter { checkSelfPermission(it) != PackageManager.PERMISSION_GRANTED }
        if (wanted.isNotEmpty()) requestPermissions(wanted.toTypedArray(), 7)
    }

    private fun statusText(): String {
        val (transport, updated) = BridgeStatus.get(this)
        if (updated == 0L) return "Connection: not started"
        val age = (System.currentTimeMillis() - updated) / 1000
        // A stale timestamp means the service is gone, which reads very differently from "connected".
        if (age > 60) return "Connection: service not running (last $transport ${age}s ago)"
        return "Connection: $transport"
    }

    private fun buildUi() {
        val wear = DeviceModeDetector.detect(this) == DeviceMode.WEAR_OS
        val prefs = getSharedPreferences("bridge", MODE_PRIVATE)
        val body = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            val p = if (wear) 26 else 32
            setPadding(p, p, p, p)
        }
        fun text(value: String, size: Float = if (wear) 14f else 18f) = TextView(this).apply {
            text = value; textSize = size; gravity = Gravity.CENTER_HORIZONTAL; setPadding(4, 8, 4, 8)
        }
        fun wide(view: android.view.View) = body.addView(
            view, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
        )

        body.addView(text("WearOS ↔ Windows Bridge", if (wear) 18f else 22f))
        body.addView(text(if (wear) "Wear OS direct mode" else "Phone companion mode"))
        val status = text(statusText()).also { statusView = it; body.addView(it) }

        val ip = EditText(this).apply { hint = "PC IP (auto if blank)"; setText(prefs.getString("host", "")); isSingleLine = true }
        wide(ip)
        val bt = EditText(this).apply { hint = "PC Bluetooth MAC (optional)"; setText(prefs.getString("btAddress", "")); isSingleLine = true }
        wide(bt)
        val key = EditText(this).apply { hint = "Pairing key (auto on approval)"; setText(prefs.getString("pairingKey", "")); isSingleLine = true }
        wide(key)

        body.addView(Button(this).apply {
            text = "Find PC automatically"
            setOnClickListener {
                isEnabled = false
                Thread {
                    val found = LanDiscovery.discover()
                    runOnUiThread {
                        if (found != null) {
                            ip.setText(found)
                            toast("PC found: $found")
                        } else {
                            toast("PC not found. Check that both are on the same Wi-Fi and the tray app is running.")
                        }
                        isEnabled = true
                    }
                }.start()
            }
        })

        val features = listOf("media" to "Media", "volume" to "Volume", "clipboard" to "Clipboard", "system" to "System stats")
        val boxes = features.map { (k, label) ->
            CheckBox(this).apply { text = label; isChecked = prefs.getBoolean("feature_$k", true); body.addView(this) } to k
        }

        body.addView(Button(this).apply {
            text = "Save & start bridge"
            setOnClickListener {
                prefs.edit()
                    .putString("host", ip.text.toString().trim())
                    .putString("btAddress", bt.text.toString().trim())
                    .putString("pairingKey", key.text.toString().trim())
                    .apply { boxes.forEach { (b, k) -> putBoolean("feature_$k", b.isChecked) } }
                    .apply()
                // Restart so the service picks up the new settings instead of running on the old ones.
                stopService(Intent(this@MainActivity, BridgeMediaService::class.java))
                androidx.core.content.ContextCompat.startForegroundService(
                    this@MainActivity, Intent(this@MainActivity, BridgeMediaService::class.java)
                )
                status.text = statusText()
                toast("Bridge started")
            }
        })

        body.addView(Button(this).apply {
            text = "Stop bridge"
            setOnClickListener {
                stopService(Intent(this@MainActivity, BridgeMediaService::class.java))
                BridgeStatus.set(this@MainActivity, "Disconnected")
                status.text = statusText()
                toast("Bridge stopped")
            }
        })

        body.addView(Button(this).apply {
            text = "Forget pairing"
            setOnClickListener {
                prefs.edit().remove("pairingKey").apply()
                key.setText("")
                toast("Pairing cleared. Approve the new request on the PC.")
            }
        })

        setContentView(ScrollView(this).apply {
            isFillViewport = true
            addView(body, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        })
    }

    private fun toast(message: String) = Toast.makeText(this, message, Toast.LENGTH_LONG).show()
}
