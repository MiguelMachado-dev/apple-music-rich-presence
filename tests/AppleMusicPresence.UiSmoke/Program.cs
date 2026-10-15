using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AppleMusicPresence;
using AppleMusicPresence.Core;
using AppleMusicPresence.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.Length > 0 ? args[0] : "out/ui-smoke");
        Directory.CreateDirectory(output);
        var application = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
            Resources = new ResourceDictionary { Source = new Uri("/AppleMusicPresence;component/Theme.xaml", UriKind.Relative) }
        };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var exitCode = 1;
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
        {
            try
            {
                await RunAsync(output);
                exitCode = 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); }
            finally
            {
                application.Shutdown(exitCode);
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });
        Dispatcher.Run();
        return exitCode;
    }

    private static async Task RunAsync(string output)
    {
        var store = new SettingsStore(Path.Combine(output, "isolated-settings.json"));
        store.Save(new AppSettings { LookupArtwork = false });
        using var controller = new PresenceController(store);
        var window = new MainWindow(controller);
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "01-empty.png"));
        var box = (TextBox)window.FindName("ApplicationIdBox");
        box.Text = "not-an-id";
        var save = Find<Button>(window, b => b.Content as string == "Salvar configuração");
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Expect(((TextBlock)window.FindName("FeedbackText")).Text.Contains("numérico"), "Invalid ID has no inline error");
        Expect(box.IsKeyboardFocused, "Invalid ID does not return keyboard focus to input");
        Expect(controller.Settings.ApplicationId == "", "Invalid ID was saved");
        box.Text = "";
        ((TextBlock)window.FindName("FeedbackText")).Text = "";
        Console.WriteLine("PASS setup validation and keyboard focus");

        var toggle = Find<Button>(window, b => b.Content as string == controller.ToggleLabel);
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Expect(!controller.Settings.SharingEnabled && !store.Load().Settings.SharingEnabled, "Sharing toggle did not persist");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Expect(controller.Settings.SharingEnabled, "Sharing toggle did not restore");
        Console.WriteLine("PASS sharing toggle and persistence");

        controller.Start();
        await Task.Delay(2200);
        Expect(controller.Playback is not null, "No live Apple Music session for this local smoke test");
        Expect(!string.IsNullOrEmpty(controller.Playback!.Track.Title), "Live song is blank");
        Expect(controller.Artwork is not null, "Local cover did not load");
        Capture(window, Path.Combine(output, "02-live-setup.png"));
        Console.WriteLine("PASS real Apple Music metadata and artwork rendered");

        ((Expander)window.FindName("SettingsExpander")).IsExpanded = false;
        window.Height = 650;
        window.Width = 440;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ((ScrollViewer)window.FindName("ContentScroll")).ScrollToTop();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "03-compact.png"));
        Expect(window.ActualWidth >= window.MinWidth, "Window smaller than supported layout");
        Console.WriteLine("PASS compact window layout");

        window.Close();
        Expect(!window.IsVisible, "Closing window did not hide it");
        window.ShowFromTray();
        Expect(window.IsVisible && window.WindowState == WindowState.Normal, "Tray reopen did not restore window");
        window.WindowState = WindowState.Minimized;
        Expect(!window.IsVisible, "Minimize did not hide window");
        window.ShowFromTray();
        Console.WriteLine("PASS close, minimize and reopen lifecycle");

        controller.Dispose();
        window.Hide();
        await Task.Delay(100);
        Console.WriteLine("PASS clean shutdown during polling");
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var content = (FrameworkElement)window.Content;
        // Render the same opaque backdrop supplied by the native window.
        if (content is Panel panel) panel.Background = window.Background;
        var image = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(content.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static T Find<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        if (parent is T value && predicate(value)) return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            try { return Find(VisualTreeHelper.GetChild(parent, i), predicate); }
            catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException("Control not found: " + typeof(T).Name);
    }

    private static void Expect(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
