using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using EliteRec.Audio.Pulse.Native;
using EliteRec.Core.Recording;

namespace EliteRec.Audio.Pulse;

// В callback только копирование в ограниченный буфер. Метки — raw timing_info,
// без сглаженного UI-интерполятора pa_stream_get_time.
internal sealed unsafe class PulseCaptureStream : IDisposable
{
    private readonly PulseConnection _connection;
    private readonly Stopwatch _clock;
    private readonly SynchronizedAudioMixer _mixer;
    private readonly AudioChannel _channel;
    private GCHandle _self;
    private nint _stream;
    private long _timedFrames;
    private long _holes, _overflows, _untimed;
    private string? _failure;
    private double _lastArrival, _lastSound, _opened;
    private volatile bool _active;
    /// <summary>Отдавать ли данные в микшер. Новый источник открывается немым и включается при подмене.</summary>
    internal bool Active { get => _active; set => _active = value; }
    internal string? Failure => Volatile.Read(ref _failure);
    internal bool HasTiming => Volatile.Read(ref _timedFrames) > 0;
    internal long Holes => Volatile.Read(ref _holes);
    internal long Overflows => Volatile.Read(ref _overflows);
    internal long UntimedFrames => Volatile.Read(ref _untimed);
    internal double ArrivalAge => _clock.Elapsed.TotalSeconds - Volatile.Read(ref _lastArrival);
    /// <summary>
    /// Сколько секунд источник присылает ровно нули. Живой микрофон даже в тишине даёт шум;
    /// нули — выключенная беспроводная гарнитура при оставленном в USB приёмнике.
    /// </summary>
    internal double SilenceAge => _clock.Elapsed.TotalSeconds - Volatile.Read(ref _lastSound);
    internal bool HeardSound => Volatile.Read(ref _lastSound) > _opened;

    private PulseCaptureStream(PulseConnection connection, Stopwatch clock, SynchronizedAudioMixer mixer, AudioChannel channel)
    {
        _connection = connection; _clock = clock; _mixer = mixer; _channel = channel;
        // «Данных нет N секунд» считается от открытия, иначе новый источник сразу выглядит умершим.
        _lastArrival = _lastSound = _opened = clock.Elapsed.TotalSeconds;
    }

    internal static PulseCaptureStream Open(PulseConnection connection, Stopwatch clock, SynchronizedAudioMixer mixer,
        AudioChannel channel, string device, CancellationToken token, bool active = true)
    {
        var capture = new PulseCaptureStream(connection, clock, mixer, channel) { Active = active };
        capture._self = GCHandle.Alloc(capture);
        try
        {
            using (connection.Lock())
            {
                var spec = new LibPulse.SampleSpec { Format = LibPulse.SampleFloat32Le, Rate = 48000, Channels = 1 };
                nint properties = LibPulse.ProplistNew();
                try
                {
                    _ = LibPulse.ProplistSets(properties, "media.role", "production");
                    _ = LibPulse.ProplistSets(properties, "application.id", "vip.elitesip.EliteRec");
                    capture._stream = LibPulse.StreamNewWithProplist(connection.Context, "EliteRec " + channel, &spec, 0, properties);
                }
                finally { LibPulse.ProplistFree(properties); }
                if (capture._stream == 0) throw new InvalidOperationException("Не удалось создать поток захвата.");
                nint user = GCHandle.ToIntPtr(capture._self);
                LibPulse.StreamSetReadCallback(capture._stream, &OnRead, user);
                LibPulse.StreamSetOverflowCallback(capture._stream, &OnOverflow, user);
                var attributes = new LibPulse.BufferAttr
                {
                    MaxLength = 48000 * sizeof(float), TLength = uint.MaxValue, PreBuf = uint.MaxValue,
                    MinReq = uint.MaxValue, FragSize = 960 * sizeof(float),
                };
                if (LibPulse.StreamConnectRecord(capture._stream, device, &attributes,
                    LibPulse.StreamAutoTimingUpdate | LibPulse.StreamAdjustLatency | LibPulse.StreamDontMove) < 0)
                    throw new InvalidOperationException(LibPulse.StrError(LibPulse.ContextErrno(connection.Context)));
            }
            connection.WaitUntil(() => capture.Ready(), token);
            using (connection.Lock()) capture.UpdateTiming();
            return capture;
        }
        catch { capture.Dispose(); throw; }
    }

