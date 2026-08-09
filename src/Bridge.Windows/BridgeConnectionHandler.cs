using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Bridge.Protocol;

namespace Bridge.Windows;

/// <summary>
/// Serves one connected peer.
///
/// The link is full duplex: a reader loop applies inbound commands and a push loop emits state on a
/// timer, both feeding a single writer. v1 replied only when polled, so the client had to sit in
/// lockstep behind its own read timeout, and a slow state read looked exactly like a dead link.
/// </summary>
public sealed class BridgeConnectionHandler(WindowsMediaBridge media, WindowsFeatures features, byte[] key)
{
    private static readonly TimeSpan PushInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>Drop a peer that has gone quiet. Clients ping well inside this.</summary>
    private static readonly TimeSpan PeerIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, long> _nonces = new();

    /// <summary>
    /// Per-connection state. Nothing here is shared between peers, but the read, push and write
    /// loops of one connection all touch it, so the cross-loop fields are volatile.
    /// </summary>
    private sealed class PeerSession
    {
        /// <summary>Written by the read loop, read by the push loop.</summary>
        public volatile BridgeFeature Enabled = BridgeFeature.Media;

        /// <summary>Written by the write loop once bytes are flushed, read by the push loop.</summary>
        public volatile string? KnownArtworkId;

        public DateTime LastFrameAt = DateTime.UtcNow;
        public int Rejected;
    }

    /// <param name="CarriesArtworkId">
    /// Set when this frame actually carries artwork bytes. The peer is credited with them only once
    /// the frame has been flushed — the outbound queue drops frames under back pressure, and the
    /// artwork frame is the largest and so the likeliest to be dropped. Crediting on enqueue would
    /// leave the peer with no image until the track changed.
    /// </param>
    private readonly record struct OutboundFrame(string Line, string? CarriesArtworkId);

