using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace EliteRec.App;

/// <summary>Значок из Icons.axaml по имени (<see cref="Kind"/>), растянутый под Width/Height; цвет — Foreground.</summary>
public sealed class Icon : ContentControl
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == KindProperty) Load();
    }

    // В шаблонах Kind задаётся раньше, чем значок попадает в дерево, — тогда ресурс находится только здесь.
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        if (Content is null) Load();
    }

    private void Load()
    {
        object? shape = null;
        bool found = Kind is { } kind && (this.TryFindResource(kind, out shape)
            || Application.Current?.TryGetResource(kind, ActualThemeVariant, out shape) == true);
        Content = found && shape is Control control ? new Viewbox { Child = control } : null;
    }
}
