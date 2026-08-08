package dev.caya.wearbridge

import android.Manifest
import android.content.Intent
import android.os.Bundle
import android.widget.*
import android.app.Activity

class MainActivity : Activity() {
 override fun onCreate(savedInstanceState: Bundle?) { super.onCreate(savedInstanceState)
  if (android.os.Build.VERSION.SDK_INT >= 31) requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 10)
  val prefs=getSharedPreferences("bridge",MODE_PRIVATE)
  val root=LinearLayout(this).apply { orientation=LinearLayout.VERTICAL; setPadding(32,32,32,32) }
  val mode=DeviceModeDetector.detect(this)
  root.addView(TextView(this).apply { text="WearOS ↔ Windows Bridge\n${if(mode==DeviceMode.WEAR_OS) "Wear OS direct mode" else "Phone companion mode"}\nBluetooth preferred • LAN fallback"; textSize=if(mode==DeviceMode.WEAR_OS) 16f else 20f })
  listOf("media" to "Media + Wear OS controls", "volume" to "Windows volume / mute", "clipboard" to "Clipboard text sync", "status" to "PC status").forEach { (key,label) ->
   root.addView(Switch(this).apply { text=label; isChecked=prefs.getBoolean(key,key=="media"||key=="status"); setOnCheckedChangeListener { _,v->prefs.edit().putBoolean(key,v).apply() } })
  }
  val host=EditText(this).apply { hint="PC local IP (example 192.168.1.10)"; setText(prefs.getString("host","")) }
  val bluetoothAddress=EditText(this).apply { hint="Paired PC Bluetooth MAC (optional)"; setText(prefs.getString("bluetoothAddress","")) }
  val pairingKey=EditText(this).apply { hint="Pairing key (Base64, shown by Windows pairing UI)"; setText(prefs.getString("pairingKey","")) }
  root.addView(host); root.addView(bluetoothAddress); root.addView(pairingKey)
  root.addView(Button(this).apply { text="Save & start bridge"; setOnClickListener {
   if (BridgeProtocol.decodeKey(pairingKey.text.toString()) == null) { Toast.makeText(this@MainActivity,"Pairing key must be a valid 256-bit Base64 key",Toast.LENGTH_LONG).show(); return@setOnClickListener }
   prefs.edit().putString("host",host.text.toString().trim()).putString("bluetoothAddress",bluetoothAddress.text.toString().trim()).putString("pairingKey",pairingKey.text.toString().trim()).apply()
   androidx.core.content.ContextCompat.startForegroundService(this@MainActivity,Intent(this@MainActivity,BridgeMediaService::class.java)); Toast.makeText(this@MainActivity,"Bridge started",Toast.LENGTH_SHORT).show()
  } })
  root.addView(TextView(this).apply { text="Clipboard is off by default. Pairing keys are stored locally and are never displayed in logs." })
  setContentView(root)
 }
}
