using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteRec.Audio.Pulse.Native;
using EliteRec.Core.Audio;

namespace EliteRec.Audio.Pulse;

// Перенос схемы PulseIntrospection EliteSIP, с таймаутом, отменой и проверкой eol/operation.
internal static unsafe class PulseIntrospection
{
    internal static AudioDeviceSnapshot Snapshot(PulseConnection connection, CancellationToken token)
    {
        var collector = new Collector();
        var handle = GCHandle.Alloc(collector);
        try
        {
            nint user = GCHandle.ToIntPtr(handle);
            Query(connection, () => LibPulse.ContextGetServerInfo(connection.Context, &OnServer, user), token);
            Query(connection, () => LibPulse.ContextGetSourceInfoList(connection.Context, &OnSource, user), token);
            Query(connection, () => LibPulse.ContextGetSinkInfoList(connection.Context, &OnSink, user), token);
            if (collector.Error is not null) throw new InvalidOperationException(collector.Error);
            if (!collector.HasServer || !collector.SourcesComplete || !collector.SinksComplete)
                throw new InvalidOperationException("Неполный снимок устройств libpulse.");
            return PulseDeviceRules.Snapshot(collector.Sources, collector.Sinks,
                collector.DefaultSource, collector.DefaultSink);
        }
        finally { handle.Free(); }
    }

    internal static void Query(PulseConnection connection, Func<nint> begin, CancellationToken token)
    {
        nint operation;
        using (connection.Lock())
        {
            operation = begin();
            if (operation == 0) throw new InvalidOperationException(LibPulse.StrError(LibPulse.ContextErrno(connection.Context)));
        }
        try
        {
            connection.WaitUntil(() =>
            {
                if (LibPulse.ContextGetState(connection.Context) != LibPulse.ContextReady)
                    throw new InvalidOperationException("Соединение с аудиосервером потеряно.");
                return LibPulse.OperationGetState(operation) != LibPulse.OperationRunning;
            }, token);
            using (connection.Lock())
                if (LibPulse.OperationGetState(operation) != 1) throw new InvalidOperationException("Запрос libpulse отменён сервером.");
        }
        finally
        {
            using (connection.Lock())
            {
                // Отмена под замком до освобождения GCHandle: callback не увидит освобождённый userdata.
                if (LibPulse.OperationGetState(operation) == LibPulse.OperationRunning) LibPulse.OperationCancel(operation);
                LibPulse.OperationUnref(operation);
            }
        }
    }

    private static Collector Of(nint user) => (Collector)GCHandle.FromIntPtr(user).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnServer(nint _, LibPulse.ServerInfo* info, nint user)
    {
        var c = Of(user);
        try
        {
            if (info is null) { c.Error = "Сервер не вернул описание."; return; }
            c.DefaultSource = LibPulse.Utf8(info->DefaultSourceName);
            c.DefaultSink = LibPulse.Utf8(info->DefaultSinkName);
            c.HasServer = true;
        }
        catch (Exception ex) { c.Error = ex.Message; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSource(nint _, LibPulse.DeviceInfo* info, int eol, nint user) =>
        Collect(info, eol, Of(user), AudioDeviceDirection.Capture);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSink(nint _, LibPulse.DeviceInfo* info, int eol, nint user) =>
        Collect(info, eol, Of(user), AudioDeviceDirection.Render);

    private static void Collect(LibPulse.DeviceInfo* info, int eol, Collector collector, AudioDeviceDirection direction)
    {
        try
        {
            if (eol < 0) collector.Error = "Ошибка перечисления устройств libpulse.";
            else if (eol > 0)
            {
                if (direction == AudioDeviceDirection.Capture) collector.SourcesComplete = true;
                else collector.SinksComplete = true;
            }
            else if (info is not null)
            {
                var port = info->ActivePort;
                var node = new PulseNode(LibPulse.Utf8(info->Name) ?? "", LibPulse.Utf8(info->Description) ?? "",
                    direction, Properties(info->Proplist),
                    direction == AudioDeviceDirection.Capture && info->MonitorOfSink != LibPulse.InvalidIndex,
                    port is null ? null : LibPulse.Utf8(port->Name), port is null ? null : LibPulse.Utf8(port->Description),
                    port is not null && port->Available == LibPulse.PortAvailableNo, LibPulse.Utf8(info->MonitorName));
                (direction == AudioDeviceDirection.Capture ? collector.Sources : collector.Sinks).Add(node);
            }
        }
        catch (Exception ex) { collector.Error = ex.Message; }
    }

    private static Dictionary<string, string> Properties(nint list)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list == 0) return properties;
        nint state = 0;
        while (LibPulse.Utf8(LibPulse.ProplistIterate(list, &state)) is string key)
            if (LibPulse.Utf8(LibPulse.ProplistGetsRaw(list, key)) is string value) properties[key] = value;
        return properties;
    }

    private sealed class Collector
    {
        internal readonly List<PulseNode> Sources = [], Sinks = [];
        internal string? DefaultSource, DefaultSink, Error;
        internal bool HasServer, SourcesComplete, SinksComplete;
    }
}
