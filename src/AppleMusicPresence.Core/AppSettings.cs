using System.Globalization;
using System.Text.Json;

namespace AppleMusicPresence.Core;

public sealed record AppSettings
{
    public string ApplicationId { get; init; } = "";
    public bool SharingEnabled { get; init; } = true;
    public bool LookupArtwork { get; init; } = true;

    public static bool IsValidApplicationId(string? id) =>
        id is { Length: >= 17 and <= 20 } && id.All(char.IsAsciiDigit) &&
        ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0;
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning = null);

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public SettingsLoadResult Load()
    {
        if (!File.Exists(Path)) return new(new());
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path)) ??
                throw new JsonException("O arquivo de configurações está vazio.");
            if (settings.ApplicationId is null ||
                (settings.ApplicationId.Length > 0 && !AppSettings.IsValidApplicationId(settings.ApplicationId)))
                throw new JsonException("Application ID inválido no arquivo de configurações.");
            return new(settings);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new(new(), "Não foi possível ler as configurações. " + ex.Message);
        }
    }

    public void Save(AppSettings settings)
    {
        if (settings.ApplicationId.Length > 0 && !AppSettings.IsValidApplicationId(settings.ApplicationId))
            throw new ArgumentException("Application ID inválido.");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temp, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
