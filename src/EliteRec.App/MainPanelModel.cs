using System.ComponentModel;
using System.Globalization;
using EliteRec.App.Resources;
using EliteRec.Core.Audio;
using EliteRec.Core.Settings;
using EliteRec.Core.Recording;
using EliteRec.Encoding;

namespace EliteRec.App;

/// <param name="Short">Имя для чипа в окне: само устройство, для «Системного» — текущее умолчание.</param>
public sealed record DeviceOption(string? Id, string Title, string? Short = null)
{
    /// <summary>«Микрофон (JBL Quantum 350)» → «JBL Quantum 350»: в чипе важно устройство, а не его роль.</summary>
    public string Chip => Short is { } s && s.IndexOf(" (", StringComparison.Ordinal) is var open and > 0 && s.EndsWith(')')
        ? s[(open + 2)..^1] : Short ?? Title;
    public override string ToString() => Title;
}

public sealed record FormatOption(RecordingFormat Value, string Title)
{
    public override string ToString() => Title;
}

public sealed class MainPanelModel : INotifyPropertyChanged
{
    private DevicePreferences _preferences;
    private readonly Action<DevicePreferences> _save;
    private readonly string? _persistentSettingsError;
    private AudioDeviceSnapshot _snapshot = AudioDeviceSnapshot.Unavailable("");
    private bool _updating;
    private DeviceOption? _microphone, _output;
    private RecordingSnapshot? _recording;
    private bool _busy;
    private string? _commandError;
    private string _deviceStatus = Text.Get("Connecting");
    public event Action? SelectionChanged;
    public DevicePreferences Preferences => _preferences;
    public bool CanChooseDevices => !_busy && !IsRecording;
    public bool CanStartRecording => !_busy && _recording?.State == RecordingState.Preview;
    public bool CanPauseRecording => !_busy && IsRecording;
    public bool CanStopRecording => !_busy && IsRecording;
    public bool IsRecording => _recording?.State is RecordingState.Recording or RecordingState.Paused;
    public bool ShowStart => !IsRecording;
    public bool IsPaused => _recording?.State == RecordingState.Paused;
    public bool IsLive => _recording?.State == RecordingState.Recording;
    public string TimerLabel => (_recording?.Duration ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public string PauseLabel => Text.Get(_recording?.State == RecordingState.Paused ? "Resume" : "Pause");
    public float MicrophonePeak => Math.Min(1, _recording?.MicrophonePeak ?? 0);
    public float SystemPeak => Math.Min(1, _recording?.SystemPeak ?? 0);
    public string RecordingHint => _commandError ?? _recording?.Error ?? DeviceWarning ?? (_recording?.State switch
    {
        RecordingState.Recording => Text.Get("RecordingNotice"),
        RecordingState.Paused => Text.Get("PausedNotice"),
        RecordingState.Preview when _recording?.Files is { } files => Text.Get("Saved") + " " + Path.GetFileName(files.MixPath),
        RecordingState.Preview => Text.Get("LivePreview"),
        _ => Text.Get("AwaitingAudio"),
    });
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<DeviceOption> Microphones { get; private set; } = [];
    public IReadOnlyList<DeviceOption> Outputs { get; private set; } = [];
    public string Status => _recording?.State switch
    {
        RecordingState.Recording => Text.Get("RecordingStatus"),
        RecordingState.Preview when _deviceStatus == Text.Get("DevicesReady") => Text.ReadyToRecord,
        RecordingState.Paused => Text.Get("PausedNotice"),
        RecordingState.Faulted => Text.Get("AudioFailure"),
        _ => _deviceStatus,
    };
    public string? SettingsError { get; private set; }
    public DeviceOption? Microphone
    {
        get => _microphone;
        set
        {
            if (value is null || _updating || !CanChooseDevices || _microphone?.Id == value.Id) return;
            _preferences = _preferences with { MicrophoneId = value.Id };
            Save(); Apply(_snapshot); SelectionChanged?.Invoke();
        }
    }
    public DeviceOption? Output
    {
        get => _output;
        set
        {
            if (value is null || _updating || !CanChooseDevices || _output?.Id == value.Id) return;
            _preferences = _preferences with { OutputId = value.Id };
            Save(); Apply(_snapshot); SelectionChanged?.Invoke();
        }
    }

    public LevelWave Wave { get; } = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Жёлтая плашка: пауза, ошибка или предупреждение об устройстве.</summary>
    public bool IsWarning => _commandError is not null || _recording?.Error is not null || DeviceWarning is not null
        || _recording?.State == RecordingState.Paused;

    /// <summary>
    /// Текст в окне — только по делу (макет 6: минимум текста): предупреждение, «Сохранено …» после
    /// записи или состояние, пока звука нет (подключение, устройства недоступны). Иначе всё говорит волна.
    /// </summary>
    public string? Notice => IsWarning || _recording?.Files is not null ? RecordingHint : _recording is null ? Status : null;
    public bool HasNotice => Notice is not null;
    public string MicrophoneLabel => _microphone?.Title ?? "";
    public string OutputLabel => _output?.Title ?? "";

    public void UpdateRecording(RecordingSnapshot? recording, bool busy = false)
    {
        _recording = recording; _busy = busy;
        if (recording is not null)
            Wave.Add(_clock.Elapsed.TotalSeconds, recording.MicrophonePeak, recording.SystemPeak, recording.State == RecordingState.Paused);
        foreach (string property in new[] { nameof(CanChooseDevices), nameof(CanStartRecording), nameof(CanPauseRecording),
            nameof(CanStopRecording), nameof(IsRecording), nameof(ShowStart), nameof(TimerLabel), nameof(PauseLabel),
            nameof(MicrophonePeak), nameof(SystemPeak), nameof(RecordingHint), nameof(Notice), nameof(HasNotice), nameof(Status), nameof(IsWarning), nameof(IsPaused), nameof(IsLive) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public IReadOnlyList<FormatOption> Formats { get; } =
        Enum.GetValues<RecordingFormat>().Select(f => new FormatOption(f, f.ToString().ToUpperInvariant())).ToArray();
    public FormatOption FormatChoice
    {
        get => Formats.Single(f => f.Value == Format);
        set { if (value is not null) Format = value.Value; }
    }
    public RecordingFormat Format
    {
        get => Enum.TryParse(_preferences.Format, ignoreCase: true, out RecordingFormat format) && Enum.IsDefined(format)
            ? format : RecordingFormat.Mp3;
        set
        {
            if (!CanChooseDevices || value == Format) return;
            _preferences = _preferences with { Format = value.ToString().ToLowerInvariant() };
            Save();
            foreach (string property in new[] { nameof(Format), nameof(FormatChoice), nameof(RecordingHint), nameof(Notice), nameof(HasNotice), nameof(SettingsError) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }

    private RecordingRoute? _route;
    private string? _routeError, _silentAlone;

    /// <summary>Предупреждение об устройстве: пишем не с выбранного или канал без источника.</summary>
    public string? DeviceWarning => _routeError is { } error ? Text.Get("SwitchFailed") + " " + error
        : _route is not { } r ? null
        : r.MicrophoneSource is null ? Text.Get("MicrophoneSilent")
        : r.MicrophoneSilent ? Text.Get(r.MicrophoneSource == _silentAlone ? "HeadsetSilentAlone" : "HeadsetSilent")
        : r.SystemSource is null ? Text.Get("SystemSilent")
        : r.MicrophoneFallback ? Text.Get("MicrophoneFallback")
        : r.SystemFallback ? Text.Get("SystemFallback") : null;
    public bool HasDeviceWarning => DeviceWarning is not null;

    /// <param name="silentSource">Молчащий микрофон; если маршрут остался на нём — заменить нечем.</param>
    public void SetRoute(RecordingRoute route, string? error = null, string? silentSource = null)
    {
        string? alone = route.MicrophoneSilent && route.MicrophoneSource == silentSource ? silentSource : null;
        if (route == _route && error == _routeError && alone == _silentAlone) return;
        _route = route; _routeError = error; _silentAlone = alone;
        foreach (string property in new[] { nameof(DeviceWarning), nameof(HasDeviceWarning), nameof(RecordingHint), nameof(Notice), nameof(HasNotice), nameof(IsWarning) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public IncompleteRecording? PendingRecovery { get; private set; }
    public bool HasRecovery => PendingRecovery is not null;
    public string RecoveryText => PendingRecovery is not { } r ? "" : string.Format(CultureInfo.CurrentCulture,
        Text.Get("RecoveryFound"), r.LastWriteTime, r.Duration.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));

    public void SetRecovery(IncompleteRecording? recording)
    {
        PendingRecovery = recording;
        foreach (string property in new[] { nameof(PendingRecovery), nameof(HasRecovery), nameof(RecoveryText) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public void SetCommandError(string? error)
    {
        _commandError = error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingHint)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Notice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNotice)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsWarning)));
    }

    public MainPanelModel(DevicePreferences preferences, Action<DevicePreferences> save, string? settingsError = null)
    {
        _preferences = preferences; _save = save; SettingsError = settingsError;
        _persistentSettingsError = settingsError;
        Apply(_snapshot);
    }

    public void Apply(AudioDeviceSnapshot snapshot)
    {
        _snapshot = snapshot;
        _updating = true;
        try
        {
            // Тот же список — тот же объект: иначе ComboBox сбрасывает выбор и на живом окне пусто.
            var microphones = Options(AudioDeviceDirection.Capture, _preferences.MicrophoneId);
            var outputs = Options(AudioDeviceDirection.Render, _preferences.OutputId);
            if (!microphones.SequenceEqual(Microphones)) Microphones = microphones;
            if (!outputs.SequenceEqual(Outputs)) Outputs = outputs;
            _microphone = Microphones.Single(d => d.Id == _preferences.MicrophoneId);
            _output = Outputs.Single(d => d.Id == _preferences.OutputId);
            _deviceStatus = !snapshot.ServerAvailable ? Text.Get("ServerMissing")
                : snapshot.Resolve(AudioDeviceDirection.Capture, _preferences.MicrophoneId) is null
                  || snapshot.Resolve(AudioDeviceDirection.Render, _preferences.OutputId) is null ? Text.Get("DeviceMissing")
                  : Text.Get("DevicesReady");
            foreach (string property in new[] { nameof(Microphones), nameof(Outputs), nameof(Microphone), nameof(Output), nameof(Status), nameof(Notice), nameof(HasNotice), nameof(SettingsError), nameof(MicrophoneLabel), nameof(OutputLabel) })
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
        finally { _updating = false; }
    }

    private IReadOnlyList<DeviceOption> Options(AudioDeviceDirection direction, string? selected) =>
        DeviceChoices.For(_snapshot, direction, selected).Select(choice =>
        {
            string name = choice.Device?.DisplayName ?? choice.Id ?? "";
            string title = choice.IsSystem ? choice.Device is null ? Text.Get("SystemMissing")
                : string.Format(CultureInfo.CurrentCulture, Text.Get("SystemCurrent"), name)
                : choice.IsMissing ? string.Format(CultureInfo.CurrentCulture, Text.Get("Missing"), name) : name;
            return new DeviceOption(choice.Id, title, choice.Device?.DisplayName);
        }).ToArray();

    private void Save()
    {
        try { _save(_preferences); SettingsError = _persistentSettingsError; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SettingsError = Text.Get("SettingsWriteFailed"); }
    }
}
