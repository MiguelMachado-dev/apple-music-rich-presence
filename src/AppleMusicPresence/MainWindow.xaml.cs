using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using AppleMusicPresence.Services;

namespace AppleMusicPresence;

public partial class MainWindow : Window
{
    private readonly PresenceController _controller;
    private readonly StartupRegistration _startup = new();
    internal bool AllowClose { get; set; }

    public MainWindow(PresenceController controller)
    {
        InitializeComponent();
        _controller = controller;
        DataContext = controller;
        ApplicationIdBox.Text = controller.Settings.ApplicationId;
        ArtworkCheckBox.IsChecked = controller.Settings.LookupArtwork;
        try { StartupCheckBox.IsChecked = _startup.IsEnabled(); }
        catch (Exception ex) { FeedbackText.Text = "Não foi possível ler o início com o Windows."; AppLog.Write(ex); }
        SettingsExpander.IsExpanded = !AppSettings.IsValidApplicationId(controller.Settings.ApplicationId);
    }

    private void SaveSettings(object sender, RoutedEventArgs e)
    {
        var id = ApplicationIdBox.Text.Trim();
        if (!AppSettings.IsValidApplicationId(id))
        {
            FeedbackText.Text = "Cole o Application ID numérico de 17 a 20 dígitos. Não use um token.";
            ApplicationIdBox.Focus();
            return;
        }
        try
        {
            _controller.Apply(_controller.Settings with { ApplicationId = id, LookupArtwork = ArtworkCheckBox.IsChecked == true });
            var startupEnabled = StartupCheckBox.IsChecked == true;
            if (_startup.IsEnabled() != startupEnabled) _startup.SetEnabled(startupEnabled);
            FeedbackText.Text = "Configuração salva. A conexão com o Discord é automática.";
        }
        catch (Exception ex)
        {
            FeedbackText.Text = "Não foi possível salvar tudo. " + ex.Message;
            AppLog.Write(ex);
        }
    }

    private void ToggleSharing(object sender, RoutedEventArgs e)
    {
        try { _controller.Apply(_controller.Settings with { SharingEnabled = !_controller.Settings.SharingEnabled }); }
        catch (Exception ex) { FeedbackText.Text = "Não foi possível salvar a preferência. " + ex.Message; AppLog.Write(ex); }
    }

    private void OpenPortal(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://discord.com/developers/applications") { UseShellExecute = true }); }
        catch (Exception ex) { FeedbackText.Text = "Abra discord.com/developers/applications no navegador."; AppLog.Write(ex); }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) Hide();
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void SettingsCollapsed(object sender, RoutedEventArgs e) => ContentScroll.ScrollToTop();
}
