using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using EliteRec.App.Resources;

namespace EliteRec.App;

/// <summary>
/// Значок в трее (как в EliteSIP — Avalonia TrayIcon, StatusNotifierItem): серый — не пишем, красный — запись,
/// жёлтый — пауза или предупреждение об устройстве. Меню: запись / пауза / стоп, окно, папка, выход.
/// Щелчок по значку — показать окно.
/// </summary>
public sealed class Tray : IDisposable
{
    public enum Look { Idle, Recording, Attention }

    private readonly MainPanelModel _model;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _start, _pause, _stop;
    private readonly Dictionary<Look, WindowIcon> _icons;
    private Look? _look;

    public Tray(MainPanelModel model, Action start, Action pause, Action stop, Action show, Action folder, Action quit)
    {
        _model = model;
        _icons = Enum.GetValues<Look>().ToDictionary(look => look, look =>
            new WindowIcon(AssetLoader.Open(new Uri($"avares://EliteRec.App/Assets/tray-{look.ToString().ToLowerInvariant()}.png"))));
        NativeMenuItem Item(string text, Action action)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => action();
            return item;
        }
        _start = Item(Text.Get("TrayStart"), start);
        _pause = Item(model.PauseLabel, pause);
        _stop = Item(Text.Get("TrayStop"), stop);
        var menu = new NativeMenu();
        foreach (var item in new NativeMenuItemBase[] { _start, _pause, _stop, new NativeMenuItemSeparator(),
            Item(Text.Get("TrayShow"), show), Item(Text.Get("OpenFolder"), folder), new NativeMenuItemSeparator(), Item(Text.Get("TrayQuit"), quit) })
            menu.Items.Add(item);
        _icon = new TrayIcon { Menu = menu, IsVisible = true };
        _icon.Clicked += (_, _) => show();
        TrayIcon.SetIcons(Application.Current!, [_icon]);
        model.PropertyChanged += OnModelChanged;
        Update();
    }

    /// <summary>Какой значок показать — вынесено для теста.</summary>
    public static Look LookFor(MainPanelModel model) =>
        model.IsLive && !model.IsWarning ? Look.Recording : model.IsRecording ? Look.Attention : Look.Idle;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainPanelModel.IsRecording) or nameof(MainPanelModel.IsLive) or nameof(MainPanelModel.IsWarning)
            or nameof(MainPanelModel.CanStartRecording) or nameof(MainPanelModel.TimerLabel) or nameof(MainPanelModel.PauseLabel))
            Update();
    }

    private void Update()
    {
        var look = LookFor(_model);
        if (look != _look) { _look = look; _icon.Icon = _icons[look]; }
        _icon.ToolTipText = !_model.IsRecording ? Text.Get("TrayIdleTip")
            : string.Format(CultureInfo.CurrentCulture, Text.Get(_model.IsPaused ? "TrayPausedTip" : "TrayRecordingTip"), _model.TimerLabel);
        _start.IsVisible = !_model.IsRecording; _start.IsEnabled = _model.CanStartRecording;
        _pause.IsVisible = _stop.IsVisible = _model.IsRecording;
        _pause.Header = _model.PauseLabel; _pause.IsEnabled = _model.CanPauseRecording; _stop.IsEnabled = _model.CanStopRecording;
    }

    public void Dispose()
    {
        _model.PropertyChanged -= OnModelChanged;
        _icon.IsVisible = false;
        TrayIcon.SetIcons(Application.Current!, []);
        _icon.Dispose();
    }
}
