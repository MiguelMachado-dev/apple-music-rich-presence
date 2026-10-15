using System.Text;
using System.Text.Json.Nodes;
using AppleMusicPresence.Core;
using AppleMusicPresence.Specs;

var failures = new List<string>();
var passed = 0;
var now = new DateTimeOffset(2026, 10, 14, 21, 0, 0, TimeSpan.Zero);
const string source = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
var playback = new PlaybackSnapshot(source, new Track("Scary Mask (feat. FEVER 333)", "Poppy", "Choke - EP"),
    PlaybackState.Playing, TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(182), now.AddSeconds(-5));

void Check(string name, Action test)
{
    try { test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception ex) { failures.Add($"{name}: {ex.Message}"); Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
void Expect(bool value, string message = "Unexpected result") { if (!value) throw new Exception(message); }
PresenceData? Make(PlaybackSnapshot? snapshot, bool enabled = true) => PresenceFactory.Create(snapshot, enabled, null, now);

Check("Artist is used for the listening header", () =>
    Expect(Make(playback)!.ToRpc().StatusDisplay == DiscordRPC.StatusDisplayType.State));
Check("Album artwork acknowledgment without a small image does not crash", () =>
{
    var coverPipe = new FakeDiscordPipe();
    using var coverBridge = new DiscordBridge(() => coverPipe);
    coverBridge.Configure("123456789012345678");
    coverBridge.Publish(PresenceFactory.Create(playback, true,
        new CatalogMatch("https://is1-ssl.mzstatic.com/image/test/512x512bb.jpg", playback.Track.SearchUrl), now));
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
        coverBridge.Pump();
        if (coverBridge.HasPublishedPresence)
        {
            Expect(coverBridge.AcknowledgedActivity?.Name == "Poppy", "Artist was not sent as activity.name");
            Expect(coverBridge.AcknowledgedActivity?.ArtworkImage?.Contains("mzstatic.com") == true,
                "The original artwork acknowledgment was lost");
            return;
        }
        Thread.Sleep(25);
    }
    throw new Exception("Artwork was not acknowledged");
});
Check("Missing song search result recovers the exact album from the artist catalog", () =>
{
    using var handler = new FakeAppleHttpHandler(uri => uri.Query.Contains("entity=musicArtist")
        ? """{"results":[{"artistId":1004130511,"artistName":"Poppy"}]}"""
        : uri.Query.Contains("/lookup") || uri.AbsolutePath == "/lookup"
            ? """{"results":[{"artistName":"Poppy","collectionName":"Empty Hands","artworkUrl100":"https://is1-ssl.mzstatic.com/image/cover/100x100bb.jpg","collectionViewUrl":"https://music.apple.com/br/album/empty-hands/1852046141"}]}"""
            : """{"results":[{"trackName":"Unrelated song","artistName":"Another artist"}]}""");
    using var http = new HttpClient(handler);
    var catalog = new AppleCatalog(http, requestInterval: TimeSpan.Zero);
    var track = new Track("Eat The Hate", "Poppy", "Empty Hands");
    var match = catalog.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult();
    Expect(match is not null && match.ArtworkUrl.Contains("512x512bb"), "Album cover was not recovered");
    Expect(handler.Requests == 3, "Expected song search, artist search and album lookup");
    var nextTrack = catalog.FindAsync(track with { Title = "Guardian" }, CancellationToken.None).GetAwaiter().GetResult();
    Expect(nextTrack is not null && handler.Requests == 3, "Album artwork was not reused for the next song");
});
Check("A missed cover can be retried after thirty seconds", () =>
{
    var clock = new TestClock();
    var found = false;
    using var handler = new FakeAppleHttpHandler(_ => found
        ? """{"results":[{"trackName":"Track","artistName":"Artist","artworkUrl100":"https://is1-ssl.mzstatic.com/image/cover/100x100bb.jpg","trackViewUrl":"https://music.apple.com/br/song/123"}]}"""
        : """{"results":[]}""");
    using var http = new HttpClient(handler);
    var catalog = new AppleCatalog(http, clock, TimeSpan.Zero);
    var track = new Track("Track", "Artist", "");
    Expect(catalog.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() is null);
    found = true;
    Expect(catalog.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() is null && handler.Requests == 1);
    clock.Now = clock.Now.AddSeconds(31);
    Expect(catalog.FindAsync(track, CancellationToken.None).GetAwaiter().GetResult() is not null && handler.Requests == 2);
});
Check("Malformed catalog data remains an explicit error", () =>
{
    using var handler = new FakeAppleHttpHandler(_ => """{"unexpected":true}""");
    using var http = new HttpClient(handler);
    try
    {
        new AppleCatalog(http, requestInterval: TimeSpan.Zero).FindAsync(new("Track", "Artist", ""), CancellationToken.None).GetAwaiter().GetResult();
        throw new Exception("Missing results were silently ignored");
    }
    catch (System.Text.Json.JsonException) { }
});
if (args.Contains("--regressions-only")) return failures.Count == 0 ? 0 : 1;

Check("Normalize the live Apple Music metadata format", () =>
    Expect(Track.FromWindows(" Scary Mask ", "Poppy — Choke - EP", "") == new Track("Scary Mask", "Poppy", "Choke - EP")));
Check("Keep an explicit album and artist intact", () =>
    Expect(Track.FromWindows("Track", "Artist — Part", "Album").Artist == "Artist — Part"));
Check("Only accept the Apple Music source", () =>
{
    Expect(AppleMusicSource.IsMatch(source));
    Expect(!AppleMusicSource.IsMatch("Spotify.exe"));
    Expect(!AppleMusicSource.IsMatch("chrome.exe"));
    Expect(!AppleMusicSource.IsMatch("FakeAppleMusic.exe"));
    Expect(Make(playback with { Source = "chrome.exe" }) is null);
});
Check("Playing track produces a listening activity and music link", () =>
{
    var presence = Make(playback)!.ToRpc();
    Expect(presence.Type == DiscordRPC.ActivityType.Listening);
    Expect(presence.Details == playback.Track.Title && presence.State == "Poppy");
    Expect(presence.Buttons.Single().Url.StartsWith("https://music.apple.com/"));
});
Check("Pause removes the activity", () => Expect(Make(playback with { State = PlaybackState.Paused }) is null));
Check("Stopped music removes the activity", () => Expect(Make(playback with { State = PlaybackState.Stopped }) is null));
Check("Closed Apple Music removes the activity", () => Expect(Make(null) is null));
Check("Sharing disabled removes the activity", () => Expect(Make(playback, false) is null));
Check("Incomplete track is never advertised", () => Expect(Make(playback with { Track = new("  ", "Artist", "Album") }) is null));
Check("Timeline advances from LastUpdatedTime", () =>
{
    Expect(playback.PositionAt(now) == TimeSpan.FromSeconds(35));
    var presence = Make(playback)!;
    Expect(presence.StartSeconds == now.AddSeconds(-35).ToUnixTimeSeconds());
    Expect(presence.EndSeconds - presence.StartSeconds == 182);
});
Check("Paused timeline stays frozen", () => Expect((playback with { State = PlaybackState.Paused }).PositionAt(now) == TimeSpan.FromSeconds(30)));
Check("Seek changes timestamps without resetting the song", () =>
{
    var before = Make(playback)!;
    var after = Make(playback with { Position = TimeSpan.FromSeconds(90), UpdatedAt = now })!;
    Expect(!before.EquivalentTo(after));
    Expect(after.StartSeconds == now.AddSeconds(-90).ToUnixTimeSeconds());
});
Check("Clock jitter does not flood Discord updates", () =>
    Expect(Make(playback)!.EquivalentTo(Make(playback with { UpdatedAt = playback.UpdatedAt.AddSeconds(1) }))));
Check("Unknown duration, invalid timeline and nonstandard speed omit timer", () =>
{
    Expect(Make(playback with { End = TimeSpan.Zero })!.StartSeconds is null);
    Expect(Make(playback with { UpdatedAt = DateTimeOffset.MinValue })!.StartSeconds is null);
    Expect(Make(playback with { Rate = 2 })!.StartSeconds is null);
});
Check("Nonzero timeline origin and end clamp", () =>
{
    var shifted = playback with { Start = TimeSpan.FromSeconds(100), Position = TimeSpan.FromSeconds(130), End = TimeSpan.FromSeconds(200) };
    Expect(shifted.PositionAt(now).TotalSeconds == 35 && shifted.Duration.TotalSeconds == 100);
    Expect((playback with { Position = TimeSpan.FromDays(1) }).PositionAt(now) == playback.Duration);
});
Check("Unicode and long metadata stay within Discord limits", () =>
{
    var name = string.Concat(Enumerable.Repeat("🎵日本語á", 60));
    var presence = Make(playback with { Track = new(name, name, name) })!;
    Expect(Encoding.UTF8.GetByteCount(presence.Title) <= 128);
    Expect(!presence.Title.Contains('\uFFFD'));
    _ = presence.ToRpc();
});
Check("One-character track titles remain valid", () =>
    Expect(Make(playback with { Track = new("x", "y", "z") })!.ToRpc().Details.Length >= 2));
Check("Application ID accepts only a public numeric snowflake", () =>
{
    Expect(AppSettings.IsValidApplicationId("123456789012345678"));
    foreach (var id in new[] { "", "123", " 123456789012345678", "123456789012345678a", "123456789012345678901", "99999999999999999999", "token.secret.value" })
        Expect(!AppSettings.IsValidApplicationId(id), id);
});
Check("Catalog must match the track and artist, with the right album preferred", () =>
{
    var song = new CatalogSong(playback.Track.Title, "Poppy", "Choke - EP", "https://is1-ssl.mzstatic.com/image/100x100bb.jpg", "https://music.apple.com/br/album/test/123?i=456");
    var match = AppleCatalog.Select(playback.Track, [song with { ArtistName = "Wrong" }, song with { CollectionName = "Other", TrackViewUrl = "https://music.apple.com/wrong" }, song]);
    Expect(match?.TrackUrl == song.TrackViewUrl);
    Expect(AppleCatalog.Select(playback.Track, [song with { TrackName = "Other" }]) is null);
    Expect(AppleCatalog.Select(playback.Track, [song with { ArtworkUrl100 = "https://example.com/cover.jpg" }]) is null);
});
Check("Settings are persisted atomically and malformed JSON remains visible", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), "AppleMusicPresence-Specs-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        var path = Path.Combine(directory, "settings.json");
        var store = new SettingsStore(path);
        Expect(store.Load().Settings.ApplicationId == "");
        var settings = new AppSettings { ApplicationId = "123456789012345678", LookupArtwork = false, SharingEnabled = false };
        store.Save(settings);
        Expect(store.Load().Settings == settings);
        Expect(Directory.GetFiles(directory, "*.tmp").Length == 0);
        File.WriteAllText(path, "{broken-json");
        Expect(store.Load().Warning is not null);
        Expect(File.ReadAllText(path) == "{broken-json");
    }
    finally { Directory.Delete(directory, recursive: true); }
});

