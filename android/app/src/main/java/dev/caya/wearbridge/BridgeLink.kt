package dev.caya.wearbridge

import android.util.Log
import java.io.BufferedReader
import java.io.BufferedWriter
import java.io.Closeable
import java.io.InputStream
import java.io.InputStreamReader
import java.io.OutputStream
import java.io.OutputStreamWriter
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * One authenticated conversation over an already-connected stream pair, shared by the LAN and
 * Bluetooth transports.
 *
 * Reads and writes run on separate threads. The previous design sent one frame then blocked for the
 * reply, so a queued tap waited for a full poll cycle and any slow reply looked like a dead link.
 * Liveness is judged by "did anything arrive recently", which works on RFCOMM too — those sockets
 * ignore read timeouts and would otherwise block forever on a wedged peer.
 */
class BridgeLink(
    private val tag: String,
    private val key: ByteArray,
    private val enabledFeatures: () -> Int,
    private val onState: (RemoteBridgeState) -> Unit
) {
    companion object {
        /** Keeps NAT/RFCOMM state warm and tells the PC which features are on. */
        private const val PING_INTERVAL_MS = 5_000L

        /** The PC pushes state every second; this much silence means the link is gone. */
        const val SILENCE_TIMEOUT_MS = 20_000L
    }

    private val outgoing = LinkedBlockingQueue<String>()
    private val alive = AtomicBoolean(true)

    @Volatile private var lastInboundAt = System.currentTimeMillis()
    @Volatile private var acceptedFrames = 0
    @Volatile private var rejectedFrames = 0

    /** True once at least one authenticated frame has arrived — the only honest "connected". */
    val authenticated: Boolean get() = acceptedFrames > 0

    fun send(message: String) { outgoing.offer(message) }

    fun close() { alive.set(false) }

    /**
     * Pumps the connection until it dies. Returns normally on a clean close; the caller reconnects.
     * [onReady] fires once the peer has proved it holds the pairing key.
     */
    fun run(input: InputStream, output: OutputStream, socket: Closeable, onReady: () -> Unit) {
        val reader = BufferedReader(InputStreamReader(input, Charsets.UTF_8))
        val writer = BufferedWriter(OutputStreamWriter(output, Charsets.UTF_8))

        // A blocked read cannot be interrupted on RFCOMM; closing the socket is what unblocks it.
        val watchdog = Thread {
            try {
                while (alive.get()) {
                    Thread.sleep(1_000)
                    if (System.currentTimeMillis() - lastInboundAt > SILENCE_TIMEOUT_MS) {
                        Log.w(tag, "no authenticated frame for ${SILENCE_TIMEOUT_MS}ms, dropping link")
                        alive.set(false)
                    }
                }
            } catch (_: InterruptedException) {
            } finally {
                runCatching { socket.close() }
            }
        }.apply { name = "$tag-watchdog"; isDaemon = true; start() }

        val sender = Thread {
            try {
                var lastPingAt = 0L
                while (alive.get()) {
                    val queued = outgoing.poll(500, TimeUnit.MILLISECONDS)
                    val now = System.currentTimeMillis()
                    val message = when {
                        queued != null -> queued
                        now - lastPingAt >= PING_INTERVAL_MS -> {
                            lastPingAt = now
                            BridgeProtocol.ping(key, enabledFeatures())
                        }
                        else -> continue
                    }
                    writer.write(message)
                    writer.newLine()
                    writer.flush()
                }
            } catch (e: Exception) {
                if (alive.get()) Log.w(tag, "send failed: ${e.message}")
                alive.set(false)
            }
        }.apply { name = "$tag-sender"; isDaemon = true; start() }

        try {
            // An immediate ping makes the PC answer at once instead of after its next push tick.
            outgoing.offer(BridgeProtocol.ping(key, enabledFeatures()))
            lastInboundAt = System.currentTimeMillis()
            while (alive.get()) {
                val line = reader.readLine() ?: break
                if (line.isEmpty()) continue
                var reason = RejectReason.NONE
                val state = BridgeProtocol.verifyAndReadState(line, key) { reason = it }
                if (state == null) {
                    rejectedFrames++
                    // Surface the first one: a systematically rejected link (wrong key, stale APK,
                    // clock far off) is otherwise indistinguishable from an idle one.
                    if (rejectedFrames == 1 || rejectedFrames % 50 == 0) {
                        Log.w(tag, "rejected frame ($reason), $rejectedFrames so far: ${line.take(120)}")
                    }
                    continue
                }
                val firstFrame = acceptedFrames == 0
                acceptedFrames++
                lastInboundAt = System.currentTimeMillis()
                if (firstFrame) { Log.i(tag, "link authenticated"); onReady() }
                onState(state)
            }
        } catch (e: Exception) {
            if (alive.get()) Log.w(tag, "receive failed: ${e.message}")
        } finally {
            alive.set(false)
            sender.interrupt()
            watchdog.interrupt()
            runCatching { socket.close() }
            Log.i(tag, "link closed after $acceptedFrames accepted / $rejectedFrames rejected frames")
        }
    }
}
