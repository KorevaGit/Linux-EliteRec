using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteRec.Probe.Native;

namespace EliteRec.Probe;

internal sealed record CaptureTick(double Seconds, long MicFrames, long SystemFrames,
    double? MicServerSeconds, double? SystemServerSeconds, double? MicLatencyMs, double? SystemLatencyMs);
internal sealed record CaptureChannel(string RequestedDevice, string ActualDevice, long Frames,
    int Holes, int Overflows, int ReadErrors, double Peak, long ClippedFrames,
    double? CallbackP95Ms, double MaxCallbackGapMs, double? DeliveredDriftPpm, double? ServerDriftPpm);
internal sealed record CaptureReport(DateTimeOffset StartedUtc, string Server, int RequestedSeconds,
    double MeasuredSeconds, int SampleRate, string? Failure, CaptureChannel Microphone,
    CaptureChannel SystemAudio, double? RelativeDeliveredDriftPpm, double? RelativeServerDriftPpm,
    double? RelativeDeliveredChangeMs, CaptureTick[] Timeline);

internal static class CaptureProbe
{
    internal static CaptureReport Run(ProbeOptions options)
    {
        var environment = AudioEnvironment.Read();
        string source = options.Source ?? environment.DefaultSource;
        string sink = options.Sink ?? environment.DefaultSink;
        if (!environment.Sources.Any(d => d.Name == source)) throw new ArgumentException($"Нет источника {source}");
        var output = environment.Sinks.SingleOrDefault(d => d.Name == sink)
            ?? throw new ArgumentException($"Нет вывода {sink}");
        string monitor = output.MonitorSource ?? sink + ".monitor";
        if (!environment.Sources.Any(d => d.Name == monitor)) throw new ArgumentException($"Нет монитора {monitor}");
        if (source == monitor) throw new ArgumentException("Микрофон и системный звук должны быть разными источниками.");

        using var session = PulseSession.Connect();
        using var mic = CaptureStream.Open(session, source, "EliteRec R0 microphone");
        using var system = CaptureStream.Open(session, monitor, "EliteRec R0 system monitor");
        var started = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        List<CaptureTick> timeline = [];
        string? failure = null;
        Console.Error.WriteLine($"Захват {options.Seconds} с. Только статистика, аудио не сохраняется.");
        while (clock.Elapsed.TotalSeconds < options.Seconds)
        {
            Thread.Sleep(250);
            using (session.Lock())
            {
                double now = clock.Elapsed.TotalSeconds;
                if (Pulse.ContextState(session.Context) != 4 || !mic.IsReady || !system.IsReady)
                {
                    failure = "Сервер или поток отключён. Автовосстановление относится к R4.";
                    break;
                }
                timeline.Add(new(now, mic.Frames, system.Frames, mic.ServerTime(), system.ServerTime(),
                    mic.Latency(), system.Latency()));
            }
        }
        using (session.Lock())
        {
            var a = mic.Report(timeline.Select(p => new ClockPoint(p.Seconds, p.MicFrames / 48000.0)),
                timeline.Select(p => new ClockPoint(p.Seconds, p.MicServerSeconds ?? double.NaN)));
            var b = system.Report(timeline.Select(p => new ClockPoint(p.Seconds, p.SystemFrames / 48000.0)),
                timeline.Select(p => new ClockPoint(p.Seconds, p.SystemServerSeconds ?? double.NaN)));
            var settled = timeline.Where(t => t.Seconds >= 3).ToArray();
            double? delta = settled.Length < 2 ? null :
                ((settled[^1].MicFrames - settled[^1].SystemFrames)
                - (settled[0].MicFrames - settled[0].SystemFrames)) / 48.0;
            if (a.ReadErrors + b.ReadErrors != 0) failure ??= "Ошибки чтения libpulse, см. ReadErrors.";
            if (a.Frames == 0 || b.Frames == 0) failure ??= "Один из потоков не доставил отсчётов.";
            return new(started, environment.Server, options.Seconds, clock.Elapsed.TotalSeconds, 48000,
                failure, a, b, a.DeliveredDriftPpm - b.DeliveredDriftPpm,
                a.ServerDriftPpm - b.ServerDriftPpm, delta, timeline.ToArray());
        }
    }
}

// Два record-stream на одном контексте; кодирование и файловый I/O в callback отсутствуют.
// Схема mainloop/peek/drop адаптирована из EliteSIP.Audio.Pulse.
internal sealed unsafe class CaptureStream : IDisposable
{
    private readonly PulseSession _session;
    private readonly string _requested;
    private nint _stream;
    private GCHandle _self;
    private long _lastCallback;
    private readonly List<double> _intervals = [];
    private int _holes, _overflows, _readErrors;
    private double _peak, _maxGap;
    private long _clipped;
    internal long Frames { get; private set; }
    internal bool IsReady => Pulse.StreamState(_stream) == Pulse.Ready;

