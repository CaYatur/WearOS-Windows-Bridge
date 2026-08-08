package dev.caya.wearbridge

import android.content.Context
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.atomic.AtomicBoolean

class LanBridgeClient(
    private val context: Context,
    private val onState: (RemoteBridgeState) -> Unit,
    private val onConnection: (Boolean) -> Unit
) {
    private val running = AtomicBoolean(false)
    private val outgoing = LinkedBlockingQueue<String>()
    private var worker: Thread? = null

    fun start() {
        if (!running.compareAndSet(false, true)) return
        worker = Thread { loop() }.apply { name = "WearBridge-LAN"; start() }
    }

    fun stop() { running.set(false); worker?.interrupt(); worker = null }
    fun send(command: String) { outgoing.offer(command) }

    private fun loop() {
        val prefs = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)
        while (running.get()) {
            var host = prefs.getString("host", "")?.trim().orEmpty()
            if (host.isBlank()) { host=LanDiscovery.discover().orEmpty(); if(host.isNotBlank()) prefs.edit().putString("host",host).apply() }
            if (host.isNotBlank() && prefs.getString("pairingKey","").isNullOrBlank()) AutoPairing.ensurePaired(context,host)
            val key = BridgeProtocol.decodeKey(prefs.getString("pairingKey", "").orEmpty())
            if (host.isBlank() || key == null) { onConnection(false); sleep(); continue }
            try {
                Socket().use { socket ->
                    socket.connect(InetSocketAddress(host, 38471), 3000)
                    socket.soTimeout = 5000
                    onConnection(true)
                    val reader = BufferedReader(InputStreamReader(socket.getInputStream(), Charsets.UTF_8))
                    val writer = BufferedWriter(OutputStreamWriter(socket.getOutputStream(), Charsets.UTF_8))
                    while (running.get() && !socket.isClosed) {
                        val msg = outgoing.poll() ?: BridgeProtocol.ping(key, BridgeProtocol.enabledFeatures(prefs))
                        writer.write(msg); writer.newLine(); writer.flush()
                        val line = reader.readLine() ?: break
                        BridgeProtocol.verifyAndReadState(line, key)?.let(onState)
                        Thread.sleep(750)
                    }
                }
            } catch (_: Exception) { onConnection(false); sleep() }
        }
        onConnection(false)
    }

    private fun sleep() { try { Thread.sleep(2000) } catch (_: InterruptedException) {} }
}
