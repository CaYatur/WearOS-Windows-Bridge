package dev.caya.wearbridge

import android.util.Log
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface

/**
 * Finds the PC by UDP broadcast.
 *
 * The probe goes to every interface's own broadcast address as well as 255.255.255.255, because
 * many Android builds silently drop the global address, and to each of several attempts, because a
 * single lost datagram on Wi-Fi otherwise reads as "PC not found".
 */
object LanDiscovery {
    private const val TAG = "WearBridgeDiscovery"
    private const val PORT = 38472
    private const val PROBE = "WEARBRIDGE_DISCOVER_V1"
    private const val REPLY_PREFIX = "WEARBRIDGE_HERE_V1|"

    fun discover(timeoutMs: Int = 2500): String? {
        val deadline = System.currentTimeMillis() + timeoutMs
        return try {
            DatagramSocket().use { socket ->
                socket.broadcast = true
                val probe = PROBE.toByteArray()
                val targets = broadcastAddresses()
                var attempt = 0
                while (System.currentTimeMillis() < deadline) {
                    for (target in targets) {
                        runCatching { socket.send(DatagramPacket(probe, probe.size, target, PORT)) }
                            .onFailure { Log.d(TAG, "probe to $target failed: ${it.message}") }
                    }
                    attempt++
                    val remaining = (deadline - System.currentTimeMillis()).toInt()
                    if (remaining <= 0) break
                    socket.soTimeout = remaining.coerceAtMost(700)
                    val buffer = ByteArray(256)
                    val response = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(response)
                        val message = String(response.data, 0, response.length)
                        if (message.startsWith(REPLY_PREFIX)) {
                            val host = response.address.hostAddress
                            Log.i(TAG, "found PC at $host after $attempt attempt(s)")
                            return host
                        }
                    } catch (_: Exception) {
                        // Timed out waiting for this round; try again until the deadline.
                    }
                }
                Log.i(TAG, "no PC answered after $attempt attempt(s)")
                null
            }
        } catch (e: Exception) {
            Log.w(TAG, "discovery failed: ${e.message}")
            null
        }
    }

    private fun broadcastAddresses(): List<InetAddress> {
        val addresses = mutableListOf<InetAddress>()
        try {
            for (nic in NetworkInterface.getNetworkInterfaces()) {
                if (!nic.isUp || nic.isLoopback) continue
                for (address in nic.interfaceAddresses) {
                    address.broadcast?.let(addresses::add)
                }
            }
        } catch (e: Exception) {
            Log.d(TAG, "could not enumerate interfaces: ${e.message}")
        }
        runCatching { addresses.add(InetAddress.getByName("255.255.255.255")) }
        return addresses.distinct()
    }
}
