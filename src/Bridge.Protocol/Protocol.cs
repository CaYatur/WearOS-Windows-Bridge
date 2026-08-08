using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bridge.Protocol;

[Flags]
public enum BridgeFeature { None=0, Media=1, Volume=2, Clipboard=4, PcStatus=8 }

public enum BridgeMessageType { Hello, State, Command, Ping, Pong, Error }
public enum MediaCommand { Play, Pause, Toggle, Next, Previous, Seek }

/// <summary>Why a receiver refused a frame. Reported so a dead link says which check failed.</summary>
public enum BridgeRejectReason { None, MalformedJson, VersionMismatch, StaleTimestamp, BadSignature, ReplayedNonce }

/// <param name="ArtworkId">
/// Stable id (content hash) of the current artwork. Artwork bytes are large, so
/// <see cref="ArtworkBase64"/> is sent only when the id changes; a receiver that already
/// holds this id keeps the image it has.
/// </param>
public sealed record MediaState(string? Title, string? Artist, string? Album, string? SourceApp, bool IsPlaying, long PositionMs, long DurationMs, string? ArtworkBase64=null, string? ArtworkId=null);
public sealed record PcState(double MasterVolume, bool Muted, double CpuPercent, double MemoryPercent, string? ClipboardText=null);
public sealed record CommandPayload(MediaCommand? Media=null, long? SeekMs=null, double? Volume=null, bool? Muted=null, string? ClipboardText=null);

public sealed record BridgePayload(
    [property: JsonConverter(typeof(BridgeFeatureNumberConverter))] BridgeFeature EnabledFeatures,
    MediaState? Media=null, PcState? Pc=null, CommandPayload? Command=null);

/// <summary>
/// A signed frame. <see cref="PayloadJson"/> is the authoritative payload: it is the exact text
/// that travels on the wire and the exact text the signature covers.
/// </summary>
public sealed record SignedEnvelope(
    string Version, BridgeMessageType Type, long TimestampUnixMs, string Nonce,
    [property: JsonPropertyName("payload")] string PayloadJson, string Signature)
{
    /// <summary>Parses the payload. The raw text stays authoritative — never re-serialize this to verify.</summary>
    public BridgePayload ReadPayload()
    {
        try { return JsonSerializer.Deserialize<BridgePayload>(PayloadJson, BridgeCodec.JsonOptions) ?? new(BridgeFeature.None); }
        catch (JsonException) { return new(BridgeFeature.None); }
    }
}

public static class BridgeCodec
{
    // v2 changed how the signature is computed; see Canonical below. A v1 peer now fails with a
    // clean version mismatch instead of an unexplainable signature error.
    public const string ProtocolVersion = "2";

    /// <summary>Clock skew allowed between the watch and the PC before a frame counts as stale.</summary>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    public static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // Relaxed escaping keeps non-ASCII (e.g. Turkish titles) and '+' literal. Nothing here is
            // embedded in HTML, and it keeps frames small. Correctness no longer depends on it: the
            // signature covers the transmitted bytes verbatim.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    public static SignedEnvelope Sign(BridgeMessageType type, BridgePayload payload, ReadOnlySpan<byte> key, long? timestamp=null, string? nonce=null)
        => SignRaw(type, JsonSerializer.Serialize(payload, JsonOptions), key, timestamp, nonce);

    /// <summary>Signs an already-serialized payload. The given text is transmitted byte-for-byte.</summary>
    public static SignedEnvelope SignRaw(BridgeMessageType type, string payloadJson, ReadOnlySpan<byte> key, long? timestamp=null, string? nonce=null)
    {
        var ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var n = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var signature = Hmac(key, Canonical(ProtocolVersion, type, ts, n, payloadJson));
        return new(ProtocolVersion, type, ts, n, payloadJson, signature);
    }

    public static bool Verify(SignedEnvelope envelope, ReadOnlySpan<byte> key, TimeSpan maxAge)
        => Verify(envelope, key, maxAge, out _);

    public static bool Verify(SignedEnvelope envelope, ReadOnlySpan<byte> key, TimeSpan maxAge, out BridgeRejectReason reason)
    {
        if (envelope.Version != ProtocolVersion) { reason = BridgeRejectReason.VersionMismatch; return false; }
        var age = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - envelope.TimestampUnixMs);
        if (age > maxAge.TotalMilliseconds) { reason = BridgeRejectReason.StaleTimestamp; return false; }
        var expected = Convert.FromHexString(Hmac(key, Canonical(envelope.Version, envelope.Type, envelope.TimestampUnixMs, envelope.Nonce, envelope.PayloadJson)));
        bool ok;
        try { ok = CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(envelope.Signature)); }
        catch (FormatException) { ok = false; }
        reason = ok ? BridgeRejectReason.None : BridgeRejectReason.BadSignature;
        return ok;
    }

    public static string Serialize(SignedEnvelope envelope) => JsonSerializer.Serialize(envelope, JsonOptions);

    public static SignedEnvelope? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<SignedEnvelope>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static byte[] NewPairingKey() => RandomNumberGenerator.GetBytes(32);

    private static string Hmac(ReadOnlySpan<byte> key, string text)
        => Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// The signed string. The payload is appended as the exact text that is transmitted — it is
    /// never re-serialized from a parsed object.
    ///
    /// v1 hashed a re-serialization of the parsed payload on both ends, which only appeared to work
    /// because C# talked to C# in tests. On the wire the two runtimes disagree: System.Text.Json
    /// writes a [Flags] enum as "media, volume" where org.json writes 15, and escapes non-ASCII and
    /// '+' into backslash-u form where org.json writes them literally. Every frame in both
    /// directions failed its signature check. Hash the bytes you send; never re-serialize.
    /// </summary>
    private static string Canonical(string version, BridgeMessageType type, long ts, string nonce, string payloadJson)
        => $"{version}\n{TypeName(type)}\n{ts}\n{nonce}\n{payloadJson}";

    /// <summary>The camelCase name written to JSON ("ping", "state"), not Enum.ToString()'s PascalCase.</summary>
    internal static string TypeName(BridgeMessageType type) => JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}

/// <summary>
/// Writes <see cref="BridgeFeature"/> as a plain integer bitmask. The shared
/// <see cref="JsonStringEnumConverter"/> would render it as "media, volume", which the Android
/// client neither writes nor reads.
/// </summary>
public sealed class BridgeFeatureNumberConverter : JsonConverter<BridgeFeature>
{
    public override BridgeFeature Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.Number => (BridgeFeature)reader.GetInt32(),
            JsonTokenType.String when int.TryParse(reader.GetString(), out var parsed) => (BridgeFeature)parsed,
            _ => BridgeFeature.None
        };

    public override void Write(Utf8JsonWriter writer, BridgeFeature value, JsonSerializerOptions options)
        => writer.WriteNumberValue((int)value);
}
