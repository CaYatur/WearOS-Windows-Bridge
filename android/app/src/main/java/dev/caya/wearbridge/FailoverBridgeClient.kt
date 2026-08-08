package dev.caya.wearbridge

import android.content.Context

/** Bluetooth is always preferred. LAN stays available as a warm fallback and carries traffic only while RFCOMM is down. */
class FailoverBridgeClient(context:Context, onState:(RemoteMediaState)->Unit, onConnection:(String)->Unit) {
 @Volatile private var bluetoothUp=false
 @Volatile private var lanUp=false
 private val bluetooth=BluetoothBridgeClient(context,onState,{ up->bluetoothUp=up; onConnection(activeTransport()) })
 private val lan=LanBridgeClient(context,{ state->if(!bluetoothUp) onState(state) },{ up->lanUp=up; onConnection(activeTransport()) })
 fun start(){ bluetooth.start(); lan.start() }
 fun stop(){ bluetooth.stop(); lan.stop() }
 fun send(message:String){ if(bluetoothUp) bluetooth.send(message) else lan.send(message) }
 private fun activeTransport()=when { bluetoothUp->"Bluetooth"; lanUp->"LAN"; else->"Disconnected" }
}
