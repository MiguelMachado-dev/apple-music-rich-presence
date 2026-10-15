using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using AppleMusicPresence.Services;
using Forms = System.Windows.Forms;

namespace AppleMusicPresence;

public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showRegistration;
    private PresenceController? _controller;
    private MainWindow? _window;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private bool _ownsMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args is ["--diagnose", var output])
        {
            await DiagnoseAsync(output);
            return;
        }
        _mutex = new Mutex(true, @"Local\AppleMusicPresence.Instance", out _ownsMutex);
        if (!_ownsMutex)
        {
            try { using var signal = EventWaitHandle.OpenExisting(@"Local\AppleMusicPresence.Show"); signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        try
        {
            _controller = new PresenceController(new SettingsStore(Path.Combine(AppLog.DataDirectory, "settings.json")));
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AppleMusicPresence.Show");
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showSignal,
                (_, _) => Dispatcher.BeginInvoke(() => EnsureWindow().ShowFromTray()), null, Timeout.Infinite, false);
            CreateTray();
            _controller.Start();
            if (!e.Args.Contains("--background") || !AppSettings.IsValidApplicationId(_controller.Settings.ApplicationId))
                EnsureWindow().Show();
        }
        catch (Exception ex)
        {
            AppLog.Write(ex);
            MessageBox.Show("Não foi possível abrir o Apple Music Presence.\n\n" + ex.Message,
                "Apple Music Presence", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // A tray-only start never pays for building the window until the user opens it.
    private MainWindow EnsureWindow()
    {
        if (_window is null)
        {
            _window = new MainWindow(_controller!);
            MainWindow = _window;
        }
        return _window;
    }

    private void CreateTray()
    {
        using var resource = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico")).Stream;
        _icon = new System.Drawing.Icon(resource);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Abrir Apple Music Presence", null, (_, _) => EnsureWindow().ShowFromTray());
        var toggle = menu.Items.Add("Desativar compartilhamento", null, (_, _) =>
        {
            try { _controller!.Apply(_controller.Settings with { SharingEnabled = !_controller.Settings.SharingEnabled }); }
            catch (Exception ex)
            {
                AppLog.Write(ex);
                _tray!.ShowBalloonTip(4000, "Não foi possível salvar", ex.Message, Forms.ToolTipIcon.Error);
            }
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Shutdown());
        _tray = new Forms.NotifyIcon
        {
            Icon = _icon,
            Text = "Apple Music Presence",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) EnsureWindow().ShowFromTray(); };
        _controller!.PropertyChanged += (_, _) =>
        {
            toggle.Text = _controller.ToggleLabel;
            var tooltip = _controller.Playback is { } playback
                ? $"{playback.Track.Title} — {playback.Track.Artist}" : "Apple Music Presence";
            _tray.Text = TextLimits.Truncate(tooltip, 63);
        };
    }

    private async Task DiagnoseAsync(string output)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var reading = await new WindowsMediaReader().ReadAsync(timeout.Token);
            var report = new
            {
                CapturedAt = DateTimeOffset.Now,
                reading.Playback, reading.Warning,
                LocalArtworkBytes = reading.Artwork?.Length ?? 0,
                DiscordRunning = Process.GetProcessesByName("Discord").Length > 0,
                ProposedPresence = PresenceFactory.Create(reading.Playback, true, null, DateTimeOffset.UtcNow),
                Note = "Diagnóstico de leitura. Nenhuma atividade foi enviada ao Discord."
            };
            var path = Path.GetFullPath(output);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(reading.Warning is null ? 0 : 1);
        }
        catch (Exception ex) { AppLog.Write(ex); Shutdown(1); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showRegistration?.Unregister(null);
        _showSignal?.Dispose();
        _controller?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Dispose();
        }
        _icon?.Dispose();
        if (_window is not null) _window.AllowClose = true;
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
