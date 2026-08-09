package dev.caya.wearbridge

import android.util.Base64
import org.json.JSONObject
import java.nio.charset.StandardCharsets
import java.util.UUID
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

data class RemoteMediaState(
    val title: String?, val artist: String?, val album: String?, val sourceApp: String?,
    val playing: Boolean, val positionMs: Long, val durationMs: Long,
    val artworkBase64: String?, val artworkId: String?
)
data class RemotePcState(
    val volume: Double,
    val muted: Boolean,
    val cpuPercent: Double,
    val memoryPercent: Double,
    val clipboardText: String?,
    val batteryPercent: Int?,
    val batteryCharging: Boolean?,
    val onAcPower: Boolean?
)
data class RemoteBridgeState(val media: RemoteMediaState?, val pc: RemotePcState?)

/** Why an inbound frame was refused. Logged so a dead link says which check failed. */
enum class RejectReason { NONE, MALFORMED, VERSION, STALE, SIGNATURE }

object BridgeProtocol {
    /**
     * Must match [Bridge.Protocol.BridgeCodec.ProtocolVersion]. v2 signs the payload text exactly as
     * transmitted; v1 signed a re-serialization of the parsed payload, which never matched across
     * runtimes (System.Text.Json vs org.json differ on flag enums and on escaping non-ASCII and '+').
     */
    const val VERSION = "2"

    /** Clock skew tolerated between watch and PC. Matches BridgeCodec.MaxClockSkew. */
    const val MAX_SKEW_MS = 5 * 60 * 1000L

    fun enabledFeatures(prefs: android.content.SharedPreferences): Int {
        var value = 0
        if (prefs.getBoolean("feature_media", true)) value = value or 1
        if (prefs.getBoolean("feature_volume", true)) value = value or 2
        if (prefs.getBoolean("feature_clipboard", true)) value = value or 4
        if (prefs.getBoolean("feature_system", true)) value = value or 8
        return value
    }

    fun command(key: ByteArray, mediaCommand: String, enabledFeatures: Int, seekMs: Long? = null): String {
        val payload = JSONObject().apply {
            put("enabledFeatures", enabledFeatures)
            put("command", JSONObject().apply {
                put("media", mediaCommand)
                if (seekMs != null) put("seekMs", seekMs)
            })
        }
        return sign("command", payload, key)
    }

    fun volumeCommand(key: ByteArray, enabledFeatures: Int, volume: Double?, muted: Boolean?): String {
        val payload = JSONObject().apply {
            put("enabledFeatures", enabledFeatures)
            put("command", JSONObject().apply {
                if (volume != null) put("volume", volume)
                if (muted != null) put("muted", muted)
            })
        }
        return sign("command", payload, key)
    }

    fun ping(key: ByteArray, enabledFeatures: Int): String =
        sign("ping", JSONObject().put("enabledFeatures", enabledFeatures), key)

    /** Parses and authenticates a frame. Returns null and sets [reason] when the frame is refused. */
    fun verifyAndReadState(
        line: String,
        key: ByteArray,
        now: Long = System.currentTimeMillis(),
        reason: (RejectReason) -> Unit = {}
    ): RemoteBridgeState? {
        val root = try { JSONObject(line) } catch (_: Exception) { reason(RejectReason.MALFORMED); return null }
        if (root.optString("version") != VERSION) { reason(RejectReason.VERSION); return null }
        val ts = root.optLong("timestampUnixMs")
        if (kotlin.math.abs(now - ts) > MAX_SKEW_MS) { reason(RejectReason.STALE); return null }
        val type = root.optString("type")
        val nonce = root.optString("nonce")
        // The payload travels as a JSON string. Hash that string verbatim - re-serializing a parsed
        // JSONObject would reintroduce the escaping mismatch that broke v1.
        val payloadText = if (root.isNull("payload")) "" else root.optString("payload")
        if (payloadText.isEmpty()) { reason(RejectReason.MALFORMED); return null }
        val canonical = "$VERSION\n$type\n$ts\n$nonce\n$payloadText"
        if (!constantTimeEquals(hmacHex(key, canonical), root.optString("signature"))) {
            reason(RejectReason.SIGNATURE); return null
        }
        val payload = try { JSONObject(payloadText) } catch (_: Exception) { reason(RejectReason.MALFORMED); return null }
        val mediaJson = payload.optJSONObject("media")
        val media = mediaJson?.let {
            RemoteMediaState(
                it.optNullableString("title"), it.optNullableString("artist"), it.optNullableString("album"),
                it.optNullableString("sourceApp"), it.optBoolean("isPlaying"), it.optLong("positionMs"),
                it.optLong("durationMs"), it.optNullableString("artworkBase64"), it.optNullableString("artworkId")
            )
        }
        val pcJson = payload.optJSONObject("pc")
        val pc = pcJson?.let {
            RemotePcState(
                it.optDouble("masterVolume", 0.0), it.optBoolean("muted"),
                it.optDouble("cpuPercent", 0.0), it.optDouble("memoryPercent", 0.0),
                it.optNullableString("clipboardText"), it.optNullableInt("batteryPercent"),
                it.optNullableBoolean("batteryCharging"), it.optNullableBoolean("onAcPower")
            )
        }
        reason(RejectReason.NONE)
        return RemoteBridgeState(media, pc)
    }

    fun decodeKey(text: String): ByteArray? = try {
        Base64.decode(text.trim(), Base64.DEFAULT).takeIf { it.size == 32 }
    } catch (_: IllegalArgumentException) { null }

    private fun sign(type: String, payload: JSONObject, key: ByteArray): String =
        frame(type, payload.toString(), key, System.currentTimeMillis(), UUID.randomUUID().toString().replace("-", ""))

    /** The exact string covered by the signature. Mirrors BridgeCodec.Canonical on Windows. */
    internal fun canonical(type: String, ts: Long, nonce: String, payloadText: String) =
        "$VERSION\n$type\n$ts\n$nonce\n$payloadText"

    internal fun signatureFor(type: String, payloadText: String, key: ByteArray, ts: Long, nonce: String) =
        hmacHex(key, canonical(type, ts, nonce, payloadText))

    /** Builds a wire frame around an already-serialized payload, which is transmitted verbatim. */
    internal fun frame(type: String, payloadText: String, key: ByteArray, ts: Long, nonce: String): String =
        JSONObject().apply {
            put("version", VERSION); put("type", type); put("timestampUnixMs", ts); put("nonce", nonce)
            put("payload", payloadText); put("signature", signatureFor(type, payloadText, key, ts, nonce))
        }.toString()

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

    private fun JSONObject.optNullableString(name: String): String? =
        if (!has(name) || isNull(name)) null else optString(name)

    private fun JSONObject.optNullableInt(name: String): Int? =
        if (!has(name) || isNull(name)) null else optInt(name)

    private fun JSONObject.optNullableBoolean(name: String): Boolean? =
        if (!has(name) || isNull(name)) null else optBoolean(name)
}
