namespace EliteRec.Core.Audio;

public enum AudioDeviceDirection { Capture, Render }

/// <param name="Id">Постоянное имя source/sink, не номер и не отображаемое имя.</param>
/// <param name="CaptureSourceId">Для вывода — реальное имя его monitor-source.</param>
public sealed record AudioDevice(string Id, string DisplayName, AudioDeviceDirection Direction,
    bool IsAvailable = true, string? PortName = null, string? CaptureSourceId = null);

public sealed record AudioDeviceSnapshot(IReadOnlyList<AudioDevice> Inputs, IReadOnlyList<AudioDevice> Outputs,
    string? DefaultInputId, string? DefaultOutputId, bool ServerAvailable = true, string? Error = null)
{
    public static AudioDeviceSnapshot Unavailable(string error) => new([], [], null, null, false, error);

    public IReadOnlyList<AudioDevice> Devices(AudioDeviceDirection direction) =>
        direction == AudioDeviceDirection.Capture ? Inputs : Outputs;

    public AudioDevice? Resolve(AudioDeviceDirection direction, string? selectedId)
    {
        string? id = selectedId ?? (direction == AudioDeviceDirection.Capture ? DefaultInputId : DefaultOutputId);
        return Devices(direction).FirstOrDefault(device => device.Id == id && device.IsAvailable);
    }
}

public interface IAudioDeviceCatalog
{
    AudioDeviceSnapshot ReadSnapshot(CancellationToken cancellationToken = default);
}

public enum AudioDeviceChangeKind { Added, Removed, StateChanged, DefaultChanged }
public sealed record AudioDeviceChange(AudioDeviceChangeKind Kind, string DeviceId, AudioDeviceDirection Direction);

/// <summary>Адаптация PulseDeviceWatch.Diff EliteSIP: сравнение по имени, не по индексу сервера.</summary>
public static class AudioDeviceChanges
{
    public static IReadOnlyList<AudioDeviceChange> Between(AudioDeviceSnapshot before, AudioDeviceSnapshot after)
    {
        var result = new List<AudioDeviceChange>();
        foreach (var direction in Enum.GetValues<AudioDeviceDirection>())
        {
            var old = before.Devices(direction).ToDictionary(d => d.Id, StringComparer.Ordinal);
            var current = after.Devices(direction).ToDictionary(d => d.Id, StringComparer.Ordinal);
            foreach (var (id, device) in current)
            {
                if (!old.TryGetValue(id, out var previous)) result.Add(new(AudioDeviceChangeKind.Added, id, direction));
                else if (previous != device) result.Add(new(AudioDeviceChangeKind.StateChanged, id, direction));
            }
            foreach (var id in old.Keys.Where(id => !current.ContainsKey(id)))
                result.Add(new(AudioDeviceChangeKind.Removed, id, direction));
            string? a = direction == AudioDeviceDirection.Capture ? before.DefaultInputId : before.DefaultOutputId;
            string? b = direction == AudioDeviceDirection.Capture ? after.DefaultInputId : after.DefaultOutputId;
            if (!string.Equals(a, b, StringComparison.Ordinal)) result.Add(new(AudioDeviceChangeKind.DefaultChanged, b ?? "", direction));
        }
        return result;
    }
}
