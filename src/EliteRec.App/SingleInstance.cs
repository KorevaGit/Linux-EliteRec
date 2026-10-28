using Tmds.DBus.Protocol;

namespace EliteRec.App;

/// <summary>
/// Один экземпляр на сеанс: имя <see cref="BusName"/> на шине D-Bus сессии. Второй запуск (ярлык при
/// работающем автозапуске) не открывает второе окно и вторую запись, а просит первый показать окно
/// и выходит. Шина недоступна — запускаемся как есть: лучше лишний экземпляр, чем никакого.
/// </summary>
public sealed class SingleInstance : IPathMethodHandler
{
    public const string BusName = "vip.elitesip.EliteRec";
    private const string ObjectPath = "/vip/elitesip/EliteRec", Interface = "vip.elitesip.EliteRec", ShowMember = "Show";
    private readonly Action _show;
    private static DBusConnection? _bus;

    private SingleInstance(Action show) => _show = show;

    /// <summary>true — мы первые и слушаем «Show»; false — уже запущен другой экземпляр, ему отправлено «Show».</summary>
    public static async Task<bool> ClaimAsync(Action show)
    {
        // Своё соединение: на общем DBusConnection.Session (автоподключение) имя занимать нельзя.
        // Живёт до конца процесса — с ним живёт и имя.
        if (DBusAddress.Session is not { } address) return true;
        var bus = new DBusConnection(address);
        try
        {
            await bus.ConnectAsync();
            if (await bus.TryRequestNameAsync(BusName, RequestNameOptions.None))
            {
                bus.AddMethodHandler(new SingleInstance(show));
                _bus = bus;
                return true;
            }
            await bus.CallMethodAsync(ShowMessage(bus)).WaitAsync(TimeSpan.FromSeconds(5));
            bus.Dispose();
            return false;
        }
        catch (Exception ex) when (ex is DBusErrorReplyException or DBusConnectionException or DBusConnectFailedException
            or InvalidOperationException or IOException or TimeoutException)
        {
            Console.Error.WriteLine("Проверка второго запуска: " + ex.Message);
            bus.Dispose();
            return true;
        }
    }

    private static MessageBuffer ShowMessage(DBusConnection bus)
    {
        using var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(destination: BusName, path: ObjectPath, @interface: Interface, member: ShowMember);
        return writer.CreateMessage();
    }

    public string Path => ObjectPath;
    public bool HandlesChildPaths => false;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        if (context.Request.InterfaceAsString == Interface && context.Request.MemberAsString == ShowMember)
        {
            _show();
            if (!context.NoReplyExpected)
            {
                using var writer = context.CreateReplyWriter(null);
                context.Reply(writer.CreateMessage());
            }
        }
        else context.ReplyUnknownMethodError();
        return ValueTask.CompletedTask;
    }
}
