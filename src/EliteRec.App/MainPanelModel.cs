using System.ComponentModel;
using System.Globalization;
using EliteRec.App.Resources;
using EliteRec.Core.Audio;
using EliteRec.Core.Settings;
using EliteRec.Core.Recording;

namespace EliteRec.App;

public sealed record DeviceOption(string? Id, string Title)
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
    public string TimerLabel => (_recording?.Duration ?? TimeSpan.Zero).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    public string PauseLabel => Text.Get(_recording?.State == RecordingState.Paused ? "Resume" : "Pause");
    public float MicrophonePeak => Math.Min(1, _recording?.MicrophonePeak ?? 0);
    public float SystemPeak => Math.Min(1, _recording?.SystemPeak ?? 0);
    public string RecordingHint => _commandError ?? _recording?.Error ?? (_recording?.State switch
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

    public void UpdateRecording(RecordingSnapshot? recording, bool busy = false)
    {
        _recording = recording; _busy = busy;
        foreach (string property in new[] { nameof(CanChooseDevices), nameof(CanStartRecording), nameof(CanPauseRecording),
            nameof(CanStopRecording), nameof(IsRecording), nameof(ShowStart), nameof(TimerLabel), nameof(PauseLabel),
            nameof(MicrophonePeak), nameof(SystemPeak), nameof(RecordingHint), nameof(Status) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    public void SetCommandError(string? error)
    {
        _commandError = error;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RecordingHint)));
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
            Microphones = Options(AudioDeviceDirection.Capture, _preferences.MicrophoneId);
            Outputs = Options(AudioDeviceDirection.Render, _preferences.OutputId);
            _microphone = Microphones.Single(d => d.Id == _preferences.MicrophoneId);
            _output = Outputs.Single(d => d.Id == _preferences.OutputId);
            _deviceStatus = !snapshot.ServerAvailable ? Text.Get("ServerMissing")
                : snapshot.Resolve(AudioDeviceDirection.Capture, _preferences.MicrophoneId) is null
                  || snapshot.Resolve(AudioDeviceDirection.Render, _preferences.OutputId) is null ? Text.Get("DeviceMissing")
                  : Text.Get("DevicesReady");
            foreach (string property in new[] { nameof(Microphones), nameof(Outputs), nameof(Microphone), nameof(Output), nameof(Status), nameof(SettingsError) })
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
            return new DeviceOption(choice.Id, title);
        }).ToArray();

    private void Save()
    {
        try { _save(_preferences); SettingsError = _persistentSettingsError; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { SettingsError = Text.Get("SettingsWriteFailed"); }
    }
}
