using Tmds.DBus.Protocol;

namespace EliteRec.App;

/// <summary>
/// Рабочий стол через D-Bus сессии, как в EliteSIP: уведомления (org.freedesktop.Notifications),
/// есть ли кому показать значок в трее (StatusNotifierWatcher), показ файлов в файловом менеджере
/// (org.freedesktop.FileManager1 — Thunar умеет). Любая ошибка — «не получилось», а не падение.
/// </summary>
public static class Desktop
{
    private static DBusConnection Session => DBusConnection.Session ?? throw new InvalidOperationException("Нет сессии D-Bus.");

    public static async Task<bool> NotifyAsync(string summary, string body, string? iconPath)
    {
        try
        {
            await Session.CallMethodAsync(NotifyMessage(summary, body, iconPath));
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusConnectFailedException or DBusReadException or DBusUnexpectedValueException or InvalidOperationException or IOException)
        { Console.Error.WriteLine("Уведомление: " + ex.Message); return false; }
    }

    /// <summary>Зарегистрирован ли хост значков (панель с областью уведомлений). Без него окно в трей не прячем.</summary>
    public static async Task<bool> TrayHostAvailableAsync()
    {
        try
        {
            return await Session.CallMethodAsync(TrayHostMessage(), (message, _) => message.GetBodyReader().ReadVariantValue().GetBool());
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusConnectFailedException or DBusReadException or DBusUnexpectedValueException or InvalidOperationException or IOException or InvalidCastException)
        { return false; }
    }

    /// <summary>Открывает папку с выделенными файлами — чтобы сразу перетащить их в Битрикс.</summary>
    public static async Task<bool> ShowFilesAsync(IEnumerable<string> paths)
    {
        try
        {
            await Session.CallMethodAsync(ShowItemsMessage(paths.Select(p => new Uri(p).AbsoluteUri).ToArray()));
            return true;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusConnectFailedException or DBusReadException or DBusUnexpectedValueException or InvalidOperationException or IOException)
        { return false; }
    }

    // MessageWriter — ref struct: сообщение собирается синхронно, отправляется уже после.
    private static MessageBuffer NotifyMessage(string summary, string body, string? iconPath)
    {
        using var writer = Session.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.Notifications", path: "/org/freedesktop/Notifications",
            @interface: "org.freedesktop.Notifications", member: "Notify", signature: "susssasa{sv}i");
        writer.WriteString("EliteRec");
        writer.WriteUInt32(0);
        writer.WriteString(iconPath ?? "");
        writer.WriteString(summary);
        writer.WriteString(body);
        writer.WriteArray(Array.Empty<string>());
        writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    private static MessageBuffer TrayHostMessage()
    {
        using var writer = Session.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.kde.StatusNotifierWatcher", path: "/StatusNotifierWatcher",
            @interface: "org.freedesktop.DBus.Properties", member: "Get", signature: "ss");
        writer.WriteString("org.kde.StatusNotifierWatcher");
        writer.WriteString("IsStatusNotifierHostRegistered");
        return writer.CreateMessage();
    }

    private static MessageBuffer ShowItemsMessage(string[] uris)
    {
        using var writer = Session.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: "org.freedesktop.FileManager1", path: "/org/freedesktop/FileManager1",
            @interface: "org.freedesktop.FileManager1", member: "ShowItems", signature: "ass");
        writer.WriteArray(uris);
        writer.WriteString("");
        return writer.CreateMessage();
    }
}
