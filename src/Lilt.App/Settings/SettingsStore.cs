using System.Text.Json;
using Lilt.Core;

namespace Lilt.App.Settings;

internal sealed class SettingsStore
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lilt");
    private readonly string _file = Path.Combine(DataDirectory, "settings.json");

    public WidgetSettings Load()
    {
        try { return (JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(_file)) ?? new()).Validated(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Save(WidgetSettings settings)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporary = _file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings.Validated(), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _file, overwrite: true);
    }
}
