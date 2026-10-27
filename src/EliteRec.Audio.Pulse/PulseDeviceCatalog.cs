using EliteRec.Core.Audio;

namespace EliteRec.Audio.Pulse;

/// <summary>Один согласованный снимок источников, выводов и системных устройств через libpulse.</summary>
public sealed class PulseDeviceCatalog : IAudioDeviceCatalog
{
    public AudioDeviceSnapshot ReadSnapshot(CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = PulseConnection.Connect(cancellationToken);
            return PulseIntrospection.Snapshot(connection, cancellationToken);
        }
        catch (Exception ex) when (IsServerFailure(ex)) { return AudioDeviceSnapshot.Unavailable(ex.Message); }
    }

    internal static bool IsServerFailure(Exception ex) => ex is InvalidOperationException or TimeoutException
        or DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException;
}
