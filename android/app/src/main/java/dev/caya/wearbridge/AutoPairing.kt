package dev.caya.wearbridge

import android.content.Context
import android.os.Build
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.InetSocketAddress
import java.net.Socket

object AutoPairing {
 fun ensurePaired(context:Context, host:String):Boolean {
  val prefs=context.getSharedPreferences("bridge",Context.MODE_PRIVATE)
  if(!prefs.getString("pairingKey","").isNullOrBlank()) return true
  return try {
   Socket().use { s ->
    s.connect(InetSocketAddress(host,38473),3000); s.soTimeout=35000
    val out=PrintWriter(s.getOutputStream(),true); val input=BufferedReader(InputStreamReader(s.getInputStream()))
    out.println(JSONObject().put("type","pair_request").put("name",Build.MANUFACTURER+" "+Build.MODEL).toString())
    val response=JSONObject(input.readLine()?:return false)
    if(response.optString("type")!="pair_ok") return false
    val key=response.optString("key"); if(key.isBlank()) return false
    prefs.edit().putString("pairingKey",key).apply(); true
   }
  } catch(_:Exception){false}
 }
}
