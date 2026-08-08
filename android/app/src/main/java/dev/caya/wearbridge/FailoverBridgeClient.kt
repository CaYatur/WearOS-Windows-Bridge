package dev.caya.wearbridge

import android.content.Context

/**
 * Runs both transports at once and presents whichever is actually carrying authenticated traffic.
 * Bluetooth wins when it is up; Wi-Fi stays warm underneath so a radio drop costs no reconnect.
 *
 * "Up" here means the peer has proved it holds the pairing key — an open socket is not enough. The
 * failover used to switch on socket-open alone, which routed taps into a link that answered nothing.
 */
class FailoverBridgeClient(
    context: Context,
    onState: (RemoteBridgeState) -> Unit,
    private val onConnection: (String) -> Unit
) {
    @Volatile private var bluetoothUp = false
    @Volatile private var lanUp = false

    private val bluetooth = BluetoothBridgeClient(context, onState) { up ->
        bluetoothUp = up
        onConnection(activeTransport())
    }

    // While Bluetooth carries the session, Wi-Fi frames are ignored so the two cannot fight over
    // the displayed state. The socket stays open, ready to take over instantly.
    private val lan = LanBridgeClient(context, { state -> if (!bluetoothUp) onState(state) }) { up ->
        lanUp = up
        onConnection(activeTransport())
    }

    fun start() { bluetooth.start(); lan.start() }

    fun stop() { bluetooth.stop(); lan.stop() }

    fun send(message: String) {
        if (bluetoothUp) bluetooth.send(message) else lan.send(message)
    }

    fun activeTransport() = when {
        bluetoothUp -> "Bluetooth"
        lanUp -> "LAN"
        else -> "Disconnected"
    }
}
