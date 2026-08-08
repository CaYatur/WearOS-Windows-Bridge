using System.Collections.Concurrent;
using System.Text;
using Bridge.Protocol;

namespace Bridge.Windows;

public sealed class BridgeConnectionHandler(WindowsMediaBridge media, WindowsFeatures features, byte[] key)
{
    private readonly ConcurrentDictionary<string,long> _nonces = new();

    public async Task HandleAsync(Stream stream, CancellationToken ct = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = true };
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct); if (line is null) break;
            SignedEnvelope? request;
            try { request = BridgeCodec.Deserialize(line); } catch { continue; }
            if (request is null || !BridgeCodec.Verify(request, key, TimeSpan.FromMinutes(2)) || !AcceptNonce(request)) continue;
            await ApplyCommandAsync(request.Payload);
            var response = await BuildStateAsync(request.Payload.EnabledFeatures);
            await writer.WriteLineAsync(BridgeCodec.Serialize(response));
        }
    }

    private bool AcceptNonce(SignedEnvelope e)
    {
        var cutoff = DateTimeOffset.UtcNow.AddMinutes(-3).ToUnixTimeMilliseconds();
        foreach (var old in _nonces.Where(x => x.Value < cutoff).Select(x => x.Key).Take(100)) _nonces.TryRemove(old, out _);
        return _nonces.TryAdd(e.Nonce, e.TimestampUnixMs);
    }

    private async Task ApplyCommandAsync(BridgePayload payload)
    {
        var command = payload.Command; if (command is null) return;
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Media)) await media.ExecuteAsync(command);
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Volume)) features.SetAudio(command.Volume, command.Muted);
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Clipboard) && command.ClipboardText is { } text) features.SetClipboardText(text);
    }

    private async Task<SignedEnvelope> BuildStateAsync(BridgeFeature enabled)
    {
        MediaState? mediaState = enabled.HasFlag(BridgeFeature.Media) ? await media.ReadAsync() : null;
        PcState? pc = null;
        if ((enabled & (BridgeFeature.Volume | BridgeFeature.Clipboard | BridgeFeature.PcStatus)) != 0)
        {
            var (volume, muted) = enabled.HasFlag(BridgeFeature.Volume) ? features.ReadAudio() : (0d, false);
            var cpu = enabled.HasFlag(BridgeFeature.PcStatus) ? features.ReadCpuPercent() : 0;
            var memory = enabled.HasFlag(BridgeFeature.PcStatus) ? WindowsFeatures.ReadMemoryPercent() : 0;
            var clipboard = enabled.HasFlag(BridgeFeature.Clipboard) ? features.ReadClipboardText() : null;
            pc = new(volume, muted, cpu, memory, clipboard);
        }
        return BridgeCodec.Sign(BridgeMessageType.State, new BridgePayload(enabled, mediaState, pc), key);
    }
}
