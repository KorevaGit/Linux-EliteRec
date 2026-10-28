namespace EliteRec.Core.Recording;

public enum AudioChannel { Microphone, System }

public sealed record SynchronizerStatistics(long MissingMicrophoneFrames, long MissingSystemFrames,
    long MicrophoneFrames, long SystemFrames, double MicrophoneClockPpm, double SystemClockPpm,
    long FutureMicrophoneFrames = 0, long FutureSystemFrames = 0, long ExpiredMicrophoneFrames = 0, long ExpiredSystemFrames = 0,
    long GapMicrophoneFrames = 0, long GapSystemFrames = 0);

/// <summary>
/// Два ограниченных кольцевых буфера и общий таймлайн 48 кГц. Модель часов
/// строится по меткам захвата, а не по порядку прихода callback. Файлового I/O нет.
/// </summary>
public sealed class SynchronizedAudioMixer
{
    public const int SampleRate = 48000;
    public const int BlockFrames = 480;
    private readonly object _gate = new();
    private ClockedBuffer _microphone = new(), _system = new();
    // Счётчики буферов, заменённых при переключении источника.
    private readonly ClockedBuffer _retiredMicrophone = new(), _retiredSystem = new();

    /// <summary>
    /// Новый источник канала (переключение устройства): старые отсчёты и модель часов сбрасываются.
    /// Пока новый поток не дал данных, канал звучит тишиной — это «разрыв», а не потеря.
    /// </summary>
    public void Restart(AudioChannel channel)
    {
        lock (_gate)
        {
            if (channel == AudioChannel.Microphone) { _retiredMicrophone.Absorb(_microphone); _microphone = new() { Detached = true, Restarted = true }; }
            else { _retiredSystem.Absorb(_system); _system = new() { Detached = true, Restarted = true }; }
        }
    }

    public void Push(AudioChannel channel, double firstSampleSeconds, ReadOnlySpan<float> samples)
    {
        if (!double.IsFinite(firstSampleSeconds)) throw new ArgumentException("Некорректная метка времени.");
        lock (_gate)
        {
            (channel == AudioChannel.Microphone ? _microphone : _system).Push(firstSampleSeconds, samples);
        }
    }

    public void Render(long firstFrame, Span<float> microphone, Span<float> system)
    {
        if (microphone.Length != system.Length) throw new ArgumentException("Размеры каналов различаются.");
        lock (_gate)
        {
            _microphone.Render(firstFrame, microphone);
            _system.Render(firstFrame, system);
        }
    }

    public bool TryRender(long firstFrame, Span<float> microphone, Span<float> system)
    {
        if (microphone.Length != system.Length) throw new ArgumentException("Размеры каналов различаются.");
        lock (_gate)
        {
            // Запись не является realtime-выводом: поздний callback можно дождаться.
            // Нельзя продвигать курсор и писать тишину за ещё не доставленные данные.
            if (!_microphone.CanRender(firstFrame, microphone.Length) || !_system.CanRender(firstFrame, system.Length)) return false;
            _microphone.Render(firstFrame, microphone);
            _system.Render(firstFrame, system);
            return true;
        }
    }

