using System.Text.Json;

namespace EliteRec.Core.Settings;

// null — явно выбранное «Системный». Отсутствующий конкретный ID остаётся в настройках.
public sealed record DevicePreferences(string? MicrophoneId = null, string? OutputId = null);

public sealed class DevicePreferencesStore(string path)
{
    public DevicePreferences Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<DevicePreferences>(File.ReadAllText(path)) ?? new()
        : new();

    public void Save(DevicePreferences preferences)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(file, preferences);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
