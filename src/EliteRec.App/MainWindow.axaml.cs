using System.Globalization;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EliteRec.App.Resources;
using EliteRec.Audio.Pulse;
using EliteRec.Core.Audio;
using EliteRec.Core.Recording;
using EliteRec.Core.Settings;
using EliteRec.Encoding;

namespace EliteRec.App;

public partial class MainWindow : Window
{
    private PulseDeviceWatch? _watch;
    private IRecordingSession? _session;
    private CancellationTokenSource? _previewCancellation;
    private readonly SemaphoreSlim _previewGate = new(1, 1);
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private AudioDeviceSnapshot _devices = AudioDeviceSnapshot.Unavailable("");
    private (string? Mic, string? System) _route;
    private int _generation;
    private bool _closing, _allowClose, _busy, _followingDevices;
    private DateTime _followRetry;
    private MainPanelModel Model => (MainPanelModel)DataContext!;

    public MainWindow() : this(CreateModel())
    {
        Model.SelectionChanged += () => _ = RefreshPreviewAsync();
        _watch = new PulseDeviceWatch((snapshot, changes) => Dispatcher.UIThread.Post(() =>
        {
            if (_closing) return;
            _devices = snapshot;
            Model.Apply(snapshot);
            _ = RefreshPreviewAsync();
            _ = FollowDevicesAsync();
        }), ex => Console.Error.WriteLine(ex.Message));
        _meterTimer.Tick += (_, _) =>
        {
            if (_closing || _session is null) return;
            Model.UpdateRecording(_session.Snapshot, _busy);
            _ = FollowDevicesAsync(); // поток мог умереть и без события об устройстве
        };
        _meterTimer.Start();
        Closing += OnClosing;
        FindIncompleteRecordings();
    }

