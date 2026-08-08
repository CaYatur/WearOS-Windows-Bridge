package dev.caya.wearbridge

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.util.Base64
import android.util.Log
import androidx.core.app.ServiceCompat
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.SimpleBasePlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture

class BridgeMediaService : MediaSessionService() {
    companion object {
        private const val TAG = "WearBridgeService"
        private const val CHANNEL_ID = "bridge_status"
        private const val NOTIFICATION_ID = 1001
    }

    private var session: MediaSession? = null
    private lateinit var remotePlayer: RemoteWindowsPlayer
    private lateinit var bridge: FailoverBridgeClient

    override fun onCreate() {
        super.onCreate()
        remotePlayer = RemoteWindowsPlayer()
        bridge = FailoverBridgeClient(this, { state -> state.media?.let(remotePlayer::update) }) { transport ->
            BridgeStatus.set(this, transport)
            remotePlayer.setConnected(transport != "Disconnected")
            Log.i(TAG, "transport is now $transport")
        }
        remotePlayer.commandSink = commandSink@{ command, seekMs ->
            val prefs = getSharedPreferences("bridge", MODE_PRIVATE)
            val key = BridgeProtocol.decodeKey(prefs.getString("pairingKey", "").orEmpty())
            if (key == null) { Log.w(TAG, "command $command dropped: no pairing key"); return@commandSink }
            bridge.send(BridgeProtocol.command(key, command, BridgeProtocol.enabledFeatures(prefs), seekMs))
        }
        session = MediaSession.Builder(this, remotePlayer).build()

        // Only now go foreground: the session exists, which Android 14+ wants for a mediaPlayback
        // service, and everything above is plain object construction so the startForeground deadline
        // is never at risk. startForegroundService() gives the process seconds to call
        // startForeground(); MediaSessionService only does so once playback is actually active, so a
        // bridge that starts while disconnected used to die with
        // ForegroundServiceDidNotStartInTimeException on first launch.
        startForegroundNow("Looking for the PC…")
        bridge.start()
    }

    /** Restart if the system reclaims us; the bridge is meant to stay up. */
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        super.onStartCommand(intent, flags, startId)
        return START_STICKY
    }

    /**
     * Stay in the foreground even when nothing is playing. Media3 detaches the service whenever
     * playback goes inactive, which for a bridge — PC paused, or not connected yet — is the normal
     * resting state and left the service eligible to be killed within minutes.
     */
    override fun onUpdateNotification(session: MediaSession, startInForegroundRequired: Boolean) {
        super.onUpdateNotification(session, true)
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session

    override fun onDestroy() {
        bridge.stop()
        session?.release()
        remotePlayer.release()
        session = null
        super.onDestroy()
    }

    private fun startForegroundNow(text: String) {
        val manager = getSystemService(NotificationManager::class.java)
        manager?.createNotificationChannel(
            NotificationChannel(CHANNEL_ID, "Bridge status", NotificationManager.IMPORTANCE_LOW).apply {
                description = "Shows whether the Windows bridge is connected"
                setShowBadge(false)
            }
        )
        ServiceCompat.startForeground(
            this, NOTIFICATION_ID, buildNotification(text),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK else 0
        )
    }

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle("Windows Bridge")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }
}

private class RemoteWindowsPlayer : SimpleBasePlayer(android.os.Looper.getMainLooper()) {
    /** (command, seekMs) — seekMs is set only for an actual scrub. */
    var commandSink: (String, Long?) -> Unit = { _, _ -> }
    private var media: RemoteMediaState? = null
    private var connected = false

    // The PC sends artwork bytes only when the track changes and repeats the id afterwards, so the
    // last image is kept here and reused while the id holds.
    private var artworkId: String? = null
    private var artwork: ByteArray? = null

    fun update(value: RemoteMediaState) = android.os.Handler(applicationLooper).post {
        if (value.artworkBase64 != null) {
            artwork = runCatching { Base64.decode(value.artworkBase64, Base64.DEFAULT) }.getOrNull()
            artworkId = value.artworkId
        } else if (value.artworkId != artworkId) {
            // Track changed and this frame carries no bytes for it: drop the stale image.
            artwork = null
            artworkId = value.artworkId
        }
        media = value
        invalidateState()
    }

    fun setConnected(value: Boolean) = android.os.Handler(applicationLooper).post {
        connected = value
        invalidateState()
    }

    override fun getState(): State {
        val m = media
        val metadata = MediaMetadata.Builder()
            .setTitle(m?.title ?: "Windows")
            .setArtist(m?.artist)
            .setAlbumTitle(m?.album)
            // setArtworkData takes real bytes. The old data: URI was never loaded by Media3's
            // default bitmap loader, so artwork simply never appeared.
            .apply { artwork?.let { setArtworkData(it, MediaMetadata.PICTURE_TYPE_FRONT_COVER) } }
            .build()
        val item = MediaItem.Builder().setMediaId("windows-current").setMediaMetadata(metadata).build()
        return State.Builder()
            .setAvailableCommands(
                Player.Commands.Builder().addAll(
                    Player.COMMAND_PLAY_PAUSE,
                    Player.COMMAND_SEEK_TO_NEXT,
                    Player.COMMAND_SEEK_TO_PREVIOUS,
                    Player.COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM,
                    Player.COMMAND_GET_TIMELINE,
                    Player.COMMAND_GET_CURRENT_MEDIA_ITEM,
                    Player.COMMAND_GET_METADATA
                ).build()
            )
            .setPlaylist(
                listOf(
                    MediaItemData.Builder("windows-current")
                        .setMediaItem(item)
                        .setDurationUs((m?.durationMs ?: 0).coerceAtLeast(0) * 1000)
                        .build()
                )
            )
            .setCurrentMediaItemIndex(0)
            .setContentPositionMs(m?.positionMs ?: 0)
            .setPlayWhenReady(m?.playing == true, Player.PLAY_WHEN_READY_CHANGE_REASON_REMOTE)
            .setPlaybackState(if (connected && m != null) Player.STATE_READY else Player.STATE_IDLE)
            .build()
    }

    override fun handleSetPlayWhenReady(playWhenReady: Boolean): ListenableFuture<*> {
        commandSink(if (playWhenReady) "play" else "pause", null)
        return Futures.immediateVoidFuture()
    }

    override fun handleSeek(mediaItemIndex: Int, positionMs: Long, seekCommand: Int): ListenableFuture<*> {
        when (seekCommand) {
            Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM -> commandSink("next", null)
            Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM -> commandSink("previous", null)
            // A scrub in the current track carries a real target position.
            else -> commandSink("seek", positionMs.coerceAtLeast(0))
        }
        return Futures.immediateVoidFuture()
    }
}
