using Bridge.Protocol;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Bridge.Windows;

public sealed class WindowsMediaBridge
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    public async Task InitializeAsync() => _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

    public async Task<MediaState?> ReadAsync()
    {
        var session = _manager?.GetCurrentSession();
        if (session is null) return null;
        var props = await session.TryGetMediaPropertiesAsync();
        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        string? art = null;
        if (props.Thumbnail is not null)
        {
            using var stream = await props.Thumbnail.OpenReadAsync().AsTask();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            if (ms.Length <= 512_000) art = Convert.ToBase64String(ms.ToArray());
        }
        return new(props.Title, props.Artist, props.AlbumTitle, session.SourceAppUserModelId,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            (long)timeline.Position.TotalMilliseconds, (long)(timeline.EndTime - timeline.StartTime).TotalMilliseconds, art);
    }

    public async Task ExecuteAsync(CommandPayload command)
    {
        var s = _manager?.GetCurrentSession(); if (s is null) return;
        if (command.Media is { } c) switch (c)
        {
            case MediaCommand.Play: await s.TryPlayAsync(); break;
            case MediaCommand.Pause: await s.TryPauseAsync(); break;
            case MediaCommand.Toggle: await s.TryTogglePlayPauseAsync(); break;
            case MediaCommand.Next: await s.TrySkipNextAsync(); break;
            case MediaCommand.Previous: await s.TrySkipPreviousAsync(); break;
            case MediaCommand.Seek when command.SeekMs is { } ms: await s.TryChangePlaybackPositionAsync(ms * 10_000); break;
        }
    }
}
