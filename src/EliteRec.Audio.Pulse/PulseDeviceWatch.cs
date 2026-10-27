using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteRec.Audio.Pulse.Native;
using EliteRec.Core.Audio;

namespace EliteRec.Audio.Pulse;

/// <summary>
/// Подписка — повод сравнить снимки, как в EliteSIP. Callback только будит worker.
/// Worker склеивает пачку событий, при потере сервера повторно подключается.
/// </summary>
public sealed class PulseDeviceWatch : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _changed = new(false);
    private readonly Action<AudioDeviceSnapshot, IReadOnlyList<AudioDeviceChange>> _onChange;
    private readonly Action<Exception>? _observerError;
    private readonly Task _worker;
    private AudioDeviceSnapshot _last = AudioDeviceSnapshot.Unavailable("Ожидание аудиосервера.");
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public PulseDeviceWatch(Action<AudioDeviceSnapshot, IReadOnlyList<AudioDeviceChange>> onChange,
        Action<Exception>? observerError = null)
    {
        _onChange = onChange;
        _observerError = observerError;
        _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { WatchConnection(); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex) when (PulseDeviceCatalog.IsServerFailure(ex))
            {
                Publish(AudioDeviceSnapshot.Unavailable(ex.Message), force: _last.ServerAvailable);
                _stop.Token.WaitHandle.WaitOne(1000);
            }
        }
    }

    private unsafe void WatchConnection()
    {
        using var connection = PulseConnection.Connect(_stop.Token);
        var self = GCHandle.Alloc(this);
        try
        {
            // Сначала подписка, затем первый снимок: событие между ними не потеряется.
            PulseIntrospection.Query(connection, () =>
            {
                LibPulse.ContextSetSubscribeCallback(connection.Context, &OnEvent, GCHandle.ToIntPtr(self));
                return LibPulse.ContextSubscribe(connection.Context,
                    LibPulse.SubscriptionMaskSink | LibPulse.SubscriptionMaskSource | LibPulse.SubscriptionMaskServer, 0, 0);
            }, _stop.Token);
            Publish(PulseIntrospection.Snapshot(connection, _stop.Token), force: true);
            while (!_stop.IsCancellationRequested)
            {
                bool changed = _changed.WaitOne(1000);
                _stop.Token.ThrowIfCancellationRequested();
                using (connection.Lock())
                    if (LibPulse.ContextGetState(connection.Context) != LibPulse.ContextReady)
                        throw new InvalidOperationException("Аудиосервер отключён.");
                if (!changed) continue;
                if (_stop.Token.WaitHandle.WaitOne(50)) break;
                _changed.Reset();
                Publish(PulseIntrospection.Snapshot(connection, _stop.Token));
            }
        }
        finally
        {
            using (connection.Lock()) LibPulse.ContextSetSubscribeCallback(connection.Context, null, 0);
            self.Free();
        }
    }

    private void Publish(AudioDeviceSnapshot current, bool force = false)
    {
        var changes = AudioDeviceChanges.Between(_last, current);
        bool changed = force || changes.Count != 0 || _last.ServerAvailable != current.ServerAvailable || _last.Error != current.Error;
        _last = current;
        if (!changed || _stop.IsCancellationRequested) return;
        try { _onChange(current, changes); }
        catch (Exception ex) { try { _observerError?.Invoke(ex); } catch { /* Наблюдатель не должен остановить worker. */ } }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnEvent(nint _, int type, uint index, nint user)
    {
        int facility = type & LibPulse.EventFacilityMask;
        if (facility is LibPulse.EventSink or LibPulse.EventSource or LibPulse.EventServer)
            ((PulseDeviceWatch)GCHandle.FromIntPtr(user).Target!)._changed.Set();
    }

    // IAsyncDisposable: callback и GCHandle остаются живы до остановки worker.
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= StopAsync());
    }

    private async Task StopAsync()
    {
        _stop.Cancel();
        _changed.Set();
        try { await _worker.ConfigureAwait(false); }
        finally { _changed.Dispose(); _stop.Dispose(); }
    }
}
