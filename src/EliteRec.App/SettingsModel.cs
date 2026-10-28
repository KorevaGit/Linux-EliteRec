using System.ComponentModel;
using System.Globalization;
using EliteRec.App.Resources;
using EliteRec.Encoding;

namespace EliteRec.App;

public sealed record FormatItem(RecordingFormat Value, string Title, string Hint);

/// <summary>
/// Окно настроек: формат применяется только по «Сохранить» (переключатель на главном экране
/// давал случайные щелчки), плюс самопроверка — короткая пробная запись обеих сторон.
/// </summary>
public sealed class SettingsModel : INotifyPropertyChanged
{
    public static readonly TimeSpan TestLength = TimeSpan.FromSeconds(6);
    private FormatItem _selected;
    private bool _testing, _testAvailable;
    private double _micLevel, _systemLevel;
    private bool? _micHeard, _systemHeard;
    private string? _testFile, _testError;
    private int _secondsLeft;

    public SettingsModel(RecordingFormat format, bool testAvailable)
    {
        Formats = Enum.GetValues<RecordingFormat>()
            .Select(f => new FormatItem(f, f.ToString().ToUpperInvariant(), Text.Get("Format" + f))).ToArray();
        _selected = Formats.Single(f => f.Value == format);
        _testAvailable = testAvailable;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<FormatItem> Formats { get; }
    public FormatItem SelectedFormat
    {
        get => _selected;
        set { if (value is null || value == _selected) return; _selected = value; Changed(nameof(SelectedFormat)); }
    }

    public bool IsTesting => _testing;
    public bool IsIdle => !_testing;
    public bool CanTest => _testAvailable && !_testing;
    public double MicLevel => _micLevel;
    public double SystemLevel => _systemLevel;
    public string TestStatus => _testing
        ? string.Format(CultureInfo.CurrentCulture, Text.Get("SelfTestRunning"), _secondsLeft)
        : _testError is { } error ? Text.Get("SelfTestFailed") + " " + error
        : !_testAvailable ? Text.Get("SelfTestUnavailable")
        : string.Format(CultureInfo.CurrentCulture, Text.Get("SelfTestHint"), (int)TestLength.TotalSeconds);
    public bool HasResult => _micHeard is not null;
    public string MicResult => Text.Get(_micHeard == true ? "MicHeard" : "MicNotHeard");
    public string SystemResult => Text.Get(_systemHeard == true ? "SystemHeard" : "SystemNotHeard");
    public bool MicHeard => _micHeard == true;
    public bool SystemHeard => _systemHeard == true;
    public string? TestFile => _testFile;
    public bool CanListen => _testFile is not null && !_testing;

    /// <summary>Сторона «слышна», если пик за пробу громче −46 дБ: шум выключенного микрофона тише, речь — громче.</summary>
    public static bool Heard(float peak) => LevelWave.Level(peak) > .3;

    public void Begin()
    {
        _testing = true; _testError = null; _testFile = null; _micHeard = _systemHeard = null;
        _secondsLeft = (int)TestLength.TotalSeconds;
        ChangedAll();
    }

    public void Report(float microphonePeak, float systemPeak, TimeSpan left)
    {
        _micLevel = LevelWave.Level(microphonePeak); _systemLevel = LevelWave.Level(systemPeak);
        _secondsLeft = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        Changed(nameof(MicLevel), nameof(SystemLevel), nameof(TestStatus));
    }

    public void Finish(float maxMicrophone, float maxSystem, string? file)
    {
        _testing = false; _micLevel = _systemLevel = 0;
        _micHeard = Heard(maxMicrophone); _systemHeard = Heard(maxSystem); _testFile = file;
        ChangedAll();
    }

    public void Fail(string error)
    {
        _testing = false; _micLevel = _systemLevel = 0; _testError = error;
        ChangedAll();
    }

    private void ChangedAll() => Changed(nameof(IsTesting), nameof(IsIdle), nameof(CanTest), nameof(MicLevel), nameof(SystemLevel),
        nameof(TestStatus), nameof(HasResult), nameof(MicResult), nameof(SystemResult), nameof(MicHeard), nameof(SystemHeard),
        nameof(TestFile), nameof(CanListen));

    private void Changed(params string[] names)
    {
        foreach (string name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
