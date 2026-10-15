using System.ComponentModel;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace AppleMusicPresence.Services;

public sealed class PresenceController : INotifyPropertyChanged, IDisposable
{
    private readonly WindowsMediaReader _media = new();
    private readonly DiscordBridge _discord = new();
    private readonly SettingsStore _store;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly AppleCatalog _catalog;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _lookupCancellation;
    private Track? _lookupTrack;
    private DateTimeOffset _nextLookupAt;
    private CatalogMatch? _match;
    private byte[]? _artworkBytes;
    private bool _polling;
    private bool _disposed;
    private string? _mediaWarning;
    private string? _settingsWarning;

    public event PropertyChangedEventHandler? PropertyChanged;
    public AppSettings Settings { get; private set; }
    public PlaybackSnapshot? Playback { get; private set; }
    public ImageSource? Artwork { get; private set; }
    public string? ArtworkWarning { get; private set; }
    public bool DiscordConnected => _discord.IsReady;
    public bool Published => _discord.HasPublishedPresence && ShouldShare;
    public bool ShouldShare => Settings.SharingEnabled && Playback?.State == PlaybackState.Playing;
    public bool HasArtwork => Artwork is not null;
    public bool SharingEnabled => Settings.SharingEnabled;
    public string TrackTitle => string.IsNullOrWhiteSpace(Playback?.Track.Title) ? "Sua próxima música aparece aqui" : Playback.Track.Title;
    public string Artist => string.IsNullOrWhiteSpace(Playback?.Track.Artist) ? "Abra o Apple Music e dê o play." : Playback.Track.Artist;
    public string Album => Playback?.Track.Album ?? "";
    public string PlaybackLabel => Playback?.State switch
    {
        PlaybackState.Playing => "TOCANDO AGORA",
        PlaybackState.Paused => "MÚSICA PAUSADA",
        _ => "AGUARDANDO MÚSICA"
    };
    public string PositionLabel => Format(Playback?.PositionAt(DateTimeOffset.UtcNow) ?? TimeSpan.Zero);
    public string DurationLabel => Format(Playback?.Duration ?? TimeSpan.Zero);
    public double Progress => Playback is { Duration.TotalSeconds: > 0 } p ? p.PositionAt(DateTimeOffset.UtcNow).TotalSeconds / p.Duration.TotalSeconds * 100 : 0;
    public string StatusTitle => !string.IsNullOrEmpty(_settingsWarning) ? "Confira suas configurações" :
        !Settings.SharingEnabled ? "Compartilhamento desativado" :
        !AppSettings.IsValidApplicationId(Settings.ApplicationId) ? "Conecte seu Discord" :
        _discord.Error is not null ? "Confira a conexão com o Discord" :
        !DiscordConnected ? "Aguardando o Discord" :
        _mediaWarning is not null ? "Reconectando ao Apple Music" :
        Playback?.State == PlaybackState.Paused ? "Música pausada" :
        Playback?.State != PlaybackState.Playing ? "Pronto para ouvir" :
        Published ? "Sua música está no Discord" : "Atualizando sua atividade";
    public string StatusDetail => _settingsWarning ?? _discord.Error ?? _mediaWarning ??
        (!Settings.SharingEnabled ? "Ative quando quiser mostrar o que está ouvindo." :
        !AppSettings.IsValidApplicationId(Settings.ApplicationId) ? "Cole o Application ID abaixo. Você só precisa fazer isso uma vez." :
        !DiscordConnected ? "Abra o aplicativo do Discord no computador. A conexão é automática." :
        Playback?.State == PlaybackState.Paused ? "A atividade foi removida até você continuar ouvindo." :
        Playback?.State != PlaybackState.Playing ? "Dê o play no Apple Music para compartilhar sua música." :
        Published ? "Pode fechar esta janela. O app continua na bandeja." : "Enviando a faixa para o Discord…");
    public string DiscordLabel => DiscordConnected ? "Discord conectado" : "Discord desconectado";
    public string ToggleLabel => Settings.SharingEnabled ? "Desativar compartilhamento" : "Ativar compartilhamento";

