using System.Security.Cryptography;
using Bridge.Protocol;
using Windows.Media.Control;

namespace Bridge.Windows;

/// <summary>
/// Reads the current Windows media session. Every WinRT call here can and does throw transiently
/// (COMException / "RPC server unavailable" while a player starts, stops or changes session), so
/// each one is contained: a bad read yields null or a stale value and never propagates into the
/// connection loop.
/// </summary>
public sealed class WindowsMediaBridge
{
    /// <summary>Artwork ceiling. RFCOMM is slow, and base64 inflates by a third.</summary>
    private const int MaxArtworkBytes = 96_000;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _artworkId;
    private string? _artworkBase64;
    private string? _artworkSourceKey;

    public async Task InitializeAsync()
    {
        try { _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync(); }
        catch (Exception ex) { Log.Warn($"media session manager unavailable: {ex.Message}"); }
    }

    /// <param name="knownArtworkId">
    /// Artwork the peer already holds. Matching ids send no bytes — artwork is by far the largest
    /// field and resending it on every poll saturates the link.
    /// </param>
    public async Task<MediaState?> ReadAsync(string? knownArtworkId = null)
    {
        GlobalSystemMediaTransportControlsSession? session;
        try { session = _manager?.GetCurrentSession(); }
        catch (Exception ex) { Log.Warn($"GetCurrentSession failed: {ex.Message}"); return null; }
        if (session is null) return null;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        try { props = await session.TryGetMediaPropertiesAsync(); }
        catch (Exception ex) { Log.Warn($"TryGetMediaProperties failed: {ex.Message}"); }

        bool playing = false;
        try { playing = session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception ex) { Log.Warn($"GetPlaybackInfo failed: {ex.Message}"); }

        long position = 0, duration = 0;
        try
        {
            var timeline = session.GetTimelineProperties();
            position = (long)timeline.Position.TotalMilliseconds;
            duration = (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds;
        }
        catch (Exception ex) { Log.Warn($"GetTimelineProperties failed: {ex.Message}"); }

        string? sourceApp = null;
        try { sourceApp = session.SourceAppUserModelId; } catch { /* transient */ }

        var (artworkId, artwork) = await ReadArtworkAsync(props, sourceApp, knownArtworkId);

        return new MediaState(props?.Title, props?.Artist, props?.AlbumTitle, sourceApp,
            playing, position, Math.Max(0, duration), artwork, artworkId);
    }

    /// <summary>
    /// Decodes artwork at most once per track. The thumbnail stream is reopened on every poll
    /// otherwise, which is expensive enough to stall the state read past the client's read deadline.
    /// </summary>
    private async Task<(string? Id, string? Base64)> ReadArtworkAsync(
        GlobalSystemMediaTransportControlsSessionMediaProperties? props, string? sourceApp, string? knownArtworkId)
    {
        if (props?.Thumbnail is null) { _artworkId = _artworkBase64 = _artworkSourceKey = null; return (null, null); }

        var sourceKey = $"{sourceApp}|{props.Title}|{props.Artist}|{props.AlbumTitle}";
        if (sourceKey != _artworkSourceKey)
        {
            _artworkSourceKey = sourceKey;
            _artworkId = _artworkBase64 = null;
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync().AsTask();
                using var ms = new MemoryStream();
                await stream.AsStreamForRead().CopyToAsync(ms);
                if (ms.Length > 0 && ms.Length <= MaxArtworkBytes)
                {
                    var bytes = ms.ToArray();
                    _artworkBase64 = Convert.ToBase64String(bytes);
                    _artworkId = Convert.ToHexString(SHA256.HashData(bytes))[..16];
                }
                else if (ms.Length > MaxArtworkBytes)
                {
                    Log.Warn($"artwork skipped, {ms.Length} bytes exceeds the {MaxArtworkBytes} byte limit");
                }
            }
            catch (Exception ex)
            {
                // Forget the key so the next poll retries. Keeping it would mean one transient
                // thumbnail failure left the track with no artwork until it changed.
                _artworkSourceKey = null;
                Log.Warn($"artwork read failed: {ex.Message}");
            }
        }

        // The peer already has these bytes; send the id alone so it keeps showing the image.
        if (_artworkId is not null && _artworkId == knownArtworkId) return (_artworkId, null);
        return (_artworkId, _artworkBase64);
    }

    public async Task ExecuteAsync(CommandPayload command)
    {
        if (command.Media is not { } c) return;
        GlobalSystemMediaTransportControlsSession? s;
        try { s = _manager?.GetCurrentSession(); }
        catch (Exception ex) { Log.Warn($"GetCurrentSession failed: {ex.Message}"); return; }
        if (s is null) { Log.Warn($"command {c} ignored: no active media session"); return; }
        try
        {
            var ok = c switch
            {
                MediaCommand.Play => await s.TryPlayAsync(),
                MediaCommand.Pause => await s.TryPauseAsync(),
                MediaCommand.Toggle => await s.TryTogglePlayPauseAsync(),
                MediaCommand.Next => await s.TrySkipNextAsync(),
                MediaCommand.Previous => await s.TrySkipPreviousAsync(),
                MediaCommand.Seek when command.SeekMs is { } ms => await s.TryChangePlaybackPositionAsync(ms * 10_000),
                _ => true
            };
            if (!ok) Log.Warn($"media session refused {c}");
        }
        catch (Exception ex) { Log.Warn($"command {c} failed: {ex.Message}"); }
    }
}