    internal MainWindow(MainPanelModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private async Task RefreshPreviewAsync()
    {
        if (_closing || Model.IsRecording || _busy) return;
        var desired = RecordingRoute.For(_devices, Model.Preferences.MicrophoneId, Model.Preferences.OutputId);
        var route = (desired.MicrophoneSource, desired.SystemSource);
        if (_session?.Snapshot.State == RecordingState.Preview && _route == route) return;
        int generation = ++_generation;
        _previewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        await _previewGate.WaitAsync();
        try
        {
            if (_closing || generation != _generation) return;
            if (_session is not null) { await _session.DisposeAsync(); _session = null; }
            _route = default;
            Model.UpdateRecording(null, busy: true);
            Model.SetRoute(desired);
            if (desired.MicrophoneSource is not { } microphone || desired.SystemSource is not { } system) return;
            var session = await PulseRecordingSession.OpenAsync(microphone, system, cancellation.Token);
            if (_closing || generation != _generation) { await session.DisposeAsync(); return; }
            _session = session; _route = route;
            Model.SetCommandError(null);
            Model.UpdateRecording(session.Snapshot);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or DllNotFoundException)
        {
            if (!_closing && generation == _generation) ShowError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation)) _previewCancellation = null;
            cancellation.Dispose(); _previewGate.Release();
            if (!_closing && generation == _generation && _session is null) Model.UpdateRecording(null);
        }
    }

    /// <summary>
    /// Во время записи держит источники в согласии с правилами <see cref="RecordingRoute"/>:
    /// пропало выбранное — системное, вернулось — снова оно, «Системный» следует за умолчанием.
    /// </summary>
    private async Task FollowDevicesAsync()
    {
        if (_closing || _followingDevices || !Model.IsRecording || _session is not { } session || DateTime.UtcNow < _followRetry) return;
        var desired = RecordingRoute.For(_devices, Model.Preferences.MicrophoneId, Model.Preferences.OutputId);
        var current = session.Snapshot;
        if (current.MicrophoneSource == desired.MicrophoneSource && current.SystemSource == desired.SystemSource)
        { Model.SetRoute(desired); return; }
        _followingDevices = true;
        try
        {
            if (current.MicrophoneSource != desired.MicrophoneSource)
                await session.SwitchSourceAsync(AudioChannel.Microphone, desired.MicrophoneSource,
                    Note("MicrophoneNote", DeviceName(_devices.Inputs.FirstOrDefault(d => d.Id == desired.MicrophoneSource)), desired.MicrophoneFallback));
            if (current.SystemSource != desired.SystemSource)
                await session.SwitchSourceAsync(AudioChannel.System, desired.SystemSource,
                    Note("SystemNote", DeviceName(_devices.Outputs.FirstOrDefault(d => d.CaptureSourceId == desired.SystemSource)), desired.SystemFallback));
            Model.SetRoute(desired);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            _followRetry = DateTime.UtcNow.AddSeconds(2);
            Model.SetRoute(desired, ex.Message);
        }
        finally { _followingDevices = false; }
    }

    private static string? DeviceName(AudioDevice? device) => device?.DisplayName;

    private static string Note(string key, string? device, bool fallback) =>
        string.Format(CultureInfo.CurrentCulture, Text.Get(key), device ?? Text.Get("NoSource"))
        + (fallback ? " " + Text.Get("FallbackNote") : "");

    private async void OnStartRecording(object? sender, RoutedEventArgs e) => await RunCommandAsync(async session =>
    {
        string name = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + "_" + Guid.NewGuid().ToString("N")[..6];
        var format = Model.Format;
        await session.StartRecordingAsync(() => new RecordingSink(Path.Combine(RecordingsFolder(), name), format));
    });

    private static string RecordingsFolder()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrEmpty(desktop)) desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop");
        return Path.Combine(desktop, Text.Get("RecordingsFolder"));
    }

    private void FindIncompleteRecordings()
    {
        try { Model.SetRecovery(RecordingRecovery.Find(RecordingsFolder()).FirstOrDefault()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
    }

    private void OnRecoverRecording(object? sender, RoutedEventArgs e) => ResolveRecovery(recording =>
    {
        var saved = RecordingRecovery.Recover(recording);
        Model.SetCommandError(Text.Get("Recovered") + " " + string.Join(", ", saved.Select(Path.GetFileName)));
    });

    private void OnDiscardRecording(object? sender, RoutedEventArgs e) => ResolveRecovery(RecordingRecovery.Discard);

    private void ResolveRecovery(Action<IncompleteRecording> action)
    {
        if (Model.PendingRecovery is not { } recording) return;
        try { action(recording); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
        FindIncompleteRecordings();
    }

    private async void OnPauseRecording(object? sender, RoutedEventArgs e) => await RunCommandAsync(session =>
        session.Snapshot.State == RecordingState.Paused ? session.ResumeAsync() : session.PauseAsync());

    private async void OnStopRecording(object? sender, RoutedEventArgs e)
    {
        await RunCommandAsync(session => session.StopRecordingAsync());
        if (!_closing) { Model.Apply(_devices); await RefreshPreviewAsync(); }
    }

    private async Task RunCommandAsync(Func<IRecordingSession, Task> command)
    {
        if (_closing || _busy || _session is null) return;
        _busy = true;
        Model.SetCommandError(null);
        Model.UpdateRecording(_session.Snapshot, busy: true);
        try { await command(_session); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { ShowError(ex.Message); }
        finally
        {
            _busy = false;
            if (!_closing && _session is not null) Model.UpdateRecording(_session.Snapshot);
        }
    }

    private void ShowError(string message) => Model.SetCommandError(message);

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true; _meterTimer.Stop(); _previewCancellation?.Cancel();
        await _previewGate.WaitAsync();
        try
        {
            try { if (_session is not null) await _session.DisposeAsync(); }
            finally { if (_watch is not null) await _watch.DisposeAsync(); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        finally { _previewGate.Release(); _allowClose = true; Close(); }
    }

    private static MainPanelModel CreateModel()
    {
        string basePath = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config && Path.IsPathRooted(config)
            ? config : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var store = new DevicePreferencesStore(Path.Combine(basePath, "EliteRec", "devices.json"));
        DevicePreferences preferences = new();
        string? error = null;
        bool canSave = true;
        try { preferences = store.Load(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { error = Text.Get("SettingsReadFailed"); canSave = false; }
        return new MainPanelModel(preferences, value => { if (canSave) store.Save(value); }, error);
    }
}
