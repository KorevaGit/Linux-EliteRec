using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
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
    private DateTime _followRetry, _silentProbe;
    private string? _silentMicrophone;
    private Tray? _tray;
    private bool _quitting, _toldAboutTray;
    private int _blink;
    private readonly EliteRec.Updates.UpdateCoordination _updates = new();
    private PosixSignalRegistration[] _terminate = [];

    private void OnTerminate(PosixSignalContext context)
    {
        context.Cancel = true;
        _ = Task.Run(async () =>
        {
            try { if (_session is { } session) await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
            finally { _updates.EndRecording(); Environment.Exit(0); }
        });
    }
    private const double SilenceLimit = 3;
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
            // Точка «пишем» мигает раз в 0,8 с — два кадра вместо анимации с частотой экрана.
            TimerDot.Opacity = Model.IsLive && ++_blink / 16 % 2 == 1 ? .3 : 1;
            if (!Model.Animate && !Model.IsRecording) PaceTimer(); // запись кончилась, окно спрятано
            if (!Model.IsRecording && !_busy) _updates.EndRecording(); // «Стоп», сбой — обновлению больше не мешаем
            _ = FollowDevicesAsync(); // поток мог умереть и без события об устройстве
        };
        _meterTimer.Start();
        Closing += OnClosing;
        // Выход из сеанса или выключение: идущая запись дописывается штатно (как «Стоп», без названия),
        // а не остаётся частями для восстановления. Без окна — X-сервер к этому моменту может уже уйти.
        _terminate = [PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnTerminate), PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnTerminate)];
        PropertyChanged += (_, e) =>
        {
            if (e.Property != IsVisibleProperty && e.Property != WindowStateProperty) return;
            Model.Animate = IsVisible && WindowState != WindowState.Minimized;
            // Окно не видно и запись не идёт — захват ни к чему: на слабом ПК это несколько процентов
            // ядра весь день. Вернётся окно — захват откроется снова.
            if (Model.Animate) _ = RefreshPreviewAsync();
            else _ = SuspendPreviewAsync();
            PaceTimer();
        };
        FindIncompleteRecordings();
        _ = StartTrayAsync();
    }

    // Трей — только если панель его покажет (как в EliteSIP): иначе спрятанное окно не вернуть.
    private async Task StartTrayAsync()
    {
        bool trayHost = await Desktop.TrayHostAvailableAsync();
        // Автозапуск: окно спрятано в трей; трея нет — окно свёрнуто в панель задач, а не закрыто.
        if (Program.StartInTray)
        {
            if (trayHost && !_closing) Hide();
            else WindowState = WindowState.Minimized;
            Opacity = 1;
        }
        if (!trayHost || _closing) return;
        _tray = new Tray(Model,
            start: StartFromTrayAsync,
            pause: () => OnPauseRecording(null, new RoutedEventArgs()),
            stop: () => OnStopRecording(null, new RoutedEventArgs()),
            show: ShowFromTray,
            folder: () => OnOpenFolder(null, new RoutedEventArgs()),
            quit: () => { _quitting = true; Close(); });
    }

    // В трее до записи захват закрыт (экономия на слабом ПК) — сначала открыть его, потом писать.
    private async void StartFromTrayAsync()
    {
        if (_session is null) await RefreshPreviewAsync(force: true);
        OnStartRecording(null, new RoutedEventArgs());
    }

    internal void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal MainWindow(MainPanelModel model)
    {
        InitializeComponent();
        DataContext = model;
        if (Program.StartInTray) Opacity = 0; // не мелькнуть окном, пока выясняется, есть ли трей
        foreach (var grip in ResizeGrips.Children.OfType<Border>())
            grip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed && Enum.TryParse(grip.Tag as string, out WindowEdge edge))
                    BeginResizeDrag(edge, e);
            };
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainPanelModel.Microphone) or nameof(MainPanelModel.Microphones)
                or nameof(MainPanelModel.Output) or nameof(MainPanelModel.Outputs)) ShowChosenDevices();
        };
        ShowChosenDevices();
    }

    private bool _showingDevices;

    private void ShowChosenDevices()
    {
        _showingDevices = true;
        try { MicrophoneBox.SelectedItem = Model.Microphone; OutputBox.SelectedItem = Model.Output; }
        finally { _showingDevices = false; }
    }

    private void OnDeviceChosen(object? sender, SelectionChangedEventArgs e)
    {
        if (_showingDevices || sender is not ComboBox { SelectedItem: DeviceOption choice } box) return;
        if (box == MicrophoneBox) Model.Microphone = choice; else Model.Output = choice;
        ShowChosenDevices(); // выбор могли не принять (запись идёт) — вернуть показанный
    }

    private async Task RefreshPreviewAsync(bool force = false)
    {
        if (_closing || Model.IsRecording || _busy || (!Model.Animate && !force)) return;
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
    /// Таймер окна будит интерфейс: 20 раз в секунду — около 4 % ядра даже впустую (замер 28 октября).
    /// Окно видно — 50 мс ради плавной волны; спрятано и пишем — 0,5 с (переключение устройств,
    /// подсказка трея); спрятано и не пишем — стоит.
    /// </summary>
    private void PaceTimer()
    {
        if (_closing) return;
        if (Model.Animate) { _meterTimer.Interval = TimeSpan.FromMilliseconds(50); _meterTimer.Start(); }
        else if (Model.IsRecording) { _meterTimer.Interval = TimeSpan.FromMilliseconds(500); _meterTimer.Start(); }
        else _meterTimer.Stop();
    }

    private async Task SuspendPreviewAsync()
    {
        if (_closing || _busy || Model.IsRecording || _session is null) return;
        _previewCancellation?.Cancel();
        await _previewGate.WaitAsync();
        try
        {
            if (_closing || _busy || Model.Animate || _session is not { } session || session.Snapshot.State != RecordingState.Preview) return;
            _session = null; _route = default;
            await session.DisposeAsync();
            Model.UpdateRecording(null);
        }
        finally { _previewGate.Release(); }
    }

    /// <summary>
    /// Во время записи держит источники в согласии с правилами <see cref="RecordingRoute"/>:
    /// пропало выбранное — системное, вернулось — снова оно, «Системный» следует за умолчанием.
    /// Выбранный микрофон, присылающий ровно нули дольше <see cref="SilenceLimit"/> (гарнитура
    /// выключена, приёмник в USB), обходится так же; раз в 2 с проба вернуться — только если он зазвучал.
    /// </summary>
    private async Task FollowDevicesAsync()
    {
        if (_closing || _followingDevices || !Model.IsRecording || _session is not { } session || DateTime.UtcNow < _followRetry) return;
        var current = session.Snapshot;
        var preferred = RecordingRoute.For(_devices, Model.Preferences.MicrophoneId, Model.Preferences.OutputId);
        if (_silentMicrophone is null && current.MicrophoneSource is { } source && source == preferred.MicrophoneSource
            && current.MicrophoneSilenceSeconds > SilenceLimit)
            _silentMicrophone = source;
        else if (_silentMicrophone is { } silent && (preferred.MicrophoneSource != silent
            || (current.MicrophoneSource == silent && current.MicrophoneSilenceSeconds < 1)))
            _silentMicrophone = null; // выбор сменился, устройство пропало или снова звучит
        _followingDevices = true;
        try
        {
            if (_silentMicrophone is { } probe && current.MicrophoneSource != probe && DateTime.UtcNow >= _silentProbe)
            {
                _silentProbe = DateTime.UtcNow.AddSeconds(2);
                if (await session.SwitchSourceAsync(AudioChannel.Microphone, probe,
                        Note("MicrophoneNote", DeviceName(_devices.Inputs.FirstOrDefault(d => d.Id == probe)), false), requireSound: true))
                { _silentMicrophone = null; current = session.Snapshot; }
            }
            var desired = RecordingRoute.For(_devices, Model.Preferences.MicrophoneId, Model.Preferences.OutputId, _silentMicrophone);
            if (current.MicrophoneSource != desired.MicrophoneSource)
                await session.SwitchSourceAsync(AudioChannel.Microphone, desired.MicrophoneSource,
                    Note("MicrophoneNote", DeviceName(_devices.Inputs.FirstOrDefault(d => d.Id == desired.MicrophoneSource)),
                        desired.MicrophoneFallback, desired.MicrophoneSilent));
            if (current.SystemSource != desired.SystemSource)
                await session.SwitchSourceAsync(AudioChannel.System, desired.SystemSource,
                    Note("SystemNote", DeviceName(_devices.Outputs.FirstOrDefault(d => d.CaptureSourceId == desired.SystemSource)), desired.SystemFallback));
            Model.SetRoute(desired, silentSource: _silentMicrophone);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            _followRetry = DateTime.UtcNow.AddSeconds(2);
            Model.SetRoute(RecordingRoute.For(_devices, Model.Preferences.MicrophoneId, Model.Preferences.OutputId, _silentMicrophone), ex.Message, _silentMicrophone);
        }
        finally { _followingDevices = false; }
    }

    private static string? DeviceName(AudioDevice? device) => device?.DisplayName;

    private static string Note(string key, string? device, bool fallback, bool silent = false) =>
        string.Format(CultureInfo.CurrentCulture, Text.Get(key), device ?? Text.Get("NoSource"))
        + (fallback ? " " + Text.Get("FallbackNote") : silent ? " " + Text.Get("SilentNote") : "");

    private async void OnStartRecording(object? sender, RoutedEventArgs e)
    {
        // «Не обновлять во время записи»: сначала своя отметка «пишу», потом проверка установки (UpdateCoordination).
        if (!_updates.TryBeginRecording(Environment.ProcessId)) { ShowError(Text.Get("UpdateInstalling")); return; }
        bool started = false;
        await RunCommandAsync(async session =>
        {
            _silentMicrophone = null;
            var format = Model.Format;
            string folder = RecordingsFolder();
            Directory.CreateDirectory(folder);
            string basePath = RecordingNames.NewBase(folder, DateTime.Now, format);
            var preferences = Model.Preferences;
            session.SetMixLevels((float)preferences.MicrophoneVolume, (float)preferences.SystemVolume);
            await session.StartRecordingAsync(() => new RecordingSink(basePath, format, mono: preferences.SaveMono));
            started = true;
        });
        if (!started) _updates.EndRecording();
        PaceTimer();
        // План: «При старте записи — „Идёт запись — предупредите клиента“».
        if (started) await Desktop.NotifyAsync(Text.Get("RecordingStartedTitle"), Text.Get("RecordingStartedBody"), IconFile());
    }

    private async void OnOpenSettings(object? sender, RoutedEventArgs e)
    {
        var preferences = Model.Preferences;
        var settings = new SettingsModel(Model.Format, Model.CanStartRecording, preferences.Folder, DefaultRecordingsFolder(),
            preferences.MicrophoneVolume, preferences.SystemVolume) { SaveMono = preferences.SaveMono };
        string folder = Path.Combine(Path.GetTempPath(), "EliteRec-check-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            if (await new SettingsWindow(settings, s => RunSelfTestAsync(s, folder)).ShowDialog<bool>(this))
            {
                Model.ApplySettings(settings.SelectedFormat.Value, settings.Folder, settings.SaveMono, settings.MicrophoneVolume, settings.SystemVolume);
                FindIncompleteRecordings(); // в новой папке могут быть свои незавершённые записи
            }
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Console.Error.WriteLine(ex.Message); }
        }
    }

    /// <summary>
    /// Самопроверка: короткая настоящая запись (WAV во временную папку) через ту же сессию, что и
    /// обычная запись, — проверяет весь путь звука, а не только индикатор. Потом сессия открывается заново,
    /// чтобы на главном экране не осталось «Сохранено …» от пробы.
    /// </summary>
    private async Task RunSelfTestAsync(SettingsModel settings, string folder)
    {
        if (_closing || _busy || _session is not { } session || Model.IsRecording) return;
        if (!_updates.TryBeginRecording(Environment.ProcessId)) { settings.Fail(Text.Get("UpdateInstalling")); return; }
        _busy = true;
        settings.Begin();
        float microphone = 0, system = 0;
        try
        {
            Directory.CreateDirectory(folder);
            await session.StartRecordingAsync(() => new RecordingSink(Path.Combine(folder, "check"), RecordingFormat.Wav));
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < SettingsModel.TestLength && !_closing)
            {
                await Task.Delay(50);
                var snapshot = session.Snapshot;
                microphone = Math.Max(microphone, snapshot.MicrophonePeak); system = Math.Max(system, snapshot.SystemPeak);
                settings.Report(snapshot.MicrophonePeak, snapshot.SystemPeak, SettingsModel.TestLength - clock.Elapsed);
            }
            await session.StopRecordingAsync();
            settings.Finish(microphone, system, session.Snapshot.Files?.StereoPath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        { settings.Fail(ex.Message); }
        finally
        {
            _updates.EndRecording();
            _busy = false;
            _route = default; // пересоздать сессию предпрослушивания
            if (!_closing) await RefreshPreviewAsync();
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e) => OpenFolder(RecordingsFolder());

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open", [folder]) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { ShowError(ex.Message); }
    }

    /// <summary>Иконка для уведомлений: путь к файлу (avares:// сервер уведомлений не прочтёт), раз в кэш.</summary>
    private static string? IconFile()
    {
        // Из пакета значок стоит в теме — уведомлениям хватает имени.
        if (File.Exists("/usr/share/icons/hicolor/256x256/apps/eliterec.png")) return "eliterec";
        try
        {
            string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } c && Path.IsPathRooted(c)
                ? c : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            string path = Path.Combine(cache, "EliteRec", "eliterec.png");
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://EliteRec.App/Assets/eliterec.png"));
                using var target = File.Create(path);
                source.CopyTo(target);
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private string RecordingsFolder() => Model.Preferences.Folder ?? DefaultRecordingsFolder();

    private static string DefaultRecordingsFolder()
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
        _updates.EndRecording();
        var files = _session?.Snapshot.Files;
        if (!_closing) { Model.Apply(_devices); await RefreshPreviewAsync(); }
        if (files is null || _closing || !File.Exists(files.StereoPath)) return;
        await SaveAsync(files);
    }

    /// <summary>
    /// План: после «Стоп» — «Название записи (необязательно)» → сохранение → папка с выделенными
    /// файлами, чтобы перетащить их в Битрикс. Запись уже на диске; название только дописывается в имя.
    /// </summary>
    private async Task SaveAsync(RecordingFiles files)
    {
        if (!IsVisible) ShowFromTray();
        string? title = await new TitleWindow(Path.GetFileName(files.StereoPath)).ShowDialog<string?>(this);
        try { files = RecordingNames.AddTitle(files, title); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowError(ex.Message); }
        Model.SetSaved(files);
        if (!await Desktop.ShowFilesAsync(files.All)) OpenFolder(Path.GetDirectoryName(files.StereoPath)!);
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
        // С треем крестик прячет окно (запись продолжается); выходят через «Выход» в меню значка.
        if (_tray is not null && !_quitting)
        {
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                await Desktop.NotifyAsync(Text.Get("HiddenToTrayTitle"), Text.Get("HiddenToTrayBody"), IconFile());
            }
            return;
        }
        if (_closing) return;
        _closing = true; _meterTimer.Stop(); _previewCancellation?.Cancel();
        await _previewGate.WaitAsync();
        try
        {
            try { if (_session is not null) await _session.DisposeAsync(); }
            finally { if (_watch is not null) await _watch.DisposeAsync(); }
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        finally { _previewGate.Release(); _tray?.Dispose(); _tray = null; _updates.EndRecording(); _allowClose = true; Close(); }
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
