package dev.caya.wearbridge

import android.content.Context
import android.util.Log
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Wi-Fi transport. Finds the PC by broadcast when no address is configured, pairs on first contact,
 * then hands the socket to [BridgeLink].
 */
class LanBridgeClient(
    private val context: Context,
    private val onState: (RemoteBridgeState) -> Unit,
    private val onConnection: (Boolean) -> Unit
) {
    companion object {
        const val PORT = 38471
        private const val TAG = "WearBridgeLAN"
        private const val CONNECT_TIMEOUT_MS = 4_000

        /** Backoff bounds. A PC that is simply off should not keep the radio busy. */
        private const val MIN_RETRY_MS = 2_000L
        private const val MAX_RETRY_MS = 30_000L
    }

    private val running = AtomicBoolean(false)
    private var worker: Thread? = null
    @Volatile private var link: BridgeLink? = null
    @Volatile private var connected = false

    fun start() {
        if (!running.compareAndSet(false, true)) return
        worker = Thread { loop() }.apply { name = "WearBridge-LAN"; isDaemon = true; start() }
    }

    fun stop() {
        running.set(false)
        link?.close()
        worker?.interrupt()
        worker = null
    }

    fun send(command: String) { link?.send(command) }

    private fun loop() {
        val prefs = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)
        var retryMs = MIN_RETRY_MS
        while (running.get()) {
            try {
                var host = prefs.getString("host", "")?.trim().orEmpty()
                if (host.isBlank()) {
                    host = LanDiscovery.discover().orEmpty()
                    // Remember it, but never overwrite an address the user typed in.
                    if (host.isNotBlank()) prefs.edit().putString("host", host).apply()
                }
                if (host.isBlank()) { setConnected(false); retryMs = backoff(retryMs); continue }

                if (prefs.getString("pairingKey", "").isNullOrBlank()) AutoPairing.ensurePaired(context, host)
                val key = BridgeProtocol.decodeKey(prefs.getString("pairingKey", "").orEmpty())
                if (key == null) {
                    Log.w(TAG, "no usable pairing key yet; approve pairing on the PC")
                    setConnected(false); retryMs = backoff(retryMs); continue
                }

                Socket().use { socket ->
                    socket.connect(InetSocketAddress(host, PORT), CONNECT_TIMEOUT_MS)
                    socket.tcpNoDelay = true
                    socket.keepAlive = true
                    // Bounded read so a silently dead peer surfaces even before the watchdog fires.
                    socket.soTimeout = BridgeLink.SILENCE_TIMEOUT_MS.toInt()
                    Log.i(TAG, "connected to $host:$PORT")
                    retryMs = MIN_RETRY_MS

                    val session = BridgeLink(TAG, key, { BridgeProtocol.enabledFeatures(prefs) }, onState)
                    link = session
                    // Report connected only once the PC proves it holds the key, so the UI never
                    // claims a working link over a socket that is authenticating into a void.
                    session.run(socket.getInputStream(), socket.getOutputStream(), socket) { setConnected(true) }
                }
            } catch (e: InterruptedException) {
                break
            } catch (e: Exception) {
                if (running.get()) Log.w(TAG, "connection failed: ${e.message}")
            } finally {
                link = null
                setConnected(false)
            }
            if (running.get()) retryMs = backoff(retryMs)
        }
        setConnected(false)
    }

    private fun backoff(current: Long): Long {
        try { Thread.sleep(current) } catch (_: InterruptedException) { return current }
        return (current * 2).coerceAtMost(MAX_RETRY_MS)
    }

    private fun setConnected(value: Boolean) {
        if (connected != value) { connected = value; onConnection(value) }
    }
}
