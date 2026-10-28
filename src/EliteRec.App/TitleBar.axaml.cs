using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace EliteRec.App;

/// <summary>Свой заголовок окна (окно без системных рамок): перетаскивание, двойной щелчок — развернуть.</summary>
public partial class TitleBar : UserControl
{
    public TitleBar()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) =>
        {
            if (Owner is not { } window) return;
            TitleText.Text = window.Title;
            MaximizeButton.IsVisible = window.CanResize && window.CanMaximize;
            MinimizeButton.IsVisible = window.CanMinimize;
        };
        PointerPressed += OnPointerPressed;
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Owner is not { } window || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Visual v && v.FindAncestorOfType<Button>(true) is not null) return;
        if (e.ClickCount == 2 && window.CanResize) Toggle(window);
        else window.BeginMoveDrag(e);
    }

    private static void Toggle(Window window) =>
        window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnMinimize(object? sender, RoutedEventArgs e) { if (Owner is { } w) w.WindowState = WindowState.Minimized; }
    private void OnMaximize(object? sender, RoutedEventArgs e) { if (Owner is { } w) Toggle(w); }
    private void OnClose(object? sender, RoutedEventArgs e) => Owner?.Close();
}
