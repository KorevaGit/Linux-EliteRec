using System.Diagnostics;
using EliteRec.Audio.Pulse.Native;
using EliteRec.Core.Recording;

namespace EliteRec.Audio.Pulse;

/// <summary>
/// Два record-stream и отдельный поток таймлайна/записи. Диск не вызывается из native callback.
/// Пропажа источника не останавливает запись: канал пишется тишиной, пока оболочка не
/// подключит другой источник через <see cref="SwitchSourceAsync"/>.
/// </summary>
public sealed class PulseRecordingSession : IRecordingSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SynchronizedAudioMixer _mixer = new();
    private readonly PulseConnection _connection;
    private readonly object _streams = new();
    private readonly SemaphoreSlim _switching = new(1, 1);
    private PulseCaptureStream? _microphone, _system;
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new(), _disposeGate = new();
    private readonly Queue<Command> _commands = new();
    private readonly Task _worker;
    private Task? _disposeTask;
    private IRecordingSink? _sink;
    private RecordingState _state = RecordingState.Preview;
    private RecordingFiles? _files;
    private long _recorded, _nextFrame, _recordingMissing;
    private bool _fadeIn, _running = true;
    private string? _failure, _microphoneSource, _systemSource;
    private RecordingSnapshot _snapshot = new(RecordingState.Preview, 0, 0, 0, new(0, 0, 0, 0, 0, 0));
    public RecordingSnapshot Snapshot => Volatile.Read(ref _snapshot);

    private PulseRecordingSession(string microphoneSource, string systemSource, CancellationToken token)
    {
        _connection = PulseConnection.Connect(token);
        PulseCaptureStream? microphone = null, system = null;
        try
        {
            microphone = PulseCaptureStream.Open(_connection, _clock, _mixer, AudioChannel.Microphone, microphoneSource, token);
            system = PulseCaptureStream.Open(_connection, _clock, _mixer, AudioChannel.System, systemSource, token);
            _connection.WaitUntil(() => microphone.HasTiming && system.HasTiming, token);
            _microphoneSource = microphoneSource; _systemSource = systemSource;
            _microphone = microphone; _system = system;
            _nextFrame = (long)(_clock.Elapsed.TotalSeconds * 100) * 480;
            _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { system?.Dispose(); microphone?.Dispose(); _connection.Dispose(); _wake.Dispose(); _stop.Dispose(); _switching.Dispose(); throw; }
    }

    public static Task<PulseRecordingSession> OpenAsync(string microphoneSource, string systemSource,
        CancellationToken cancellationToken = default) => Task.Run(() => new PulseRecordingSession(microphoneSource, systemSource, cancellationToken), cancellationToken);

    public Task StartRecordingAsync(Func<IRecordingSink> createSink) => Enqueue(() =>
    {
        if (_state != RecordingState.Preview) throw new InvalidOperationException("Запись уже начата.");
        _sink = createSink(); _files = _sink.Files; _recorded = 0; _recordingMissing = 0; _fadeIn = true; _state = RecordingState.Recording;
    });

    public Task PauseAsync() => Enqueue(() =>
    {
        if (_state != RecordingState.Recording) throw new InvalidOperationException("Нет активной записи.");
        _state = RecordingState.Paused;
    }, endsSegment: true);

    public Task ResumeAsync() => Enqueue(() =>
    {
        if (_state != RecordingState.Paused) throw new InvalidOperationException("Запись не на паузе.");
        _fadeIn = true; _state = RecordingState.Recording;
    });

    public Task StopRecordingAsync() => Enqueue(() =>
    {
        if (_state is not (RecordingState.Recording or RecordingState.Paused)) throw new InvalidOperationException("Нет активной записи.");
        var sink = _sink; _sink = null;
        _state = RecordingState.Preview; sink!.Dispose();
    }, endsSegment: true);

    public async Task<bool> SwitchSourceAsync(AudioChannel channel, string? source, string note, bool requireSound = false)
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_running) throw new InvalidOperationException(_failure ?? "Аудиосессия закрыта.");
            bool switched = await Task.Run(() =>
            {
                // Сначала открываем новый (немым): не открылся — старый источник продолжает писать.
                var stream = source is null ? null
                    : PulseCaptureStream.Open(_connection, _clock, _mixer, channel, source, _stop.Token, active: false);
                if (stream is not null && requireSound)
                {
                    // Проба: гарнитура включена, если за 1,5 с прислала хоть что-то кроме нулей.
                    for (int i = 0; i < 150 && !stream.HeardSound && !_stop.IsCancellationRequested; i++) Thread.Sleep(10);
                    if (!stream.HeardSound) { stream.Dispose(); return false; }
                }
                Detach(channel);
                if (stream is null) return true;
                lock (_streams)
                {
                    if (channel == AudioChannel.Microphone) { _microphone = stream; _microphoneSource = source; }
                    else { _system = stream; _systemSource = source; }
                }
                stream.Active = true;
                return true;
            }).ConfigureAwait(false);
            if (!switched) return false;
        }
        finally
        {
            _switching.Release();
            Publish(Snapshot.MicrophonePeak, Snapshot.SystemPeak);
        }
        // Отметка — только об удавшемся переключении, на позиции таймлайна записи.
        await Enqueue(() => _sink?.Mark(Position, note)).ConfigureAwait(false);
        return true;
    }

    private TimeSpan Position => TimeSpan.FromSeconds(_recorded / (double)SynchronizedAudioMixer.SampleRate);

    /// <summary>Закрывает поток канала; до нового источника канал звучит тишиной.</summary>
    private void Detach(AudioChannel channel)
    {
        PulseCaptureStream? old;
        lock (_streams)
        {
            if (channel == AudioChannel.Microphone) { old = _microphone; _microphone = null; _microphoneSource = null; }
            else { old = _system; _system = null; _systemSource = null; }
        }
        old?.Dispose();
        _mixer.Restart(channel);
    }

    private Task Enqueue(Action action, bool endsSegment = false)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (!_running || _stop.IsCancellationRequested) return Task.FromException(new InvalidOperationException(_failure ?? "Аудиосессия закрыта."));
            long frame = (long)Math.Ceiling(_clock.Elapsed.TotalSeconds * 100) * 480;
            _commands.Enqueue(new(frame, action, endsSegment, completion));
            _wake.Set();
        }
        return completion.Task;
    }

    private void Run()
    {
        float[] microphone = new float[480], system = new float[480], mix = new float[480];
        double lastTiming = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                _wake.WaitOne(10);
                PulseCaptureStream? mic, sys;
                lock (_streams) { mic = _microphone; sys = _system; }
                AudioChannel? lost = null;
                using (_connection.Lock())
                {
                    if (LibPulse.ContextGetState(_connection.Context) != LibPulse.ContextReady) throw new InvalidOperationException("Аудиосервер отключён.");
                    if (mic is not null && !Alive(mic)) lost = AudioChannel.Microphone;
                    else if (sys is not null && !Alive(sys)) lost = AudioChannel.System;
                    else if (_clock.Elapsed.TotalSeconds - lastTiming >= .25)
                    { mic?.UpdateTiming(); sys?.UpdateTiming(); lastTiming = _clock.Elapsed.TotalSeconds; }
                }
                if (lost is { } channel)
                {
                    // Выдернули гарнитуру или источник умолк: запись продолжается, канал — тишина.
                    if (_switching.Wait(0))
                    {
                        try { Detach(channel); }
                        finally { _switching.Release(); }
                        if (_state is RecordingState.Recording or RecordingState.Paused)
                            _sink?.Mark(Position, (channel == AudioChannel.Microphone ? "Микрофон" : "Звук системы") + ": источник пропал");
                        Publish(Snapshot.MicrophonePeak, Snapshot.SystemPeak);
                    }
                    continue;
                }
                // 250 мс запаса для серверных буферов и джиттера доставки.
                long ready = (long)((_clock.Elapsed.TotalSeconds - .25) * 48000);
                while (_nextFrame + 480 <= ready && !_stop.IsCancellationRequested)
                {
                    ExecuteCommands();
                    var before = _mixer.Statistics;
                    if (!_mixer.TryRender(_nextFrame, microphone, system)) break;
                    float micPeak = Peak(microphone), sysPeak = Peak(system);
                    if (_state == RecordingState.Recording)
                    {
                        var after = _mixer.Statistics;
                        _recordingMissing += after.MissingMicrophoneFrames + after.MissingSystemFrames
                            - before.MissingMicrophoneFrames - before.MissingSystemFrames;
                        if (_recordingMissing > 4800) throw new InvalidOperationException("Потеряно более 100 мс аудиоданных. Запись завершена.");
                        bool fadeOut;
                        lock (_gate) fadeOut = _commands.TryPeek(out var next) && next.EndsSegment && next.Frame <= _nextFrame + 480;
                        Fade(microphone, _fadeIn, fadeOut); Fade(system, _fadeIn, fadeOut); _fadeIn = false;
                        SynchronizedAudioMixer.Mix(microphone, system, mix);
                        _sink!.Write(microphone, system, mix); _recorded += 480;
                    }
                    _nextFrame += 480;
                    Publish(micPeak, sysPeak);
                }
            }
        }
        catch (Exception ex) { _failure = ex.Message; _state = RecordingState.Faulted; }
        finally
        {
            try { _sink?.Dispose(); }
            catch (Exception ex) { _failure ??= ex.Message; _state = RecordingState.Faulted; }
            _sink = null;
            lock (_gate)
            {
                _running = false;
                while (_commands.TryDequeue(out var command)) command.Completion.TrySetException(new InvalidOperationException(_failure ?? "Аудиосессия закрыта."));
            }
            Publish(0, 0);
        }
    }

    /// <summary>Поток жив: не упал, не отключён сервером и данные приходят.</summary>
    private static bool Alive(PulseCaptureStream stream)
    {
        if (stream.Failure is not null || stream.ArrivalAge > 2) return false;
        try { stream.Ready(); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private void ExecuteCommands()
    {
        while (true)
        {
            Command command;
            lock (_gate)
            {
                if (!_commands.TryPeek(out command!) || command.Frame > _nextFrame) return;
                _commands.Dequeue();
            }
            try { command.Action(); Publish(Snapshot.MicrophonePeak, Snapshot.SystemPeak); command.Completion.SetResult(); }
            catch (Exception ex) { command.Completion.SetException(ex); }
        }
    }

    private void Publish(float microphone, float system) => Volatile.Write(ref _snapshot,
        new(_state, _recorded, microphone, system, _mixer.Statistics, _failure, _files,
            Capture(), _microphoneSource, _systemSource, Silence(AudioChannel.Microphone), Silence(AudioChannel.System)));

    private double Silence(AudioChannel channel)
    {
        lock (_streams) return (channel == AudioChannel.Microphone ? _microphone : _system)?.SilenceAge ?? 0;
    }

    // Счётчики текущих потоков; после переключения начинаются заново.
    private CaptureStatistics Capture()
    {
        lock (_streams)
            return new((_microphone?.Holes ?? 0) + (_system?.Holes ?? 0), (_microphone?.Overflows ?? 0) + (_system?.Overflows ?? 0),
                (_microphone?.UntimedFrames ?? 0) + (_system?.UntimedFrames ?? 0));
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        float peak = 0; foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample)); return peak;
    }

    private static void Fade(Span<float> samples, bool fadeIn, bool fadeOut)
    {
        const int ramp = 240;
        if (fadeIn) for (int i = 0; i < ramp; i++) samples[i] *= i / (float)ramp;
        if (fadeOut) for (int i = 0; i < ramp; i++) samples[^(i + 1)] *= i / (float)ramp;
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        try
        {
            if (Snapshot.State is RecordingState.Recording or RecordingState.Paused) await StopRecordingAsync().ConfigureAwait(false);
        }
        finally
        {
            _stop.Cancel(); _wake.Set();
            await _worker.ConfigureAwait(false);
            await _switching.WaitAsync().ConfigureAwait(false);
            lock (_streams) { _system?.Dispose(); _microphone?.Dispose(); _system = _microphone = null; }
            _connection.Dispose();
            _wake.Dispose(); _stop.Dispose(); _switching.Dispose();
            Volatile.Write(ref _snapshot, Snapshot with { State = RecordingState.Closed });
        }
    }

    private sealed record Command(long Frame, Action Action, bool EndsSegment, TaskCompletionSource Completion);
}