    public async Task HandleAsync(Stream stream, string transport, CancellationToken ct = default)
    {
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var session = new PeerSession();

        // Bounded + DropOldest: if the peer reads slower than we produce (RFCOMM under artwork),
        // discard stale snapshots instead of growing a queue the peer will never catch up on.
        var outgoing = Channel.CreateBounded<OutboundFrame>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });

        // A pending read on a silent peer does not always observe token cancellation, and a stuck
        // read would keep the tray claiming a live connection forever. Closing the stream is what
        // reliably unblocks it.
        await using var closeOnCancel = connection.Token.Register(() => { try { stream.Close(); } catch { } });

        var writerLoop = WriteLoopAsync(stream, outgoing.Reader, session, connection.Token);
        var pushLoop = PushLoopAsync(outgoing.Writer, session, connection);
        try
        {
            await ReadLoopAsync(stream, outgoing.Writer, session, transport, connection.Token);
        }
        finally
        {
            outgoing.Writer.TryComplete();
            await connection.CancelAsync();
            try { await Task.WhenAll(writerLoop, pushLoop); } catch { /* shutting down */ }
        }
    }

    private async Task ReadLoopAsync(Stream stream, ChannelWriter<OutboundFrame> outgoing, PeerSession session, string transport, CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true);
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;
            if (line.Length == 0) continue;
            session.LastFrameAt = DateTime.UtcNow;

            var request = BridgeCodec.Deserialize(line);
            if (request is null) { Reject(session, transport, BridgeRejectReason.MalformedJson); continue; }
            if (!BridgeCodec.Verify(request, key, BridgeCodec.MaxClockSkew, out var reason)) { Reject(session, transport, reason); continue; }
            if (!AcceptNonce(request)) { Reject(session, transport, BridgeRejectReason.ReplayedNonce); continue; }

            var payload = request.ReadPayload();
            session.Enabled = payload.EnabledFeatures;
            if (session.Rejected > 0)
            {
                Log.Info($"{transport}: peer recovered after {session.Rejected} rejected frames");
                session.Rejected = 0;
            }

            if (payload.Command is not null)
            {
                await ApplyCommandAsync(payload);
                // Answer a command with fresh state so the watch UI settles immediately
                // rather than showing the old value until the next scheduled push.
                await PublishStateAsync(outgoing, session, ct);
            }
            else if (request.Type is BridgeMessageType.Ping or BridgeMessageType.Hello)
            {
                // The Android side does not call the transport "connected" until it has verified
                // one signed state frame. Reply to its first ping immediately instead of waiting
                // for the one-second push tick; this also makes a real RFCOMM connection distinct
                // from an OS-level Bluetooth bond that never reached the bridge protocol.
                await PublishStateAsync(outgoing, session, ct);
            }
        }
    }

    private void Reject(PeerSession session, string transport, BridgeRejectReason reason)
    {
        session.Rejected++;
        // Log the first one immediately: a systematically rejected peer (wrong key, wrong protocol
        // version, clock far off) is otherwise indistinguishable from an idle link.
        if (session.Rejected == 1 || session.Rejected % 50 == 0)
            Log.Warn($"{transport}: rejected frame ({reason}), {session.Rejected} so far on this connection");
    }

    private async Task PushLoopAsync(ChannelWriter<OutboundFrame> outgoing, PeerSession session, CancellationTokenSource connection)
    {
        try
        {
            while (!connection.IsCancellationRequested)
            {
                await Task.Delay(PushInterval, connection.Token);
                if (DateTime.UtcNow - session.LastFrameAt > PeerIdleTimeout)
                {
                    Log.Warn($"peer idle for {PeerIdleTimeout.TotalSeconds:0}s, closing connection");
                    await connection.CancelAsync();
                    break;
                }
                await PublishStateAsync(outgoing, session, connection.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"state push loop stopped: {ex.Message}"); }
    }

    private async Task PublishStateAsync(ChannelWriter<OutboundFrame> outgoing, PeerSession session, CancellationToken ct)
    {
        try
        {
            await outgoing.WriteAsync(await BuildStateAsync(session), ct);
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (Exception ex) { Log.Warn($"state build failed: {ex.Message}"); }
    }

    private static async Task WriteLoopAsync(Stream stream, ChannelReader<OutboundFrame> outgoing, PeerSession session, CancellationToken ct)
    {
        // The only writer on this stream. Two concurrent writers would interleave partial lines and
        // every frame would fail to parse - which looks identical to a broken signature.
        var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, true) { AutoFlush = false, NewLine = "\n" };
        try
        {
            await foreach (var frame in outgoing.ReadAllAsync(ct))
            {
                await writer.WriteLineAsync(frame.Line.AsMemory(), ct);
                await writer.FlushAsync(ct);
                // Only now does the peer really have the artwork bytes.
                if (frame.CarriesArtworkId is not null) session.KnownArtworkId = frame.CarriesArtworkId;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn($"write loop stopped: {ex.Message}"); }
        // The stream may already be closed by the teardown path; disposing then is not an error.
        finally { try { await writer.DisposeAsync(); } catch { } }
    }

    private bool AcceptNonce(SignedEnvelope e)
    {
        if (_nonces.Count > 512)
        {
            var cutoff = DateTimeOffset.UtcNow.Subtract(BridgeCodec.MaxClockSkew).ToUnixTimeMilliseconds();
            foreach (var old in _nonces.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray()) _nonces.TryRemove(old, out _);
        }
        return _nonces.TryAdd(e.Nonce, e.TimestampUnixMs);
    }

    private async Task ApplyCommandAsync(BridgePayload payload)
    {
        var command = payload.Command;
        if (command is null) return;
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Media) && command.Media is not null) await media.ExecuteAsync(command);
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Volume)) features.SetAudio(command.Volume, command.Muted);
        if (payload.EnabledFeatures.HasFlag(BridgeFeature.Clipboard) && command.ClipboardText is { } text) features.SetClipboardText(text);
    }

    private async Task<OutboundFrame> BuildStateAsync(PeerSession session)
    {
        var enabled = session.Enabled;
        MediaState? mediaState = null;
        if (enabled.HasFlag(BridgeFeature.Media)) mediaState = await media.ReadAsync(session.KnownArtworkId);

        PcState? pc = null;
        if ((enabled & (BridgeFeature.Volume | BridgeFeature.Clipboard | BridgeFeature.PcStatus)) != 0)
        {
            var (volume, muted) = enabled.HasFlag(BridgeFeature.Volume) ? features.ReadAudio() : (0d, false);
            var cpu = enabled.HasFlag(BridgeFeature.PcStatus) ? features.ReadCpuPercent() : 0;
            var memory = enabled.HasFlag(BridgeFeature.PcStatus) ? WindowsFeatures.ReadMemoryPercent() : 0;
            var power = enabled.HasFlag(BridgeFeature.PcStatus) ? WindowsFeatures.ReadPowerState() : (null, null, null);
            var clipboard = enabled.HasFlag(BridgeFeature.Clipboard) ? features.ReadClipboardText() : null;
            pc = new(volume, muted, cpu, memory, clipboard, power.Percent, power.Charging, power.OnAcPower);
        }

        var envelope = BridgeCodec.Sign(BridgeMessageType.State, new BridgePayload(enabled, mediaState, pc), key);
        // A reconnect starts a fresh session with no known id, so artwork is resent then.
        return new OutboundFrame(BridgeCodec.Serialize(envelope),
            mediaState?.ArtworkBase64 is not null ? mediaState.ArtworkId : null);
    }
}
