package dev.caya.wearbridge

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.util.UUID
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean

class BluetoothBridgeClient(
 private val context: Context,
 private val onState: (RemoteMediaState) -> Unit,
 private val onConnection: (Boolean) -> Unit
) {
 companion object { val SERVICE_UUID: UUID = UUID.fromString("7e3d7b5a-3c51-4a32-93ab-c854b152e743") }
 private val running=AtomicBoolean(false)
 private val outgoing=LinkedBlockingQueue<String>()
 private var worker: Thread?=null
 @Volatile var connected=false; private set

 fun start() { if(running.compareAndSet(false,true)) worker=Thread { loop() }.apply { name="WearBridge-Bluetooth"; start() } }
 fun stop() { running.set(false); worker?.interrupt(); worker=null; connected=false }
 fun send(message:String)=outgoing.offer(message)

 private fun loop() {
  val prefs=context.getSharedPreferences("bridge",Context.MODE_PRIVATE)
  while(running.get()) {
   val key=BridgeProtocol.decodeKey(prefs.getString("pairingKey","").orEmpty())
   val address=prefs.getString("bluetoothAddress","")?.trim().orEmpty()
   if(key==null || address.isBlank() || !hasPermission()) { setConnected(false); sleep(); continue }
   var socket: BluetoothSocket?=null
   try {
    val adapter=BluetoothAdapter.getDefaultAdapter() ?: throw IllegalStateException("Bluetooth unavailable")
    val device:BluetoothDevice=adapter.getRemoteDevice(address)
    socket=device.createRfcommSocketToServiceRecord(SERVICE_UUID)
    adapter.cancelDiscovery(); socket.connect(); setConnected(true)
    val reader=BufferedReader(InputStreamReader(socket.inputStream,Charsets.UTF_8))
    val writer=BufferedWriter(OutputStreamWriter(socket.outputStream,Charsets.UTF_8))
    while(running.get() && socket.isConnected) {
     val msg=outgoing.poll() ?: BridgeProtocol.ping(key,BridgeProtocol.enabledFeatures(prefs))
     writer.write(msg); writer.newLine(); writer.flush()
     val line=reader.readLine() ?: break
     BridgeProtocol.verifyAndReadState(line,key)?.let(onState)
     Thread.sleep(500)
    }
   } catch (_:Exception) { } finally { try { socket?.close() } catch (_:Exception) {}; setConnected(false) }
   sleep()
  }
 }
 private fun setConnected(value:Boolean) { if(connected!=value) { connected=value; onConnection(value) } }
 private fun hasPermission() = Build.VERSION.SDK_INT < 31 || ContextCompat.checkSelfPermission(context,Manifest.permission.BLUETOOTH_CONNECT)==PackageManager.PERMISSION_GRANTED
 private fun sleep(){ try{Thread.sleep(1500)}catch(_:InterruptedException){} }
}
