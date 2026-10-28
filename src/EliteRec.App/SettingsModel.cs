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

    public SettingsModel(RecordingFormat format, bool testAvailable, string? folder = null, string defaultFolder = "",
        double microphoneVolume = 1, double systemVolume = 1)
    {
        Formats = Enum.GetValues<RecordingFormat>()
            .Select(f => new FormatItem(f, f.ToString().ToUpperInvariant(), Text.Get("Format" + f))).ToArray();
        _selected = Formats.Single(f => f.Value == format);
        _testAvailable = testAvailable;
        _folder = folder; DefaultFolder = defaultFolder;
        _microphoneVolume = Volume(microphoneVolume); _systemVolume = Volume(systemVolume);
    }

    public const double MinVolume = .25, MaxVolume = 2;
    private bool _saveMono;

    /// <summary>Кроме стерео-файла писать и общий моно-файл (громкости сторон — только для него).</summary>
    public bool SaveMono
    {
        get => _saveMono;
        set { _saveMono = value; Changed(nameof(SaveMono)); }
    }
    private string? _folder;
    private double _microphoneVolume, _systemVolume;

    /// <summary>Своя папка записей; null — по умолчанию («Записи EliteRec» на рабочем столе).</summary>
    public string? Folder
    {
        get => _folder;
        set { _folder = string.IsNullOrWhiteSpace(value) ? null : value; Changed(nameof(Folder), nameof(FolderLabel), nameof(IsCustomFolder)); }
    }
    public string DefaultFolder { get; }
    public string FolderLabel => _folder ?? DefaultFolder;
    public bool IsCustomFolder => _folder is not null;

    /// <summary>Громкость стороны в общем файле, 1 — как есть (¼…2, т. е. −12…+6 дБ).</summary>
    public double MicrophoneVolume
    {
        get => _microphoneVolume;
        set { _microphoneVolume = Volume(value); Changed(nameof(MicrophoneVolume), nameof(MicrophoneVolumeLabel)); }
    }
    public double SystemVolume
    {
        get => _systemVolume;
        set { _systemVolume = Volume(value); Changed(nameof(SystemVolume), nameof(SystemVolumeLabel)); }
    }
    public string MicrophoneVolumeLabel => Percent(_microphoneVolume);
    public string SystemVolumeLabel => Percent(_systemVolume);

    private static double Volume(double value) => double.IsFinite(value) ? Math.Round(Math.Clamp(value, MinVolume, MaxVolume), 2) : 1;
    private static string Percent(double value) => ((int)Math.Round(value * 100)).ToString(CultureInfo.CurrentCulture) + " %";

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