    public SynchronizerStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                ClockedBuffer m = _microphone, s = _system, rm = _retiredMicrophone, rs = _retiredSystem;
                return new(m.Missing + rm.Missing, s.Missing + rs.Missing, m.Count + rm.Count, s.Count + rs.Count,
                    (m.Rate / SampleRate - 1) * 1e6, (s.Rate / SampleRate - 1) * 1e6,
                    m.Future + rm.Future, s.Future + rs.Future, m.Expired + rm.Expired, s.Expired + rs.Expired,
                    m.Gap + rm.Gap, s.Gap + rs.Gap);
            }
        }
    }

    /// <param name="microphoneGain">Громкость микрофона в миксе (настройка «громкость микрофона / системы», по умолчанию 1:1).
    /// Раздельные каналы стерео-файла не трогаются — они нужны расшифровке как есть.</param>
    public static void Mix(ReadOnlySpan<float> microphone, ReadOnlySpan<float> system, Span<float> mix,
        float microphoneGain = 1, float systemGain = 1)
    {
        if (microphone.Length != system.Length || mix.Length != microphone.Length) throw new ArgumentException("Размеры каналов различаются.");
        for (int i = 0; i < mix.Length; i++)
        {
            double sum = microphone[i] * microphoneGain + system[i] * systemGain;
            // Мягкое колено: тихий сигнал не меняется; сумма не обрезается плоским потолком.
            double magnitude = Math.Abs(sum);
            mix[i] = (float)(magnitude <= .8 ? sum : Math.CopySign(.8 + .2 * Math.Tanh((magnitude - .8) / .2), sum));
        }
    }

    private sealed class ClockedBuffer
    {
        // 10 с: после подвисания процесса один канал может догнать раньше другого — его накопленное
        // не должно перезаписаться, пока сведение ждёт второй канал.
        private readonly float[] _samples = new float[SampleRate * 10];
        private readonly (double Time, double Frame)[] _anchors = new (double, double)[64];
        private int _anchorNext, _anchorCount;
        private double _lastAnchorTime = double.NegativeInfinity, _offset;
        private double _position = double.NaN;
        private long _nextOutputFrame = -1;
        internal long Count, Missing, Future, Expired, Gap;
        internal double Rate = SampleRate;
        /// <summary>Источник переключается: до первой метки нового потока канал — тишина.</summary>
        internal bool Detached;
        /// <summary>Буфер заменённого источника: позиции до его первого отсчёта — разрыв, а не потеря.</summary>
        internal bool Restarted;

        internal void Absorb(ClockedBuffer old)
        {
            Count += old.Count; Missing += old.Missing; Future += old.Future; Expired += old.Expired; Gap += old.Gap;
        }

        internal void Push(double firstSeconds, ReadOnlySpan<float> samples)
        {
            long first = Count;
            for (int i = 0; i < samples.Length; i++)
                _samples[(first + i) % _samples.Length] = float.IsFinite(samples[i]) ? samples[i] : 0;
            Count += samples.Length;
            if (samples.IsEmpty || firstSeconds - _lastAnchorTime < .25) return;
            Detached = false;
            _lastAnchorTime = firstSeconds;
            _anchors[_anchorNext] = (firstSeconds, first);
            _anchorNext = (_anchorNext + 1) % _anchors.Length;
            _anchorCount = Math.Min(_anchorCount + 1, _anchors.Length);
            double time = 0, frame = 0;
            for (int i = 0; i < _anchorCount; i++) { time += _anchors[i].Time; frame += _anchors[i].Frame; }
            time /= _anchorCount; frame /= _anchorCount;
            double xx = 0, xy = 0;
            for (int i = 0; i < _anchorCount; i++)
            {
                double dt = _anchors[i].Time - time;
                xx += dt * dt; xy += dt * (_anchors[i].Frame - frame);
            }
            // На короткой базе джиттер слишком велик для оценки частоты.
            Rate = xx > 1 ? Math.Clamp(xy / xx, SampleRate * .99, SampleRate * 1.01) : SampleRate;
            _offset = frame - Rate * time;
        }

        internal void Render(long firstFrame, Span<float> output)
        {
            if (Detached) { output.Clear(); Gap += output.Length; return; }
            if (_anchorCount == 0) { output.Clear(); Missing += output.Length; return; }
            double desired = _offset + Rate * firstFrame / SampleRate;
            if (_nextOutputFrame != firstFrame || double.IsNaN(_position)) _position = desired;
            double step = Rate / SampleRate + Math.Clamp((desired - _position) / (SampleRate * .5), -.005, .005);
            long oldest = Math.Max(0, Count - _samples.Length);
            for (int i = 0; i < output.Length; i++, _position += step)
            {
                long a = (long)Math.Floor(_position);
                if (a < 0 && Restarted) { output[i] = 0; Gap++; continue; } // до первого отсчёта нового источника
                if (a < oldest) { output[i] = 0; Missing++; Expired++; continue; }
                if (a + 1 >= Count) { output[i] = 0; Missing++; Future++; continue; }
                float left = _samples[a % _samples.Length], right = _samples[(a + 1) % _samples.Length];
                output[i] = left + (right - left) * (float)(_position - a);
            }
            _nextOutputFrame = firstFrame + output.Length;
        }

        internal bool CanRender(long firstFrame, int count)
        {
            if (count == 0) return true;
            if (Detached) return true;
            if (_anchorCount == 0) return false;
            double desired = _offset + Rate * firstFrame / SampleRate;
            double position = _nextOutputFrame != firstFrame || double.IsNaN(_position) ? desired : _position;
            double step = Rate / SampleRate + Math.Clamp((desired - position) / (SampleRate * .5), -.005, .005);
            return Math.Floor(position + step * (count - 1)) + 1 < Count;
        }
    }
}
