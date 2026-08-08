package dev.caya.wearbridge

import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

object LanDiscovery {
 fun discover(timeoutMs:Int=1800):String? {
  return try {
   DatagramSocket().use { s ->
    s.broadcast=true; s.soTimeout=timeoutMs
    val data="WEARBRIDGE_DISCOVER_V1".toByteArray()
    s.send(DatagramPacket(data,data.size,InetAddress.getByName("255.255.255.255"),38472))
    val buf=ByteArray(256); val p=DatagramPacket(buf,buf.size); s.receive(p)
    val msg=String(p.data,0,p.length)
    if(msg.startsWith("WEARBRIDGE_HERE_V1|")) p.address.hostAddress else null
   }
  } catch(_:Exception){null}
 }
}
