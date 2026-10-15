using System.Text;

namespace AppleMusicPresence.Core;

public enum PlaybackState { Stopped, Playing, Paused }

public sealed record Track(string Title, string Artist, string Album)
{
    public static Track FromWindows(string title, string artist, string album)
    {
        // Apple Music for Windows exposes "artist — album" when AlbumTitle is empty.
        if (string.IsNullOrWhiteSpace(album))
        {
            var separator = artist.IndexOf(" — ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < artist.Length)
            {
                album = artist[(separator + 3)..];
                artist = artist[..separator];
            }
        }
        return new(title.Trim(), artist.Trim(), album.Trim());
    }

    public string SearchUrl => "https://music.apple.com/br/search?term=" +
        Uri.EscapeDataString(TextLimits.Truncate($"{Artist} {Title}", 100));
}

public sealed record PlaybackSnapshot(
    string Source, Track Track, PlaybackState State,
    TimeSpan Position, TimeSpan Start, TimeSpan End,
    DateTimeOffset UpdatedAt, double Rate = 1)
{
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var seconds = (Position - Start).TotalSeconds;
        if (State == PlaybackState.Playing && UpdatedAt > DateTimeOffset.UnixEpoch &&
            UpdatedAt <= now && double.IsFinite(Rate) && Rate > 0)
            seconds += (now - UpdatedAt).TotalSeconds * Rate;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 0,
            Duration > TimeSpan.Zero ? Duration.TotalSeconds : Math.Max(0, seconds)));
    }
}

public static class AppleMusicSource
{
    public static bool IsMatch(string source) =>
        source.Equals("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", StringComparison.OrdinalIgnoreCase) ||
        source.Equals("AppleMusic.exe", StringComparison.OrdinalIgnoreCase);
}

public static class TextLimits
{
    // Bound both UTF-16 length (library validation) and UTF-8 bytes (Discord transport).
    public static string Truncate(string value, int limit)
    {
        var result = new StringBuilder();
        var bytes = 0;
        foreach (var rune in value.Trim().EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > limit || result.Length + rune.Utf16SequenceLength > limit)
                break;
            result.Append(rune);
            bytes += rune.Utf8SequenceLength;
        }
        return result.ToString();
    }
}
