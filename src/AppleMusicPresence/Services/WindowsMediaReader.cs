using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace AppleMusicPresence.Services;

public sealed record MediaReading(PlaybackSnapshot? Playback, byte[]? Artwork, string? Warning = null);

public sealed class WindowsMediaReader
{
    private readonly TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onMediaChanged;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private volatile bool _mediaChanged = true;
    private Track? _track;
    private TimeSpan _trackEnd;
    private Track? _artworkTrack;
    private byte[]? _artwork;

    // Every media-properties read pins native memory until the GC finalizes it, so metadata
    // is re-read when Windows reports a change rather than on every poll.
    public WindowsMediaReader() => _onMediaChanged = (_, _) => _mediaChanged = true;

    public async Task<MediaReading> ReadAsync(CancellationToken token)
    {
        try
        {
            _manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token);
            var session = _manager.GetSessions()
                .Where(s => AppleMusicSource.IsMatch(s.SourceAppUserModelId))
                .OrderByDescending(s => s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                .FirstOrDefault();
            if (!ReferenceEquals(session, _session)) Watch(session);
            if (session is null)
            {
                _track = null;
                _artworkTrack = null;
                _artwork = null;
                return new(null, null);
            }

            var info = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            // A new duration also means a new track, even if the change event was missed.
            if (_mediaChanged || _track is null || timeline.EndTime != _trackEnd)
            {
                _mediaChanged = false;
                try
                {
                    var media = await session.TryGetMediaPropertiesAsync().AsTask(token);
                    _track = Track.FromWindows(media.Title ?? "", media.Artist ?? "", media.AlbumTitle ?? "");
                    _trackEnd = timeline.EndTime;
                    if (_artworkTrack != _track) await ReadArtworkAsync(media, _track, token);
                }
                catch
                {
                    _mediaChanged = true;
                    throw;
                }
            }
            var state = info.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
                _ => PlaybackState.Stopped
            };
            var playback = new PlaybackSnapshot(session.SourceAppUserModelId, _track, state,
                timeline.Position, timeline.StartTime, timeline.EndTime, timeline.LastUpdatedTime,
                info.PlaybackRate ?? 1);
            return new(playback, _artwork);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or InvalidOperationException)
        {
            _manager = null;
            Watch(null);
            return new(null, null, "Não foi possível ler o Apple Music. Tentando novamente. " + ex.Message);
        }
    }

    private async Task ReadArtworkAsync(GlobalSystemMediaTransportControlsSessionMediaProperties media, Track track, CancellationToken token)
    {
        byte[]? artwork = null;
        if (media.Thumbnail is not null)
        {
            try
            {
                using var stream = await media.Thumbnail.OpenReadAsync().AsTask(token);
                if (stream.Size is > 0 and <= 5_000_000)
                {
                    using var reader = new DataReader(stream);
                    await reader.LoadAsync((uint)stream.Size).AsTask(token);
                    artwork = new byte[(int)stream.Size];
                    reader.ReadBytes(artwork);
                }
            }
            catch (Exception ex) when (ex is COMException or IOException)
            {
                // A cover failure must never hide a valid track; retry on the next poll.
                _artworkTrack = null;
                _artwork = null;
                _mediaChanged = true;
                return;
            }
        }
        _artworkTrack = track;
        _artwork = artwork;
    }

    private void Watch(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            try { _session.MediaPropertiesChanged -= _onMediaChanged; }
            catch (COMException) { }
        }
        _session = session;
        _mediaChanged = true;
        if (session is not null) session.MediaPropertiesChanged += _onMediaChanged;
    }
}
