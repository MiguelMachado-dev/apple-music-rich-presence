using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AppleMusicPresence.Core;

public sealed class AppleCatalog(HttpClient http, TimeProvider? clock = null, TimeSpan? requestInterval = null)
{
    private readonly Dictionary<Track, (DateTimeOffset Expires, CatalogMatch? Match)> _cache = [];
    private readonly Dictionary<(string Artist, string Album), (DateTimeOffset Expires, CatalogSong Album)> _albums = [];
    private readonly Dictionary<string, (DateTimeOffset Expires, CatalogSong[] Albums)> _artists = [];
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _requestInterval = requestInterval ?? TimeSpan.FromSeconds(4);
    private DateTimeOffset _nextRequest;

    public async Task<CatalogMatch?> FindAsync(Track track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)) return null;
        await _lookupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock.GetUtcNow();
            var albumKey = (Normalize(track.Artist), Normalize(track.Album));
            var hasCachedTrack = _cache.TryGetValue(track, out var cached) && cached.Expires > now;
            if (hasCachedTrack && cached.Match is not null) return cached.Match;
            if (_albums.TryGetValue(albumKey, out var cover) && cover.Expires > now)
                return AlbumMatch(track, cover.Album);
            if (hasCachedTrack) return null;

            var term = Uri.EscapeDataString($"{track.Artist} {track.Title}");
            var songs = await RequestAsync($"search?term={term}&entity=song&media=music&country=BR&limit=25", cancellationToken).ConfigureAwait(false);
            var match = Select(track, songs);
            var exactAlbum = songs.FirstOrDefault(song => IsAlbum(track, song) && IsAppleUrl(song.ArtworkUrl100, artwork: true));

            if (match is null && !string.IsNullOrWhiteSpace(track.Album))
            {
                // The song search index can omit releases that the artist lookup has.
                var albums = await ArtistAlbumsAsync(track.Artist, cancellationToken).ConfigureAwait(false);
                exactAlbum = albums.FirstOrDefault(song => IsAlbum(track, song) && IsAppleUrl(song.ArtworkUrl100, artwork: true));
                if (exactAlbum is not null) match = AlbumMatch(track, exactAlbum);
            }

            if (exactAlbum is not null)
            {
                if (_albums.Count >= 128) _albums.Clear();
                _albums[albumKey] = (_clock.GetUtcNow().AddHours(1), exactAlbum);
            }
            if (_cache.Count >= 128) _cache.Clear();
            _cache[track] = (_clock.GetUtcNow().AddSeconds(match is null ? 30 : 3600), match);
            return match;
        }
        finally { _lookupGate.Release(); }
    }

    private async Task<CatalogSong[]> ArtistAlbumsAsync(string artist, CancellationToken token)
    {
        var key = Normalize(artist);
        if (_artists.TryGetValue(key, out var cached) && cached.Expires > _clock.GetUtcNow()) return cached.Albums;
        var artists = await RequestAsync($"search?term={Uri.EscapeDataString(artist)}&entity=musicArtist&attribute=artistTerm&country=BR&limit=10", token).ConfigureAwait(false);
        var ids = artists.Where(item => Normalize(item.ArtistName) == key && item.ArtistId > 0)
            .Select(item => item.ArtistId).Distinct().Take(5).ToArray();
        var albums = ids.Length == 0 ? [] : await RequestAsync(
            $"lookup?id={string.Join(',', ids)}&entity=album&limit=200&country=BR", token).ConfigureAwait(false);
        if (_artists.Count >= 16) _artists.Clear();
        _artists[key] = (_clock.GetUtcNow().AddMinutes(10), albums);
        return albums;
    }

    private async Task<CatalogSong[]> RequestAsync(string path, CancellationToken token)
    {
        var wait = _nextRequest - _clock.GetUtcNow();
        if (wait > TimeSpan.Zero) await Task.Delay(wait, token).ConfigureAwait(false);
        _nextRequest = _clock.GetUtcNow() + _requestInterval;
        var response = await http.GetFromJsonAsync<SearchResult>("https://itunes.apple.com/" + path, token).ConfigureAwait(false);
        return response?.Results ?? throw new JsonException("A resposta da Apple não contém a lista de resultados.");
    }

    private static bool IsAlbum(Track track, CatalogSong song) => !string.IsNullOrWhiteSpace(track.Album) &&
        Normalize(song.ArtistName) == Normalize(track.Artist) && Normalize(song.CollectionName) == Normalize(track.Album);

    private static CatalogMatch AlbumMatch(Track track, CatalogSong song) => new(
        ArtworkUrl(song.ArtworkUrl100!), IsAppleUrl(song.CollectionViewUrl, artwork: false) ? song.CollectionViewUrl! : track.SearchUrl);

    private static string ArtworkUrl(string url) => url.Replace("100x100bb", "512x512bb", StringComparison.Ordinal);

    public static CatalogMatch? Select(Track track, IEnumerable<CatalogSong> songs)
    {
        var matches = songs.Where(song => Normalize(song.TrackName) == Normalize(track.Title) &&
            Normalize(song.ArtistName) == Normalize(track.Artist));
        var song = matches.OrderByDescending(song => Normalize(song.CollectionName) == Normalize(track.Album))
            .FirstOrDefault();
        if (song is null || !IsAppleUrl(song.ArtworkUrl100, artwork: true) || !IsAppleUrl(song.TrackViewUrl, artwork: false))
            return null;
        return new(ArtworkUrl(song.ArtworkUrl100!), song.TrackViewUrl!);
    }

    private static bool IsAppleUrl(string? url, bool artwork) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        url!.Length <= (artwork ? 256 : 512) &&
        (artwork ? uri.Host.EndsWith(".mzstatic.com", StringComparison.OrdinalIgnoreCase) :
            uri.Host is "music.apple.com" or "itunes.apple.com");

    private static string Normalize(string? value) => string.Concat((value ?? "").Normalize(NormalizationForm.FormD)
        .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(c)))
        .ToUpperInvariant();

    private sealed record SearchResult([property: JsonPropertyName("results")] CatalogSong[]? Results);
}

public sealed record CatalogSong(
    [property: JsonPropertyName("trackName")] string? TrackName,
    [property: JsonPropertyName("artistName")] string? ArtistName,
    [property: JsonPropertyName("collectionName")] string? CollectionName,
    [property: JsonPropertyName("artworkUrl100")] string? ArtworkUrl100,
    [property: JsonPropertyName("trackViewUrl")] string? TrackViewUrl,
    [property: JsonPropertyName("collectionViewUrl")] string? CollectionViewUrl = null,
    [property: JsonPropertyName("artistId")] long ArtistId = 0);
