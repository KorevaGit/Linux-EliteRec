using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace EliteRec.App;

public partial class SettingsWindow : Window
{
    private readonly Func<SettingsModel, Task>? _runTest;
    private Process? _player;
    private SettingsModel Model => (SettingsModel)DataContext!;

    public SettingsWindow() : this(new SettingsModel(default, false), null) { }

    /// <param name="runTest">Пробная запись через сессию главного окна; результат — в модель.</param>
    public SettingsWindow(SettingsModel model, Func<SettingsModel, Task>? runTest)
    {
        InitializeComponent();
        DataContext = model;
        _runTest = runTest;
        Closing += (_, e) => { if (Model.IsTesting) e.Cancel = true; else StopPlayer(); };
    }

    private async void OnRunTest(object? sender, RoutedEventArgs e)
    {
        StopPlayer();
        if (_runTest is not null) await _runTest(Model);
    }

    private void OnListen(object? sender, RoutedEventArgs e)
    {
        if (Model.TestFile is not { } file) return;
        StopPlayer();
        // В EliteLinux (PipeWire) есть pw-play, а paplay — только если стоит pulseaudio-utils.
        foreach (string player in new[] { "pw-play", "paplay" })
        {
            try { _player = Process.Start(new ProcessStartInfo(player, [file]) { UseShellExecute = false }); return; }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException ex) { Model.Fail(ex.Message); return; }
        }
        Model.Fail("нет pw-play и paplay");
    }

    private void StopPlayer()
    {
        try { if (_player is { HasExited: false }) _player.Kill(); }
        catch (InvalidOperationException) { }
        _player?.Dispose(); _player = null;
    }

    private async void OnChooseFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = EliteRec.App.Resources.Text.Get("FolderCaption"), AllowMultiple = false,
            SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(Model.FolderLabel),
        });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) Model.Folder = path;
    }

    private void OnDefaultFolder(object? sender, RoutedEventArgs e) => Model.Folder = null;

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
    private void OnSave(object? sender, RoutedEventArgs e) => Close(true);
}
