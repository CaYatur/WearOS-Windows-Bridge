package dev.caya.wearbridge

import android.content.Context

object BridgeStatus {
 private const val PREF="bridge_status"
 fun set(context:Context, transport:String){ context.getSharedPreferences(PREF,0).edit().putString("transport",transport).putLong("updated",System.currentTimeMillis()).apply() }
 fun get(context:Context):Pair<String,Long>{ val p=context.getSharedPreferences(PREF,0); return Pair(p.getString("transport","Disconnected")?:"Disconnected",p.getLong("updated",0)) }
}
