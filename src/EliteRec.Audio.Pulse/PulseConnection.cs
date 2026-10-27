using System.Diagnostics;
using EliteRec.Audio.Pulse.Native;

namespace EliteRec.Audio.Pulse;

// Адаптировано из EliteSIP PulseConnection: свой threaded mainloop, no-autospawn.
// Ожидание готовности ограничено 5 с; callback звука не занимается ожиданием.
internal sealed class PulseConnection : IDisposable
{
    internal nint Loop { get; private set; }
    internal nint Context { get; private set; }
    private bool _started;

    internal static PulseConnection Connect(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Для Pulse-каталога необходим 64-битный Linux.");
        cancellationToken.ThrowIfCancellationRequested();
        var session = new PulseConnection();
        try
        {
            session.Loop = LibPulse.ThreadedMainloopNew();
            if (session.Loop == 0) throw new InvalidOperationException("pa_threaded_mainloop_new: NULL");
            session.Context = LibPulse.ContextNewWithProplist(LibPulse.ThreadedMainloopGetApi(session.Loop), "EliteRec", 0);
            if (session.Context == 0) throw new InvalidOperationException("pa_context_new: NULL");
            if (LibPulse.ContextConnect(session.Context, 0, 1, 0) < 0 || LibPulse.ThreadedMainloopStart(session.Loop) < 0)
                throw new InvalidOperationException(LibPulse.StrError(LibPulse.ContextErrno(session.Context)));
            session._started = true;
            session.WaitUntil(() => LibPulse.ContextGetState(session.Context) switch
            {
                4 => true,
                5 or 6 => throw new InvalidOperationException(LibPulse.StrError(LibPulse.ContextErrno(session.Context))),
                _ => false,
            }, cancellationToken);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    internal void WaitUntil(Func<bool> ready, CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (Lock()) if (ready()) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("libpulse: готовность не наступила за 5 с.");
    }

    internal LoopGuard Lock()
    {
        LibPulse.ThreadedMainloopLock(Loop);
        return new LoopGuard(Loop);
    }

    public void Dispose()
    {
        if (Loop == 0) return;
        if (_started) LibPulse.ThreadedMainloopStop(Loop);
        if (Context != 0)
        {
            LibPulse.ContextDisconnect(Context);
            LibPulse.ContextUnref(Context);
            Context = 0;
        }
        LibPulse.ThreadedMainloopFree(Loop);
        Loop = 0;
    }

    internal readonly struct LoopGuard(nint loop) : IDisposable
    {
        public void Dispose() => LibPulse.ThreadedMainloopUnlock(loop);
    }
}
