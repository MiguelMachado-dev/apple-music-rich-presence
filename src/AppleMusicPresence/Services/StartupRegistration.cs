using Microsoft.Win32;

namespace AppleMusicPresence.Services;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string EntryName = "AppleMusicPresence";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(EntryName) is string;
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled) key.DeleteValue(EntryName, throwOnMissingValue: false);
        else
        {
            var executable = Environment.ProcessPath ?? throw new IOException("Caminho do aplicativo não encontrado.");
            if (System.IO.Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Abra o executável publicado antes de ativar o início com o Windows.");
            key.SetValue(EntryName, $"\"{executable}\" --background");
        }
    }
}
