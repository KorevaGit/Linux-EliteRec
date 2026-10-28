using System.Text.Json;

namespace EliteRec.Core.Settings;

// null — явно выбранное «Системный». Отсутствующий конкретный ID остаётся в настройках.
// Format — формат записи (mp3/flac/opus/wav); неизвестное значение приложение читает как mp3.
// Folder — папка записей; null — «Записи EliteRec» на рабочем столе.
// SaveMono — кроме стерео (L — микрофон, R — система) писать и общий моно-файл; по умолчанию нет.
// MicrophoneVolume / SystemVolume — громкости сторон в общем моно-файле, 1 — как есть.
public sealed record DevicePreferences(string? MicrophoneId = null, string? OutputId = null, string Format = "mp3",
    string? Folder = null, double MicrophoneVolume = 1, double SystemVolume = 1, bool SaveMono = false);

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
