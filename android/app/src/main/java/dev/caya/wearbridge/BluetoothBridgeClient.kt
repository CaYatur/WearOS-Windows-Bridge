package dev.caya.wearbridge

import android.Manifest
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import androidx.core.content.ContextCompat
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

/**
 * RFCOMM transport. Requires the PC to be bonded with *this* device — on a watch that means
 * pairing the watch itself with the PC, which many Wear OS builds do not expose. When no bonded
 * device answers, [FailoverBridgeClient] stays on Wi-Fi; that is the expected path, not an error.
 */
class BluetoothBridgeClient(
    private val context: Context,
    private val onState: (RemoteBridgeState) -> Unit,
    private val onConnection: (Boolean) -> Unit
) {
    companion object {
        val SERVICE_UUID: UUID = UUID.fromString("7e3d7b5a-3c51-4a32-93ab-c854b152e743")
        private const val TAG = "WearBridgeBT"
        private const val MIN_RETRY_MS = 3_000L
        private const val MAX_RETRY_MS = 60_000L
    }

    private val running = AtomicBoolean(false)
    private var worker: Thread? = null
    @Volatile private var link: BridgeLink? = null
    @Volatile var connected = false; private set

    fun start() {
        if (!running.compareAndSet(false, true)) return
        worker = Thread { loop() }.apply { name = "WearBridge-Bluetooth"; isDaemon = true; start() }
    }

    fun stop() {
        running.set(false)
        link?.close()
        worker?.interrupt()
        worker = null
        setConnected(false)
    }

    fun send(message: String) { link?.send(message) }

    private fun loop() {
        val prefs = context.getSharedPreferences("bridge", Context.MODE_PRIVATE)
        var retryMs = MIN_RETRY_MS
        while (running.get()) {
            try {
                val key = BridgeProtocol.decodeKey(prefs.getString("pairingKey", "").orEmpty())
                if (key == null || !hasPermission()) { retryMs = backoff(retryMs); continue }

                val adapter = adapter()
                if (adapter == null || !adapter.isEnabled) { retryMs = backoff(retryMs); continue }

                val candidates = candidates(adapter, prefs)
                if (candidates.isEmpty()) { retryMs = backoff(retryMs); continue }

                var openedAny = false
                for (device in candidates) {
                    if (!running.get()) break
                    val socket = connect(adapter, device) ?: continue
                    openedAny = true
                    retryMs = MIN_RETRY_MS
                    // Remember the winner so later reconnects skip the whole bonded list.
                    prefs.edit().putString("btAddress", device.address).apply()
                    try {
                        val session = BridgeLink(TAG, key, { BridgeProtocol.enabledFeatures(prefs) }, onState)
                        link = session
                        session.run(socket.inputStream, socket.outputStream, socket) { setConnected(true) }
                    } finally {
                        link = null
                        setConnected(false)
                        runCatching { socket.close() }
                    }
                    break
                }
                if (!openedAny) {
                    // The PC is bonded but its bridge service is not reachable; back off rather than
                    // hammering the radio, which starves Wi-Fi on a watch.
                    retryMs = backoff(retryMs)
                }
            } catch (e: InterruptedException) {
                break
            } catch (e: Exception) {
                if (running.get()) Log.w(TAG, "bluetooth loop error: ${e.message}")
                retryMs = backoff(retryMs)
            }
        }
        setConnected(false)
    }

    /**
     * Every bonded device, preferring the one that worked last time. v1 filtered on a name
     * containing "Windows" or "PC", which never matches the usual DESKTOP-XXXXXX, so Bluetooth
     * could not connect at all.
     */
    private fun candidates(adapter: BluetoothAdapter, prefs: android.content.SharedPreferences): List<BluetoothDevice> {
        val preferred = prefs.getString("btAddress", "")?.trim().orEmpty()
        val bonded = try { adapter.bondedDevices?.toList().orEmpty() } catch (_: SecurityException) { emptyList() }
        if (bonded.isEmpty()) return emptyList()
        val known = bonded.filter { it.address.equals(preferred, ignoreCase = true) }
        // A user-typed address may not be bonded yet; still worth one attempt.
        if (known.isEmpty() && BluetoothAdapter.checkBluetoothAddress(preferred)) {
            return listOf(adapter.getRemoteDevice(preferred)) + bonded
        }
        return known + bonded.filterNot { it.address.equals(preferred, ignoreCase = true) }
    }

    private fun connect(adapter: BluetoothAdapter, device: BluetoothDevice): BluetoothSocket? {
        runCatching { adapter.cancelDiscovery() }
        // Secure first; some stacks only accept the insecure variant against a Windows SPP record.
        for (secure in listOf(true, false)) {
            val socket = try {
                if (secure) device.createRfcommSocketToServiceRecord(SERVICE_UUID)
                else device.createInsecureRfcommSocketToServiceRecord(SERVICE_UUID)
            } catch (e: SecurityException) {
                Log.w(TAG, "missing BLUETOOTH_CONNECT for ${device.address}"); return null
            } catch (e: Exception) {
                Log.w(TAG, "socket create failed for ${device.address}: ${e.message}"); continue
            }
            try {
                socket.connect()
                Log.i(TAG, "connected to ${device.address} (secure=$secure)")
                return socket
            } catch (e: Exception) {
                runCatching { socket.close() }
                Log.i(TAG, "connect to ${device.address} failed (secure=$secure): ${e.message}")
            }
        }
        return null
    }

    private fun adapter(): BluetoothAdapter? = try {
        ContextCompat.getSystemService(context, BluetoothManager::class.java)?.adapter
    } catch (e: Exception) {
        Log.w(TAG, "bluetooth unavailable: ${e.message}"); null
    }

    private fun backoff(current: Long): Long {
        try { Thread.sleep(current) } catch (_: InterruptedException) { return current }
        return (current * 2).coerceAtMost(MAX_RETRY_MS)
    }

    private fun setConnected(value: Boolean) {
        if (connected != value) { connected = value; onConnection(value) }
    }

    private fun hasPermission() = Build.VERSION.SDK_INT < 31 ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.BLUETOOTH_CONNECT) == PackageManager.PERMISSION_GRANTED
}
