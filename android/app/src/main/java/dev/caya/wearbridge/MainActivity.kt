package dev.caya.wearbridge

import android.Manifest
import android.content.Intent
import android.os.Bundle
import android.widget.*
import androidx.appcompat.app.AppCompatActivity

class MainActivity : AppCompatActivity() {
 override fun onCreate(savedInstanceState: Bundle?) { super.onCreate(savedInstanceState)
  if (android.os.Build.VERSION.SDK_INT >= 31) requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT), 10)
  val prefs=getSharedPreferences("bridge",MODE_PRIVATE)
  val root=LinearLayout(this).apply { orientation=LinearLayout.VERTICAL; setPadding(32,32,32,32) }
  root.addView(TextView(this).apply { text="WearOS ↔ Windows Bridge\nBluetooth preferred • LAN fallback"; textSize=20f })
  listOf("media" to "Media + Wear OS controls", "volume" to "Windows volume / mute", "clipboard" to "Clipboard text sync", "status" to "PC status").forEach { (key,label) ->
   root.addView(Switch(this).apply { text=label; isChecked=prefs.getBoolean(key,key=="media"||key=="status"); setOnCheckedChangeListener { _,v->prefs.edit().putBoolean(key,v).apply() } })
  }
  val host=EditText(this).apply { hint="PC local IP (example 192.168.1.10)"; setText(prefs.getString("host","")) }
  root.addView(host)
  root.addView(Button(this).apply { text="Save & start bridge"; setOnClickListener { prefs.edit().putString("host",host.text.toString().trim()).apply(); startService(Intent(this@MainActivity,BridgeMediaService::class.java)); Toast.makeText(this@MainActivity,"Bridge started",Toast.LENGTH_SHORT).show() } })
  root.addView(TextView(this).apply { text="Clipboard is off by default. Pairing keys are stored locally and are never displayed in logs." })
  setContentView(root)
 }
}