    public PresenceController(SettingsStore store)
    {
        _store = store;
        var loaded = store.Load();
        Settings = loaded.Settings;
        _settingsWarning = loaded.Warning;
        _catalog = new AppleCatalog(_http);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AppleMusicPresence/0.1.0");
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _discord.Configure(Settings.ApplicationId);
        _timer.Start();
        OnTick(this, EventArgs.Empty);
    }

    public void Apply(AppSettings settings)
    {
        _store.Save(settings);
        var lookupChanged = Settings.LookupArtwork != settings.LookupArtwork;
        Settings = settings;
        _settingsWarning = null;
        _discord.Configure(settings.ApplicationId);
        if (lookupChanged || !settings.SharingEnabled)
        {
            _lookupCancellation?.Cancel();
            _lookupCancellation = null;
            _lookupTrack = null;
            _nextLookupAt = DateTimeOffset.MinValue;
            _match = null;
            ArtworkWarning = null;
        }
        Publish();
        Notify();
    }

    private async void OnTick(object? sender, EventArgs args)
    {
        if (_disposed || _polling) return;
        _polling = true;
        try
        {
            _discord.Pump();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var reading = await _media.ReadAsync(timeout.Token);
            if (_disposed) return;
            Playback = reading.Playback;
            _mediaWarning = reading.Warning;
            UpdateArtwork(reading.Artwork);
            if (Playback?.Track != _lookupTrack)
            {
                _lookupCancellation?.Cancel();
                _lookupCancellation = null;
                _match = null;
                ArtworkWarning = null;
                _lookupTrack = Playback?.Track;
                _nextLookupAt = DateTimeOffset.MinValue;
            }
            Publish();
            if (Settings.LookupArtwork && AppSettings.IsValidApplicationId(Settings.ApplicationId) &&
                ShouldShare && Playback is not null && _lookupCancellation is null && DateTimeOffset.UtcNow >= _nextLookupAt)
                _ = LookupAsync(Playback.Track);
        }
        catch (OperationCanceledException)
        {
            if (_disposed) return;
            Playback = null;
            _mediaWarning = "O Apple Music demorou para responder. Tentando novamente…";
            Publish();
        }
        catch (Exception ex)
        {
            if (_disposed) return;
            Playback = null;
            _mediaWarning = "Falha ao atualizar a música. Tentando novamente…";
            AppLog.Write(ex);
            _discord.Publish(null);
        }
        finally
        {
            _polling = false;
            if (!_disposed) Notify();
        }
    }

    private async Task LookupAsync(Track track)
    {
        _lookupTrack = track;
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _lookupCancellation = lookup;
        var token = lookup.Token;
        try
        {
            var match = await _catalog.FindAsync(track, token);
            if (_disposed || token.IsCancellationRequested || Playback?.Track != track || !Settings.LookupArtwork) return;
            _match = match;
            _nextLookupAt = match is null ? DateTimeOffset.UtcNow.AddSeconds(30) : DateTimeOffset.MaxValue;
            ArtworkWarning = match is null ? "Capa não encontrada. Tentaremos novamente em instantes." : null;
            Publish();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_disposed || token.IsCancellationRequested) return;
            _nextLookupAt = DateTimeOffset.UtcNow.AddSeconds(30);
            ArtworkWarning = "Capa online indisponível. A música continua sendo compartilhada.";
            AppLog.Write(ex);
        }
        finally
        {
            if (ReferenceEquals(_lookupCancellation, lookup)) _lookupCancellation = null;
            if (!_disposed) Notify();
        }
    }

    private void UpdateArtwork(byte[]? bytes)
    {
        if (ReferenceEquals(bytes, _artworkBytes)) return;
        _artworkBytes = bytes;
        Artwork = null;
        if (bytes is null) return;
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 256;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            Artwork = image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or ArgumentException)
        { AppLog.Write(ex); }
    }

    private void Publish() => _discord.Publish(PresenceFactory.Create(Playback, Settings.SharingEnabled,
        Settings.LookupArtwork ? _match : null, DateTimeOffset.UtcNow));

    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    private static string Format(TimeSpan time) => time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _lifetime.Cancel();
        _lookupCancellation?.Cancel();
        _discord.Dispose();
        _http.Dispose();
        _lifetime.Dispose();
    }
}
