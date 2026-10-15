using DiscordRPC;

namespace AppleMusicPresence.Core;

public sealed record CatalogMatch(string ArtworkUrl, string TrackUrl);

public sealed record PresenceData(
    string Title, string Artist, string Album, string? ArtworkUrl, string TrackUrl,
    long? StartSeconds, long? EndSeconds)
{
    public RichPresence ToRpc() => new()
    {
        Type = ActivityType.Listening,
        StatusDisplay = StatusDisplayType.State,
        Details = Title.Length == 1 ? Title + "\u200b" : Title,
        State = Artist.Length == 1 ? Artist + "\u200b" : Artist,
        Assets = ArtworkUrl is null ? null : new Assets
        {
            LargeImageKey = ArtworkUrl,
            LargeImageText = string.IsNullOrWhiteSpace(Album) ? Title : Album
        },
        Timestamps = StartSeconds.HasValue && EndSeconds.HasValue ? new Timestamps(
            DateTimeOffset.FromUnixTimeSeconds(StartSeconds.Value).UtcDateTime,
            DateTimeOffset.FromUnixTimeSeconds(EndSeconds.Value).UtcDateTime) : null,
        Buttons = [new DiscordRPC.Button { Label = "Ouvir no Apple Music", Url = TrackUrl }]
    };

    public bool EquivalentTo(PresenceData? other) => other is not null &&
        Title == other.Title && Artist == other.Artist && Album == other.Album &&
        ArtworkUrl == other.ArtworkUrl && TrackUrl == other.TrackUrl &&
        Close(StartSeconds, other.StartSeconds) && Close(EndSeconds, other.EndSeconds);

    private static bool Close(long? left, long? right) =>
        left == right || (left.HasValue && right.HasValue && Math.Abs(left.Value - right.Value) <= 2);
}

public static class PresenceFactory
{
    public static PresenceData? Create(PlaybackSnapshot? playback, bool sharing,
        CatalogMatch? catalog, DateTimeOffset now)
    {
        if (!sharing || playback is null || !AppleMusicSource.IsMatch(playback.Source) ||
            playback.State != PlaybackState.Playing || string.IsNullOrWhiteSpace(playback.Track.Title))
            return null;

        long? start = null, end = null;
        if (playback.Duration > TimeSpan.Zero && playback.Rate == 1 &&
            playback.UpdatedAt > DateTimeOffset.UnixEpoch && playback.UpdatedAt <= now.AddSeconds(5))
        {
            start = now.Subtract(playback.PositionAt(now)).ToUnixTimeSeconds();
            end = start + (long)Math.Ceiling(playback.Duration.TotalSeconds);
            // Do not advertise an expired timer while the player is changing tracks.
            if (end <= now.ToUnixTimeSeconds()) start = end = null;
        }
        return new(
            TextLimits.Truncate(playback.Track.Title, 128),
            TextLimits.Truncate(string.IsNullOrWhiteSpace(playback.Track.Artist) ? "Apple Music" : playback.Track.Artist, 128),
            TextLimits.Truncate(playback.Track.Album, 128),
            catalog?.ArtworkUrl, catalog?.TrackUrl ?? playback.Track.SearchUrl, start, end);
    }
}
