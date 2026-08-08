using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Bridge.Protocol;

[Flags]
public enum BridgeFeature { None=0, Media=1, Volume=2, Clipboard=4, PcStatus=8 }

public enum BridgeMessageType { Hello, State, Command, Ping, Pong, Error }
public enum MediaCommand { Play, Pause, Toggle, Next, Previous, Seek }

public sealed record MediaState(string? Title, string? Artist, string? Album, string? SourceApp, bool IsPlaying, long PositionMs, long DurationMs, string? ArtworkBase64=null);
public sealed record PcState(double MasterVolume, bool Muted, double CpuPercent, double MemoryPercent, string? ClipboardText=null);
public sealed record CommandPayload(MediaCommand? Media=null, long? SeekMs=null, double? Volume=null, bool? Muted=null, string? ClipboardText=null);
public sealed record BridgePayload(BridgeFeature EnabledFeatures, MediaState? Media=null, PcState? Pc=null, CommandPayload? Command=null);
public sealed record SignedEnvelope(string Version, BridgeMessageType Type, long TimestampUnixMs, string Nonce, BridgePayload Payload, string Signature);

public static class BridgeCodec
{
    public const string ProtocolVersion = "1";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SignedEnvelope Sign(BridgeMessageType type, BridgePayload payload, ReadOnlySpan<byte> key, long? timestamp=null, string? nonce=null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var n = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var unsigned = Canonical(ProtocolVersion, type, ts, n, payload);
        var signature = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(unsigned)));
        return new(ProtocolVersion, type, ts, n, payload, signature);
    }

    public static bool Verify(SignedEnvelope envelope, ReadOnlySpan<byte> key, TimeSpan maxAge)
    {
        if (envelope.Version != ProtocolVersion) return false;
        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - envelope.TimestampUnixMs);
        if (age > maxAge.TotalMilliseconds) return false;
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(Canonical(envelope.Version, envelope.Type, envelope.TimestampUnixMs, envelope.Nonce, envelope.Payload)));
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(envelope.Signature)); }
        catch (FormatException) { return false; }
    }

    public static string Serialize(SignedEnvelope envelope) => JsonSerializer.Serialize(envelope, JsonOptions);
    public static SignedEnvelope? Deserialize(string json) => JsonSerializer.Deserialize<SignedEnvelope>(json, JsonOptions);
    public static byte[] NewPairingKey() => RandomNumberGenerator.GetBytes(32);
    private static string Canonical(string version, BridgeMessageType type, long ts, string nonce, BridgePayload payload) => $"{version}\n{type}\n{ts}\n{nonce}\n{JsonSerializer.Serialize(payload, JsonOptions)}";
}
