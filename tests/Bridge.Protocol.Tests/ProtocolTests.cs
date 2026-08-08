using System.Text.Json;
using Bridge.Protocol;
using Xunit;

namespace Bridge.Protocol.Tests;

public class ProtocolTests
{
    [Fact] public void Signed_message_verifies_and_roundtrips()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.State,new BridgePayload(BridgeFeature.Media,new MediaState("Song","Artist",null,"Test",true,100,1000)),key);
        var decoded=BridgeCodec.Deserialize(BridgeCodec.Serialize(env));
        Assert.NotNull(decoded); Assert.True(BridgeCodec.Verify(decoded!,key,TimeSpan.FromMinutes(1))); Assert.Equal("Song",decoded!.ReadPayload().Media!.Title);
    }

    [Fact] public void Tampered_payload_is_rejected()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.Ping,new BridgePayload(BridgeFeature.Media),key);
        var tampered=env with { PayloadJson=JsonSerializer.Serialize(new BridgePayload(BridgeFeature.Clipboard),BridgeCodec.JsonOptions) };
        Assert.False(BridgeCodec.Verify(tampered,key,TimeSpan.FromMinutes(1),out var reason));
        Assert.Equal(BridgeRejectReason.BadSignature,reason);
    }

    [Fact] public void Old_message_is_rejected()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.Ping,new BridgePayload(BridgeFeature.Media),key,DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds());
        Assert.False(BridgeCodec.Verify(env,key,BridgeCodec.MaxClockSkew,out var reason));
        Assert.Equal(BridgeRejectReason.StaleTimestamp,reason);
    }

    [Fact] public void Wrong_version_is_rejected_before_signature()
    {
        var key=BridgeCodec.NewPairingKey();
        var env=BridgeCodec.Sign(BridgeMessageType.Ping,new BridgePayload(BridgeFeature.Media),key) with { Version="1" };
        Assert.False(BridgeCodec.Verify(env,key,BridgeCodec.MaxClockSkew,out var reason));
        Assert.Equal(BridgeRejectReason.VersionMismatch,reason);
    }

    [Fact] public void Malformed_json_deserializes_to_null()
        => Assert.Null(BridgeCodec.Deserialize("{not json"));

    [Fact] public void Feature_flags_travel_as_an_integer_bitmask()
    {
        // Android writes and reads a plain int here. The shared string-enum converter would emit
        // "media, volume" instead, which was the v1 bug that broke every watch-to-PC frame.
        var json=JsonSerializer.Serialize(new BridgePayload(BridgeFeature.Media|BridgeFeature.Volume),BridgeCodec.JsonOptions);
        Assert.Equal("{\"enabledFeatures\":3}",json);
        Assert.Equal(BridgeFeature.Media|BridgeFeature.Volume,JsonSerializer.Deserialize<BridgePayload>("{\"enabledFeatures\":3}",BridgeCodec.JsonOptions)!.EnabledFeatures);
    }
}

/// <summary>
/// Byte-exact wire vectors shared with the Android client. Both runtimes are checked against these
/// fixed strings, never against each other — a round-trip test passes even when the two sides
/// disagree, which is exactly how the v1 signature bug survived.
///
/// The Kotlin mirror is android/app/src/test/java/dev/caya/wearbridge/BridgeProtocolGoldenTest.kt.
/// Changing a vector here means changing it there in the same commit.
/// </summary>
public class GoldenVectorTests
{
    private static readonly byte[] Key = Enumerable.Range(0,32).Select(i=>(byte)i).ToArray();
    public const string KeyBase64 = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    public const long Timestamp = 1735689600000;
    public const string Nonce = "0123456789ABCDEF0123456789ABCDEF";
    private static readonly TimeSpan IgnoreAge = TimeSpan.FromDays(36500);

