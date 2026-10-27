namespace EliteRec.Core.Audio;

/// <summary>
/// Откуда писать каждый канал прямо сейчас. Source — имя source PulseAudio (для системы —
/// monitor-source вывода); null — писать нечего, канал звучит тишиной.
/// Fallback — выбранное менеджером конкретное устройство пропало, пишем с системного.
/// </summary>
public sealed record RecordingRoute(string? MicrophoneSource, string? SystemSource,
    bool MicrophoneFallback = false, bool SystemFallback = false)
{
    /// <summary>
    /// Правила раздела «Устройства» плана: конкретный выбор не подменяется умолчанием, пока
    /// устройство есть; пропало — временно системное; вернулось — снова оно. «Системный»
    /// (null) следует за текущим умолчанием, в том числе когда оно сменилось во время записи.
    /// </summary>
    public static RecordingRoute For(AudioDeviceSnapshot snapshot, string? microphoneId, string? outputId)
    {
        var microphone = snapshot.Resolve(AudioDeviceDirection.Capture, microphoneId);
        bool microphoneFallback = microphone is null && microphoneId is not null;
        if (microphoneFallback) microphone = snapshot.Resolve(AudioDeviceDirection.Capture, null);

        var output = snapshot.Resolve(AudioDeviceDirection.Render, outputId);
        bool systemFallback = output is null && outputId is not null;
        if (systemFallback) output = snapshot.Resolve(AudioDeviceDirection.Render, null);

        return new(microphone?.Id, output?.CaptureSourceId, microphoneFallback, systemFallback);
    }
}
