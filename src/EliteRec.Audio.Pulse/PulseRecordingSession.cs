using System.Diagnostics;
using EliteRec.Audio.Pulse.Native;
using EliteRec.Core.Recording;

namespace EliteRec.Audio.Pulse;

/// <summary>Два record-stream и отдельный поток таймлайна/записи. Диск не вызывается из native callback.</summary>
public sealed class PulseRecordingSession : IRecordingSession
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SynchronizedAudioMixer _mixer = new();
    private readonly PulseConnection _connection;
    private readonly PulseCaptureStream _microphone, _system;
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
    private string? _failure;
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
            _microphone = microphone; _system = system;
            _nextFrame = (long)(_clock.Elapsed.TotalSeconds * 100) * 480;
            _worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        catch { system?.Dispose(); microphone?.Dispose(); _connection.Dispose(); _wake.Dispose(); _stop.Dispose(); throw; }
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
                if (_microphone.Failure is { } micError) throw new InvalidOperationException(micError);
                if (_system.Failure is { } sysError) throw new InvalidOperationException(sysError);
                if (_microphone.ArrivalAge > 2 || _system.ArrivalAge > 2)
                    throw new InvalidOperationException("Аудиоданные не поступают более двух секунд. Запись завершена.");
                using (_connection.Lock())
                {
                    if (LibPulse.ContextGetState(_connection.Context) != LibPulse.ContextReady) throw new InvalidOperationException("Аудиосервер отключён.");
                    _microphone.Ready(); _system.Ready();
                    if (_clock.Elapsed.TotalSeconds - lastTiming >= .25)
                    { _microphone.UpdateTiming(); _system.UpdateTiming(); lastTiming = _clock.Elapsed.TotalSeconds; }
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
            new(_microphone.Holes + _system.Holes, _microphone.Overflows + _system.Overflows,
                _microphone.UntimedFrames + _system.UntimedFrames)));

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
            _system.Dispose(); _microphone.Dispose(); _connection.Dispose();
            _wake.Dispose(); _stop.Dispose();
            Volatile.Write(ref _snapshot, Snapshot with { State = RecordingState.Closed });
        }
    }

    private sealed record Command(long Frame, Action Action, bool EndsSegment, TaskCompletionSource Completion);
}
