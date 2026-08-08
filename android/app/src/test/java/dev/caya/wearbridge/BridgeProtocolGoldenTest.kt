package dev.caya.wearbridge

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Kotlin half of the shared wire vectors. The C# half is
 * tests/Bridge.Protocol.Tests/ProtocolTests.cs (GoldenVectorTests) and carries the same constants.
 *
 * Both sides are checked against these fixed strings, never against each other. A round-trip test
 * passes even when the two runtimes disagree, which is how the v1 signature bug went unnoticed:
 * System.Text.Json and org.json render flag enums and non-ASCII escapes differently, so every frame
 * failed its HMAC check in both directions.
 *
 * Frames are compared by signature and by parsed content rather than by whole-string equality:
 * org.json orders object keys by hash on the JVM and by insertion on Android, so byte-identical
 * envelope text is not a property worth asserting. It is also not one the protocol needs — each
 * side hashes the text it actually sends.
 */
class BridgeProtocolGoldenTest {
    private val key = ByteArray(32) { it.toByte() }
    private val ts = 1735689600000L
    private val nonce = "0123456789ABCDEF0123456789ABCDEF"

    private val statePayload =
        """{"enabledFeatures":15,"media":{"title":"Şarkı Çöl Ağı","artist":"Gülşen","sourceApp":"Spotify.exe","isPlaying":true,"positionMs":61000,"durationMs":245000,"artworkBase64":"iVBORw0KGgo+AB/CD==","artworkId":"a1b2c3"},"pc":{"masterVolume":0.42,"muted":false,"cpuPercent":12.5,"memoryPercent":63.25,"clipboardText":"pano"}}"""

    /** Byte-for-byte what Windows puts on the wire. */
    private val stateWire =
        """{"version":"2","type":"state","timestampUnixMs":1735689600000,"nonce":"0123456789ABCDEF0123456789ABCDEF","payload":"{\"enabledFeatures\":15,\"media\":{\"title\":\"Şarkı Çöl Ağı\",\"artist\":\"Gülşen\",\"sourceApp\":\"Spotify.exe\",\"isPlaying\":true,\"positionMs\":61000,\"durationMs\":245000,\"artworkBase64\":\"iVBORw0KGgo+AB/CD==\",\"artworkId\":\"a1b2c3\"},\"pc\":{\"masterVolume\":0.42,\"muted\":false,\"cpuPercent\":12.5,\"memoryPercent\":63.25,\"clipboardText\":\"pano\"}}","signature":"CB4ADAC089AADE3B11F8B022A4080DD7A3082E0CEC9CE550AD57292A7D372A8E"}"""

    private val pingPayload = """{"enabledFeatures":15}"""
    private val pingSignature = "EBECD41C6DD8B2FBF9DF166EA23E7AA9F604584A68EC053AF3604E9B9340C81E"
    private val commandPayload = """{"enabledFeatures":15,"command":{"media":"play"}}"""
    private val commandSignature = "4EF992F6565EEC25C92CDB25B406ABE78312014993FB9F44AEDFD9CCD27EDC93"

    @Test fun `accepts the windows state vector including turkish text and plus in base64`() {
        var reason = RejectReason.MALFORMED
        val state = BridgeProtocol.verifyAndReadState(stateWire, key, now = ts) { reason = it }
        assertEquals(RejectReason.NONE, reason)
        assertNotNull(state)
        val decoded = state!!
        val media = decoded.media!!
        assertEquals("Şarkı Çöl Ağı", media.title)
        assertEquals("Gülşen", media.artist)
        assertEquals("iVBORw0KGgo+AB/CD==", media.artworkBase64)
        assertEquals("a1b2c3", media.artworkId)
        assertTrue(media.playing)
        assertEquals(61000L, media.positionMs)
        val pc = decoded.pc!!
        assertEquals(0.42, pc.volume, 1e-9)
        assertEquals("pano", pc.clipboardText)
    }

    @Test fun `produces the signature windows expects for a ping`() =
        assertEquals(pingSignature, BridgeProtocol.signatureFor("ping", pingPayload, key, ts, nonce))

    @Test fun `produces the signature windows expects for a command`() =
        assertEquals(commandSignature, BridgeProtocol.signatureFor("command", commandPayload, key, ts, nonce))

    @Test fun `canonical string has the agreed shape`() =
        assertEquals("2\nping\n$ts\n$nonce\n$pingPayload", BridgeProtocol.canonical("ping", ts, nonce, pingPayload))

    @Test fun `ping frame carries the payload verbatim and signs what it carries`() {
        val root = JSONObject(BridgeProtocol.frame("ping", pingPayload, key, ts, nonce))
        assertEquals("2", root.getString("version"))
        assertEquals("ping", root.getString("type"))
        assertEquals(pingPayload, root.getString("payload"))
        assertEquals(pingSignature, root.getString("signature"))
    }

    @Test fun `command frame carries the payload verbatim and signs what it carries`() {
        val root = JSONObject(BridgeProtocol.frame("command", commandPayload, key, ts, nonce))
        assertEquals(commandPayload, root.getString("payload"))
        assertEquals(commandSignature, root.getString("signature"))
        assertEquals("play", JSONObject(root.getString("payload")).getJSONObject("command").getString("media"))
    }

    @Test fun `tampered payload is rejected`() {
        val tampered = stateWire.replace("Gülşen", "Mallory")
        var reason = RejectReason.NONE
        assertNull(BridgeProtocol.verifyAndReadState(tampered, key, now = ts) { reason = it })
        assertEquals(RejectReason.SIGNATURE, reason)
    }

    @Test fun `v1 frame is rejected on version rather than signature`() {
        val old = stateWire.replace("\"version\":\"2\"", "\"version\":\"1\"")
        var reason = RejectReason.NONE
        assertNull(BridgeProtocol.verifyAndReadState(old, key, now = ts) { reason = it })
        assertEquals(RejectReason.VERSION, reason)
    }

    @Test fun `frame outside the skew window is rejected`() {
        var reason = RejectReason.NONE
        assertNull(BridgeProtocol.verifyAndReadState(stateWire, key, now = ts + BridgeProtocol.MAX_SKEW_MS + 1) { reason = it })
        assertEquals(RejectReason.STALE, reason)
    }

    @Test fun `wrong key is rejected`() {
        var reason = RejectReason.NONE
        assertNull(BridgeProtocol.verifyAndReadState(stateWire, ByteArray(32) { 9 }, now = ts) { reason = it })
        assertEquals(RejectReason.SIGNATURE, reason)
    }

    @Test fun `garbage input is rejected without throwing`() {
        var reason = RejectReason.NONE
        assertNull(BridgeProtocol.verifyAndReadState("{not json", key, now = ts) { reason = it })
        assertEquals(RejectReason.MALFORMED, reason)
    }
}