// Exercise the real RPC library serialization, READY/ack events, clearing and reconnects.
var pipe = new FakeDiscordPipe();
using (var bridge = new DiscordBridge(() => pipe))
{
    void WaitUntil(Func<bool> condition, string failure)
    {
        var deadline = DateTime.UtcNow.AddSeconds(7);
        while (DateTime.UtcNow < deadline)
        {
            bridge.Pump();
            if (condition()) return;
            Thread.Sleep(25);
        }
        throw new Exception(failure);
    }
    JsonNode?[] Activities() => pipe.Commands.Where(c => c["cmd"]?.GetValue<string>() == "SET_ACTIVITY")
        .Select(c => c["args"]?["activity"]).ToArray();

    Check("RPC handshake and activity acknowledgment", () =>
    {
        bridge.Configure("123456789012345678");
        bridge.Publish(Make(playback));
        WaitUntil(() => bridge.IsReady && bridge.HasPublishedPresence, "Discord READY/SET_ACTIVITY acknowledgment was not processed.");
        var activity = Activities().Last(a => a is not null)!;
        Expect(activity["type"]!.GetValue<int>() == 2);
        Expect(activity["details"]!.GetValue<string>() == playback.Track.Title);
    });
    Check("Equivalent polls do not submit repeated activity", () =>
    {
        var count = Activities().Length;
        for (var i = 0; i < 10; i++) bridge.Publish(Make(playback));
        Thread.Sleep(100);
        bridge.Pump();
        Expect(Activities().Length == count);
    });
    Check("Pause sends an explicit null activity", () =>
    {
        bridge.Publish(Make(playback with { State = PlaybackState.Paused }));
        WaitUntil(() => Activities().LastOrDefault() is null, "No clear command was sent.");
        Expect(!bridge.HasPublishedPresence);
    });
    Check("Next track replaces the old song", () =>
    {
        bridge.Publish(Make(playback with { Track = new("New track", "New artist", "New album") }));
        WaitUntil(() => Activities().LastOrDefault()?["details"]?.GetValue<string>() == "New track", "New track was not sent.");
    });
    Check("Discord errors are visible and pausing still clears after an error", () =>
    {
        pipe.RejectPresence();
        WaitUntil(() => bridge.Error is not null, "Discord error was not visible.");
        bridge.Publish(null);
        WaitUntil(() => Activities().LastOrDefault() is null, "Failed activity was not cleared.");
    });
    Check("Reconnect never republishes a song cleared during disconnect", () =>
    {
        var count = Activities().Length;
        var handshakes = pipe.Handshakes;
        pipe.Disconnect();
        bridge.Publish(null);
        WaitUntil(() => pipe.Handshakes > handshakes && bridge.IsReady, "Did not reconnect.");
        Thread.Sleep(150);
        bridge.Pump();
        Expect(Activities().Skip(count).All(a => a is null), "A stale song was replayed.");
    });
    Check("Closing the bridge clears an active song", () =>
    {
        bridge.Publish(Make(playback));
        WaitUntil(() => bridge.HasPublishedPresence, "Active song was not acknowledged before shutdown.");
        bridge.Dispose();
        WaitUntil(() => Activities().LastOrDefault() is null, "Shutdown did not clear the active song.");
    });
}

Console.WriteLine($"\n{passed} passed; {failures.Count} failed.");
foreach (var failure in failures) Console.Error.WriteLine(failure);
return failures.Count == 0 ? 0 : 1;