    internal bool Ready()
    {
        int state = LibPulse.StreamGetState(_stream);
        if (state is LibPulse.StreamFailed or LibPulse.StreamTerminated)
            throw new InvalidOperationException("Источник звука отключён: " + _channel);
        return state == LibPulse.StreamReady;
    }

    internal void UpdateTiming()
    {
        nint operation = LibPulse.StreamUpdateTiming(_stream, 0, 0);
        if (operation != 0) LibPulse.OperationUnref(operation);
    }

    private double? FirstSampleTime()
    {
        var timing = LibPulse.StreamGetTimingInfo(_stream);
        if (timing is null || timing->ReadIndexCorrupt != 0 || timing->WriteIndexCorrupt != 0) return null;
        double timestamp = timing->Seconds + timing->Microseconds / 1e6;
        double wall = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        double age = wall - timestamp;
        if (age is < -.1 or > 2) return null;
        // В момент timestamp индекс write_index уже получен сервером. source_usec
        // отодвигает его назад к микрофону; sink_usec монитора — вперёд к воспроизведению.
        return _clock.Elapsed.TotalSeconds - age
            + (timing->ReadIndex - timing->WriteIndex) / (48000.0 * sizeof(float))
            - timing->SourceUsec / 1e6 + timing->SinkUsec / 1e6;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRead(nint stream, nuint _, nint user)
    {
        var self = (PulseCaptureStream)GCHandle.FromIntPtr(user).Target!;
        try
        {
            Span<float> silence = stackalloc float[960];
            silence.Clear();
            while (true)
            {
                void* data = null; nuint bytes = 0;
                if (LibPulse.StreamPeek(stream, &data, &bytes) < 0) throw new InvalidOperationException("Ошибка чтения аудиопотока.");
                if (bytes == 0) return;
                Volatile.Write(ref self._lastArrival, self._clock.Elapsed.TotalSeconds);
                int count = checked((int)(bytes / sizeof(float)));
                if (data is not null && new ReadOnlySpan<float>(data, count).ContainsAnyExcept(0f))
                    Volatile.Write(ref self._lastSound, self._clock.Elapsed.TotalSeconds);
                if (!self._active)
                {
                    if (LibPulse.StreamDrop(stream) < 0) throw new InvalidOperationException("Ошибка освобождения аудиоблока.");
                    continue;
                }
                double? time = self.FirstSampleTime();
                if (time is null)
                {
                    self._untimed += count;
                    if (self._timedFrames != 0) Volatile.Write(ref self._failure, "Потеряны метки времени аудиопотока.");
                }
                else if (data is not null)
                {
                    self._mixer.Push(self._channel, time.Value, new ReadOnlySpan<float>(data, count));
                    self._timedFrames += count;
                }
                else
                {
                    self._holes++;
                    for (int offset = 0; offset < count; offset += silence.Length)
                        self._mixer.Push(self._channel, time.Value + offset / 48000.0, silence[..Math.Min(silence.Length, count - offset)]);
                    self._timedFrames += count;
                }
                if (LibPulse.StreamDrop(stream) < 0) throw new InvalidOperationException("Ошибка освобождения аудиоблока.");
            }
        }
        catch (Exception ex) { Volatile.Write(ref self._failure, ex.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOverflow(nint _, nint user) => ((PulseCaptureStream)GCHandle.FromIntPtr(user).Target!)._overflows++;

    public void Dispose()
    {
        using (_connection.Lock())
        {
            if (_stream != 0)
            {
                LibPulse.StreamSetReadCallback(_stream, null, 0);
                LibPulse.StreamSetOverflowCallback(_stream, null, 0);
                _ = LibPulse.StreamDisconnect(_stream);
                LibPulse.StreamUnref(_stream);
                _stream = 0;
            }
            if (_self.IsAllocated) _self.Free();
        }
    }
}
