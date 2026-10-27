namespace EliteRec.Core.Audio;

/// <summary>
/// Откуда писать каждый канал прямо сейчас. Source — имя source PulseAudio (для системы —
/// monitor-source вывода); null — писать нечего, канал звучит тишиной.
/// Fallback — выбранное менеджером конкретное устройство пропало, пишем с системного.
/// MicrophoneSilent — выбранный микрофон есть, но присылает цифровую тишину (гарнитура выключена).
/// </summary>
public sealed record RecordingRoute(string? MicrophoneSource, string? SystemSource,
    bool MicrophoneFallback = false, bool SystemFallback = false, bool MicrophoneSilent = false)
{
    /// <summary>
    /// Правила раздела «Устройства» плана: конкретный выбор не подменяется умолчанием, пока
    /// устройство есть; пропало — временно системное; вернулось — снова оно. «Системный»
    /// (null) следует за текущим умолчанием, в том числе когда оно сменилось во время записи.
    /// </summary>
    /// <param name="silentMicrophone">
    /// Источник, признанный молчащим (ровно нули дольше порога): обходится как пропавший —
    /// системный, а если системный и есть он, то любой другой доступный микрофон.
    /// </param>
    public static RecordingRoute For(AudioDeviceSnapshot snapshot, string? microphoneId, string? outputId,
        string? silentMicrophone = null)
    {
        var microphone = snapshot.Resolve(AudioDeviceDirection.Capture, microphoneId);
        bool microphoneFallback = microphone is null && microphoneId is not null;
        if (microphoneFallback) microphone = snapshot.Resolve(AudioDeviceDirection.Capture, null);
        bool silent = microphone is not null && microphone.Id == silentMicrophone;
        if (silent)
            microphone = new[] { snapshot.Resolve(AudioDeviceDirection.Capture, null) }
                .Concat(snapshot.Inputs.Where(d => d.IsAvailable))
                .FirstOrDefault(d => d is not null && d.Id != silentMicrophone) ?? microphone;

        var output = snapshot.Resolve(AudioDeviceDirection.Render, outputId);
        bool systemFallback = output is null && outputId is not null;
        if (systemFallback) output = snapshot.Resolve(AudioDeviceDirection.Render, null);

        return new(microphone?.Id, output?.CaptureSourceId, microphoneFallback, systemFallback, silent);
    }
}
