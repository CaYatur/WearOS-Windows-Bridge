package dev.caya.wearbridge

import android.util.Base64
import org.json.JSONObject
import java.nio.charset.StandardCharsets
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

data class RemoteMediaState(
    val title: String?, val artist: String?, val album: String?, val sourceApp: String?,
    val playing: Boolean, val positionMs: Long, val durationMs: Long, val artworkBase64: String?
)
data class RemotePcState(val volume: Double, val muted: Boolean, val cpuPercent: Double, val memoryPercent: Double, val clipboardText: String?)
data class RemoteBridgeState(val media: RemoteMediaState?, val pc: RemotePcState?)

object BridgeProtocol {
    const val VERSION = "1"

    fun enabledFeatures(prefs: android.content.SharedPreferences): Int {
        var value = 0
        if (prefs.getBoolean("media", true)) value = value or 1
        if (prefs.getBoolean("volume", false)) value = value or 2
        if (prefs.getBoolean("clipboard", false)) value = value or 4
        if (prefs.getBoolean("status", true)) value = value or 8
        return value
    }

    fun command(key: ByteArray, mediaCommand: String, seekMs: Long? = null): String {
        val payload = JSONObject().apply {
            put("enabledFeatures", 1)
            put("command", JSONObject().apply {
                put("media", mediaCommand)
                if (seekMs != null) put("seekMs", seekMs)
            })
        }
        return sign("command", payload, key)
    }

    fun ping(key: ByteArray, enabledFeatures: Int): String = sign(
        "ping", JSONObject().put("enabledFeatures", enabledFeatures), key
    )

    fun verifyAndReadState(line: String, key: ByteArray): RemoteBridgeState? {
        val root = JSONObject(line)
        if (root.optString("version") != VERSION) return null
        val ts = root.optLong("timestampUnixMs")
        if (kotlin.math.abs(System.currentTimeMillis() - ts) > 120_000L) return null
        val type = root.optString("type")
        val nonce = root.optString("nonce")
        val payload = root.getJSONObject("payload")
        val canonical = "$VERSION\n$type\n$ts\n$nonce\n${payload}"
        if (!constantTimeEquals(hmacHex(key, canonical), root.optString("signature"))) return null
        val mediaJson = payload.optJSONObject("media")
        val media = mediaJson?.let {
            RemoteMediaState(it.optNullableString("title"), it.optNullableString("artist"), it.optNullableString("album"),
                it.optNullableString("sourceApp"), it.optBoolean("isPlaying"), it.optLong("positionMs"),
                it.optLong("durationMs"), it.optNullableString("artworkBase64"))
        }
        val pcJson = payload.optJSONObject("pc")
        val pc = pcJson?.let { RemotePcState(it.optDouble("masterVolume"), it.optBoolean("muted"), it.optDouble("cpuPercent"), it.optDouble("memoryPercent"), it.optNullableString("clipboardText")) }
        return RemoteBridgeState(media, pc)
    }

    fun decodeKey(text: String): ByteArray? = try {
        Base64.decode(text.trim(), Base64.DEFAULT).takeIf { it.size == 32 }
    } catch (_: IllegalArgumentException) { null }

    private fun sign(type: String, payload: JSONObject, key: ByteArray): String {
        val ts = System.currentTimeMillis()
        val nonce = java.util.UUID.randomUUID().toString().replace("-", "")
        val canonical = "$VERSION\n$type\n$ts\n$nonce\n${payload}"
        return JSONObject().apply {
            put("version", VERSION); put("type", type); put("timestampUnixMs", ts); put("nonce", nonce)
            put("payload", payload); put("signature", hmacHex(key, canonical))
        }.toString()
    }

    private fun hmacHex(key: ByteArray, text: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal(text.toByteArray(StandardCharsets.UTF_8)).joinToString("") { "%02X".format(it) }
    }

    private fun constantTimeEquals(a: String, b: String): Boolean {
        if (a.length != b.length) return false
        var diff = 0
        for (i in a.indices) diff = diff or (a[i].code xor b[i].code)
        return diff == 0
    }

    private fun JSONObject.optNullableString(name: String): String? = if (isNull(name) || !has(name)) null else optString(name)
}
