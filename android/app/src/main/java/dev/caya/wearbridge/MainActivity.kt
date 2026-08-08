package dev.caya.wearbridge

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.view.Gravity
import android.view.ViewGroup
import android.widget.*

class MainActivity : Activity() {
 override fun onCreate(savedInstanceState:Bundle?) { super.onCreate(savedInstanceState); requestBt(); buildUi() }
 private fun requestBt(){ if(android.os.Build.VERSION.SDK_INT>=31 && checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT)!=PackageManager.PERMISSION_GRANTED) requestPermissions(arrayOf(Manifest.permission.BLUETOOTH_CONNECT),7) }
 private fun buildUi(){
  val wear=DeviceModeDetector.detect(this)==DeviceMode.WEAR_OS
  val prefs=getSharedPreferences("bridge",MODE_PRIVATE)
  val body=LinearLayout(this).apply { orientation=LinearLayout.VERTICAL; gravity=Gravity.CENTER_HORIZONTAL; val p=if(wear) 26 else 32; setPadding(p,p,p,p) }
  fun text(value:String,size:Float=if(wear)14f else 18f)=TextView(this).apply { text=value; textSize=size; gravity=Gravity.CENTER_HORIZONTAL; setPadding(4,8,4,8) }
  body.addView(text("WearOS ↔ Windows Bridge",if(wear)18f else 22f)); body.addView(text(if(wear) "Wear OS direct mode" else "Phone companion mode"))
  val status=text("Connection: ${BridgeStatus.get(this).first}"); body.addView(status)
  val ip=EditText(this).apply { hint="PC IP (auto if blank)"; setText(prefs.getString("host","")); isSingleLine=true }; body.addView(ip,ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT))
  val bt=EditText(this).apply { hint="PC Bluetooth MAC (optional)"; setText(prefs.getString("btAddress","")); isSingleLine=true }; body.addView(bt,ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT))
  val key=EditText(this).apply { hint="Pairing key"; setText(prefs.getString("pairingKey","")); isSingleLine=true }; body.addView(key,ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT))
  body.addView(Button(this).apply { text="Find PC automatically"; setOnClickListener { isEnabled=false; Thread { val found=LanDiscovery.discover(); runOnUiThread { if(found!=null){ip.setText(found);Toast.makeText(this@MainActivity,"PC found: $found",Toast.LENGTH_SHORT).show()}else Toast.makeText(this@MainActivity,"PC not found on LAN",Toast.LENGTH_SHORT).show();isEnabled=true } }.start() } })
  val features=listOf("media" to "Media","volume" to "Volume","clipboard" to "Clipboard","system" to "System stats")
  val boxes=features.map { (k,label)-> CheckBox(this).apply { text=label; isChecked=prefs.getBoolean("feature_$k",true); body.addView(this) } to k }
  body.addView(Button(this).apply { text="Save & start bridge"; setOnClickListener { prefs.edit().putString("host",ip.text.toString().trim()).putString("btAddress",bt.text.toString().trim()).putString("pairingKey",key.text.toString().trim()).apply { boxes.forEach{(b,k)->putBoolean("feature_$k",b.isChecked)} }.apply(); androidx.core.content.ContextCompat.startForegroundService(this@MainActivity,Intent(this@MainActivity,BridgeMediaService::class.java)); Toast.makeText(this@MainActivity,"Bridge started",Toast.LENGTH_SHORT).show() } })
  body.addView(Button(this).apply { text="Refresh connection"; setOnClickListener { status.text="Connection: ${BridgeStatus.get(this@MainActivity).first}" } })
  setContentView(ScrollView(this).apply { isFillViewport=true; addView(body,ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT,ViewGroup.LayoutParams.WRAP_CONTENT)) })
 }
}
