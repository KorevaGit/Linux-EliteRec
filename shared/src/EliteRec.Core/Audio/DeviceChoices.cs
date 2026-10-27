namespace EliteRec.Core.Audio;

/// <summary>Семантика выбора для интерфейса; текст переводится в оболочке.</summary>
public sealed record DeviceChoice(string? Id, AudioDevice? Device, bool IsSystem, bool IsMissing);

public static class DeviceChoices
{
    public static IReadOnlyList<DeviceChoice> For(AudioDeviceSnapshot snapshot, AudioDeviceDirection direction, string? selectedId)
    {
        var result = new List<DeviceChoice> { new(null, snapshot.Resolve(direction, null), true, false) };
        result.AddRange(snapshot.Devices(direction).Select(d => new DeviceChoice(d.Id, d, false, !d.IsAvailable)));
        if (selectedId is not null && result.All(c => c.Id != selectedId))
            result.Add(new(selectedId, null, false, true));
        return result;
    }
}