    private CaptureStream(PulseSession session, string device) { _session = session; _requested = device; }

    internal static CaptureStream Open(PulseSession session, string device, string name)
    {
        var capture = new CaptureStream(session, device);
        capture._self = GCHandle.Alloc(capture);
        try
        {
            using (session.Lock())
            {
                var spec = new Pulse.SampleSpec { Format = 5, Rate = 48000, Channels = 1 };
                capture._stream = Pulse.StreamNew(session.Context, name, &spec, 0);
                if (capture._stream == 0) throw new InvalidOperationException("pa_stream_new: NULL");
                nint user = GCHandle.ToIntPtr(capture._self);
                Pulse.SetRead(capture._stream, &OnRead, user);
                Pulse.SetOverflow(capture._stream, &OnOverflow, user);
                var attr = new Pulse.BufferAttr
                {
                    MaxLength = uint.MaxValue, TLength = uint.MaxValue, PreBuf = uint.MaxValue,
                    MinReq = uint.MaxValue, FragSize = 48000 * sizeof(float) / 50,
                };
                if (Pulse.ConnectRecord(capture._stream, device, &attr,
                    Pulse.AutoTiming | Pulse.Interpolate | Pulse.AdjustLatency | Pulse.DontMove) < 0)
                    throw new InvalidOperationException(Pulse.Error(Pulse.ContextError(session.Context)));
            }
            session.WaitUntil(() => Pulse.StreamState(capture._stream) switch
            {
                Pulse.Ready => true,
                Pulse.Failed or Pulse.Terminated => throw new InvalidOperationException("Захват не открылся: " + device),
                _ => false,
            });
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    internal double? ServerTime()
    {
        ulong value = 0;
        return Pulse.GetTime(_stream, &value) < 0 ? null : value / 1e6;
    }

    internal double? Latency()
    {
        ulong value = 0; int negative = 0;
        return Pulse.GetLatency(_stream, &value, &negative) < 0 ? null : value / 1000.0 * (negative == 0 ? 1 : -1);
    }

    internal CaptureChannel Report(IEnumerable<ClockPoint> delivered, IEnumerable<ClockPoint> server) =>
        new(_requested, Marshal.PtrToStringUTF8(Pulse.DeviceName(_stream)) ?? "?", Frames,
            _holes, _overflows, _readErrors, _peak, _clipped, Measurement.Percentile(_intervals, .95), _maxGap,
            Measurement.DriftPpm(delivered), Measurement.DriftPpm(server));

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRead(nint stream, nuint _, nint user)
    {
        var self = (CaptureStream)GCHandle.FromIntPtr(user).Target!;
        // Ни одно managed-исключение не должно пересечь unmanaged callback.
        try
        {
            long now = Stopwatch.GetTimestamp();
            if (self._lastCallback != 0)
            {
                double gap = Stopwatch.GetElapsedTime(self._lastCallback, now).TotalMilliseconds;
                self._intervals.Add(gap);
                self._maxGap = Math.Max(self._maxGap, gap);
            }
            self._lastCallback = now;
            while (true)
            {
                void* data = null; nuint bytes = 0;
                if (Pulse.Peek(stream, &data, &bytes) < 0) { self._readErrors++; return; }
                if (bytes == 0) return;
                long count = checked((long)(bytes / sizeof(float)));
                self.Frames += count;
                if (data is null) self._holes++;
                else
                {
                    float* samples = (float*)data;
                    for (long i = 0; i < count; i++)
                    {
                        double sample = Math.Abs(samples[i]);
                        if (sample >= 1) self._clipped++;
                        if (double.IsFinite(sample)) self._peak = Math.Max(self._peak, sample);
                    }
                }
                if (Pulse.Drop(stream) < 0) { self._readErrors++; return; }
            }
        }
        catch { self._readErrors++; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOverflow(nint _, nint user) => ((CaptureStream)GCHandle.FromIntPtr(user).Target!)._overflows++;

    public void Dispose()
    {
        using (_session.Lock())
        {
            if (_stream != 0)
            {
                Pulse.SetRead(_stream, null, 0);
                Pulse.SetOverflow(_stream, null, 0);
                _ = Pulse.Disconnect(_stream);
                Pulse.StreamUnref(_stream);
                _stream = 0;
            }
            if (_self.IsAllocated) _self.Free();
        }
    }
}
