package dev.caya.wearbridge

import android.net.Uri
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.SimpleBasePlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture

class BridgeMediaService : MediaSessionService() {
 private var session: MediaSession? = null
 private lateinit var remotePlayer: RemoteWindowsPlayer
 private lateinit var bridge: FailoverBridgeClient

 override fun onCreate() {
  super.onCreate()
  remotePlayer = RemoteWindowsPlayer()
  bridge = FailoverBridgeClient(this, { state -> state.media?.let(remotePlayer::update) }, { transport -> remotePlayer.setConnected(transport != "Disconnected") })
  remotePlayer.commandSink = commandSink@{ command ->
   val prefs=getSharedPreferences("bridge",MODE_PRIVATE)
   val key=BridgeProtocol.decodeKey(prefs.getString("pairingKey","").orEmpty()) ?: return@commandSink
   bridge.send(BridgeProtocol.command(key, command))
  }
  session = MediaSession.Builder(this, remotePlayer).build()
  bridge.start()
 }

 override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session
 override fun onDestroy() { bridge.stop(); session?.release(); remotePlayer.release(); session=null; super.onDestroy() }
}

private class RemoteWindowsPlayer : SimpleBasePlayer(android.os.Looper.getMainLooper()) {
 var commandSink: (String) -> Unit = {}
 private var media: RemoteMediaState? = null
 private var connected = false

 fun update(value: RemoteMediaState) { android.os.Handler(applicationLooper).post { media=value; invalidateState() } }
 fun setConnected(value: Boolean) { android.os.Handler(applicationLooper).post { connected=value; invalidateState() } }

 override fun getState(): State {
  val m=media
  val metadata=MediaMetadata.Builder().setTitle(m?.title ?: "Windows").setArtist(m?.artist).setAlbumTitle(m?.album)
   .apply { m?.artworkBase64?.let { setArtworkUri(Uri.parse("data:image/jpeg;base64,$it")) } }.build()
  val item=MediaItem.Builder().setMediaId("windows-current").setMediaMetadata(metadata).build()
  return State.Builder()
   .setAvailableCommands(Player.Commands.Builder().addAll(Player.COMMAND_PLAY_PAUSE,Player.COMMAND_SEEK_TO_NEXT,Player.COMMAND_SEEK_TO_PREVIOUS).build())
   .setPlaylist(listOf(MediaItemData.Builder("windows-current").setMediaItem(item).setDurationUs((m?.durationMs ?: 0)*1000).build()))
   .setCurrentMediaItemIndex(0).setContentPositionMs(m?.positionMs ?: 0)
   .setPlayWhenReady(m?.playing == true, Player.PLAY_WHEN_READY_CHANGE_REASON_REMOTE)
   .setPlaybackState(if (connected && m != null) Player.STATE_READY else Player.STATE_IDLE)
   .build()
 }
 override fun handleSetPlayWhenReady(playWhenReady: Boolean): ListenableFuture<*> { commandSink(if(playWhenReady) "play" else "pause"); return Futures.immediateVoidFuture() }
 override fun handleSeek(mediaItemIndex: Int, positionMs: Long, seekCommand: Int): ListenableFuture<*> { commandSink(if(seekCommand==Player.COMMAND_SEEK_TO_NEXT) "next" else if(seekCommand==Player.COMMAND_SEEK_TO_PREVIOUS) "previous" else "toggle"); return Futures.immediateVoidFuture() }
}
