using System.Text.Json;
using AppleMusicPresence.Core;
using AppleMusicPresence.Services;

// Explicit manual integration check: uses the saved public app ID and the real
// current song. Close the tray app first to avoid two publishers competing.
var output = Path.GetFullPath(args.Length > 0 ? args[0] : "out/live-discord-check.json");
var store = new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppleMusicPresence", "settings.json"));
var settings = store.Load();
if (settings.Warning is not null) throw new InvalidOperationException(settings.Warning);
if (!AppSettings.IsValidApplicationId(settings.Settings.ApplicationId)) throw new InvalidOperationException("Configure o Application ID antes deste teste.");
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
var reader = new WindowsMediaReader();
var catalog = new AppleCatalog(http);
var reading = await reader.ReadAsync(timeout.Token);
if (reading.Playback?.State != PlaybackState.Playing) throw new InvalidOperationException("Nenhuma faixa tocando no Apple Music.");
var cover = await catalog.FindAsync(reading.Playback.Track, timeout.Token);
if (cover is null) throw new InvalidOperationException("A capa da faixa atual não foi localizada.");
var fresh = await reader.ReadAsync(timeout.Token);
if (fresh.Playback?.Track != reading.Playback.Track) throw new InvalidOperationException("A música mudou durante o teste; execute novamente.");
var desired = PresenceFactory.Create(fresh.Playback, true, cover, DateTimeOffset.UtcNow)!;
using var bridge = new DiscordBridge();
bridge.Configure(settings.Settings.ApplicationId);
bridge.Publish(desired);
while (!timeout.IsCancellationRequested)
{
    bridge.Pump();
    if (bridge.Error is not null) throw new InvalidOperationException(bridge.Error);
    if (bridge.HasPublishedPresence && bridge.AcknowledgedActivity is { } acknowledged &&
        acknowledged.Title == desired.Title && acknowledged.Name == desired.Artist &&
        acknowledged.ArtworkImage?.StartsWith("mp:external/", StringComparison.Ordinal) == true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var report = new
        {
            CheckedAt = DateTimeOffset.Now,
            Requested = desired,
            Acknowledged = acknowledged,
            Verified = "O Discord real confirmou o nome do artista e a imagem na atividade."
        };
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    await Task.Delay(100, timeout.Token);
}
throw new TimeoutException("O Discord não confirmou o artista e a capa.");
