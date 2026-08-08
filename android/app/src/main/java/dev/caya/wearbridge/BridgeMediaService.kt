package dev.caya.wearbridge

import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.session.MediaSession
import androidx.media3.session.MediaSessionService

/**
 * Owns the Android MediaSession exposed to system UI and Wear OS.
 * Transport synchronization is deliberately isolated from this service so Bluetooth and LAN can fail over.
 */
class BridgeMediaService : MediaSessionService() {
 private var session: MediaSession? = null
 override fun onCreate() { super.onCreate(); val player=ExoPlayer.Builder(this).build(); session=MediaSession.Builder(this,player).build() }
 override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaSession? = session
 override fun onDestroy() { session?.run { player.release(); release() }; session=null; super.onDestroy() }
}