    // Windows -> watch. Turkish characters and a '+' inside base64 are the two cases the v1
    // canonicalization escaped differently on each side.
    public const string StatePayload =
        """{"enabledFeatures":15,"media":{"title":"Şarkı Çöl Ağı","artist":"Gülşen","sourceApp":"Spotify.exe","isPlaying":true,"positionMs":61000,"durationMs":245000,"artworkBase64":"iVBORw0KGgo+AB/CD==","artworkId":"a1b2c3"},"pc":{"masterVolume":0.42,"muted":false,"cpuPercent":12.5,"memoryPercent":63.25,"clipboardText":"pano"}}""";
    public const string StateWire =
        """{"version":"2","type":"state","timestampUnixMs":1735689600000,"nonce":"0123456789ABCDEF0123456789ABCDEF","payload":"{\"enabledFeatures\":15,\"media\":{\"title\":\"Şarkı Çöl Ağı\",\"artist\":\"Gülşen\",\"sourceApp\":\"Spotify.exe\",\"isPlaying\":true,\"positionMs\":61000,\"durationMs\":245000,\"artworkBase64\":\"iVBORw0KGgo+AB/CD==\",\"artworkId\":\"a1b2c3\"},\"pc\":{\"masterVolume\":0.42,\"muted\":false,\"cpuPercent\":12.5,\"memoryPercent\":63.25,\"clipboardText\":\"pano\"}}","signature":"CB4ADAC089AADE3B11F8B022A4080DD7A3082E0CEC9CE550AD57292A7D372A8E"}""";

    // watch -> Windows, written exactly as org.json renders these objects.
    public const string PingWire =
        """{"version":"2","type":"ping","timestampUnixMs":1735689600000,"nonce":"0123456789ABCDEF0123456789ABCDEF","payload":"{\"enabledFeatures\":15}","signature":"EBECD41C6DD8B2FBF9DF166EA23E7AA9F604584A68EC053AF3604E9B9340C81E"}""";
    public const string CommandWire =
        """{"version":"2","type":"command","timestampUnixMs":1735689600000,"nonce":"0123456789ABCDEF0123456789ABCDEF","payload":"{\"enabledFeatures\":15,\"command\":{\"media\":\"play\"}}","signature":"4EF992F6565EEC25C92CDB25B406ABE78312014993FB9F44AEDFD9CCD27EDC93"}""";

    [Fact] public void Key_vector_matches_the_kotlin_mirror()
        => Assert.Equal(KeyBase64, Convert.ToBase64String(Key));

    [Fact] public void Windows_produces_the_exact_state_vector()
    {
        var env = BridgeCodec.SignRaw(BridgeMessageType.State, StatePayload, Key, Timestamp, Nonce);
        Assert.Equal(StateWire, BridgeCodec.Serialize(env));
    }

    [Fact] public void Windows_verifies_its_own_state_vector()
    {
        var env = BridgeCodec.Deserialize(StateWire);
        Assert.NotNull(env);
        Assert.True(BridgeCodec.Verify(env!, Key, IgnoreAge, out var reason), $"rejected: {reason}");
        var media = env!.ReadPayload().Media!;
        Assert.Equal("Şarkı Çöl Ağı", media.Title);
        Assert.Equal("iVBORw0KGgo+AB/CD==", media.ArtworkBase64);
    }

    [Fact] public void Windows_accepts_the_watch_ping_vector()
    {
        var env = BridgeCodec.Deserialize(PingWire);
        Assert.NotNull(env);
        Assert.True(BridgeCodec.Verify(env!, Key, IgnoreAge, out var reason), $"rejected: {reason}");
        Assert.Equal(BridgeFeature.Media|BridgeFeature.Volume|BridgeFeature.Clipboard|BridgeFeature.PcStatus, env!.ReadPayload().EnabledFeatures);
    }

    [Fact] public void Windows_accepts_the_watch_command_vector()
    {
        var env = BridgeCodec.Deserialize(CommandWire);
        Assert.NotNull(env);
        Assert.True(BridgeCodec.Verify(env!, Key, IgnoreAge, out var reason), $"rejected: {reason}");
        Assert.Equal(MediaCommand.Play, env!.ReadPayload().Command!.Media);
    }

    [Fact] public void Payload_is_hashed_verbatim_not_reserialized()
    {
        // Same payload, two spellings that any JSON parser treats as equal. They must produce
        // different signatures: the signature covers transmitted bytes, not parsed meaning.
        var spaced = BridgeCodec.SignRaw(BridgeMessageType.Ping, "{\"enabledFeatures\": 15}", Key, Timestamp, Nonce);
        var tight  = BridgeCodec.SignRaw(BridgeMessageType.Ping, "{\"enabledFeatures\":15}",  Key, Timestamp, Nonce);
        Assert.NotEqual(spaced.Signature, tight.Signature);
        // ...and each still verifies as sent.
        Assert.True(BridgeCodec.Verify(spaced, Key, IgnoreAge));
        Assert.True(BridgeCodec.Verify(tight, Key, IgnoreAge));
    }
}
