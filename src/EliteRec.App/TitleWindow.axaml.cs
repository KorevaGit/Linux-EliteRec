using Avalonia.Controls;
using Avalonia.Interactivity;

namespace EliteRec.App;

/// <summary>
/// Название записи после «Стоп»: видно имя файла, дописывается только название. Результат — введённый
/// текст; закрытие окна — то же, что «Сохранить» с пустым полем (запись уже на диске).
/// </summary>
public partial class TitleWindow : Window
{
    public TitleWindow() : this("2026-10-28_10-32.mp3") { }

    /// <param name="fileName">Имя общего файла записи, например 2026-10-28_10-32.mp3.</param>
    public TitleWindow(string fileName)
    {
        InitializeComponent();
        StampText.Text = Path.GetFileNameWithoutExtension(fileName) + "_";
        ExtensionText.Text = Path.GetExtension(fileName);
        Opened += (_, _) => TitleBox.Focus();
    }

    private void OnSave(object? sender, RoutedEventArgs e) => Close(TitleBox.Text);
}
