using System.Diagnostics;
using EliteRec.Probe.Native;

namespace EliteRec.Probe;

// Адаптировано из EliteSIP PulseConnection: свой threaded mainloop, no-autospawn.
// Ожидание готовности ограничено 5 с; callback звука не занимается ожиданием.
internal sealed class PulseSession : IDisposable
{
    internal nint Loop { get; private set; }
    internal nint Context { get; private set; }
    private bool _started;

    internal static PulseSession Connect()
    {
        var session = new PulseSession();
        try
        {
            session.Loop = Pulse.LoopNew();
            if (session.Loop == 0) throw new InvalidOperationException("pa_threaded_mainloop_new: NULL");
            session.Context = Pulse.ContextNew(Pulse.LoopApi(session.Loop), "EliteRec R0");
            if (session.Context == 0) throw new InvalidOperationException("pa_context_new: NULL");
            if (Pulse.ContextConnect(session.Context, 0, 1, 0) < 0 || Pulse.LoopStart(session.Loop) < 0)
                throw new InvalidOperationException(Pulse.Error(Pulse.ContextError(session.Context)));
            session._started = true;
            session.WaitUntil(() => Pulse.ContextState(session.Context) switch
            {
                4 => true,
                5 or 6 => throw new InvalidOperationException(Pulse.Error(Pulse.ContextError(session.Context))),
                _ => false,
            });
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal void WaitUntil(Func<bool> ready)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            using (Lock()) if (ready()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("libpulse: готовность не наступила за 5 с.");
    }

    internal LoopGuard Lock()
    {
        Pulse.LoopLock(Loop);
        return new LoopGuard(Loop);
    }

    public void Dispose()
    {
        if (Loop == 0) return;
        if (_started) Pulse.LoopStop(Loop);
        if (Context != 0)
        {
            Pulse.ContextDisconnect(Context);
            Pulse.ContextUnref(Context);
            Context = 0;
        }
        Pulse.LoopFree(Loop);
        Loop = 0;
    }

    internal readonly struct LoopGuard(nint loop) : IDisposable
    {
        public void Dispose() => Pulse.LoopUnlock(loop);
    }
}
