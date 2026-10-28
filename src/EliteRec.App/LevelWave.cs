using System.ComponentModel;

namespace EliteRec.App;

/// <summary>
/// Капсула волны или подсветка фона: объект живёт всё время, меняются только свойства —
/// окно сглаживает их переходами, ничего не «едет» блоком.
/// </summary>
public sealed class LevelBar : INotifyPropertyChanged
{
    private double _height, _opacity;
    public double Height
    {
        get => _height;
        set { if (_height == value) return; _height = value; PropertyChanged?.Invoke(this, new(nameof(Height))); }
    }
    public double Opacity
    {
        get => _opacity;
        set { if (_opacity == value) return; _opacity = value; PropertyChanged?.Invoke(this, new(nameof(Opacity))); }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// «Стеклянная волна» (макет 6): от центральной оси влево — Вы, вправо — Система.
/// Уровень расходится от оси (капсула i повторяет уровень i тиков назад), поверх — бегущая синусоида,
/// к краям спад высоты и прозрачности. Подъём быстрый, спад медленный — как у стрелочного индикатора.
/// Шкала — от фона, а не абсолютная: у гарнитуры фон бывает около −30 дБ, и на шкале −66…0 молчание
/// рисовалось на полволны, а речь почти не добавляла (замечание 28 октября). Поэтому у каждой стороны
/// свой уровень фона (<see cref="NoiseFloor"/>): волна начинается на 10 дБ выше него и
/// доходит до полной высоты к −3 дБ. Тишина — короткая капсула <see cref="Stub"/>, а не пустота.
/// Фон за говорящей стороной подсвечивается (<see cref="LeftGlow"/> / <see cref="RightGlow"/>).
/// </summary>
public sealed class LevelWave
{
    public const int BarCount = 16;
    public const double BarHeight = 220, Stub = .03, GlowFloor = .25;
    private const double FloorDb = -66, TopDb = -3, GateDb = 10;
    private readonly double[] _micTrail = new double[BarCount], _sysTrail = new double[BarCount];
    private readonly NoiseFloor _micFloor = new(), _sysFloor = new();
    private double _last = double.NaN;
    public IReadOnlyList<LevelBar> LeftBars { get; } = Bars();
    public IReadOnlyList<LevelBar> RightBars { get; } = Bars();
    public LevelBar LeftGlow { get; } = new() { Opacity = GlowFloor };
    public LevelBar RightGlow { get; } = new() { Opacity = GlowFloor };

    private static LevelBar[] Bars() =>
        Enumerable.Range(0, BarCount).Select(i => new LevelBar { Height = Stub * BarHeight, Opacity = 1 - i * .045 }).ToArray();

    /// <summary>Один тик окна (~50 мс). На паузе волна оседает до тишины.</summary>
    /// <param name="now">Монотонное время окна, секунды.</param>
    public void Add(double now, float microphonePeak, float systemPeak, bool paused)
    {
        double elapsed = double.IsNaN(_last) ? 0 : Math.Clamp(now - _last, 0, 1);
        _last = now;
        Step(now, _micTrail, LeftBars, LeftGlow, _micFloor.Level(paused ? 0 : microphonePeak, elapsed), 0);
        Step(now, _sysTrail, RightBars, RightGlow, _sysFloor.Level(paused ? 0 : systemPeak, elapsed), 1.7);
    }

    private static void Step(double now, double[] trail, IReadOnlyList<LevelBar> bars, LevelBar glow, double level, double phase)
    {
        Array.Copy(trail, 0, trail, 1, trail.Length - 1);
        trail[0] = level;
        for (int i = 0; i < bars.Count; i++)
        {
            double ripple = .72 + .28 * Math.Sin(now * 5.5 - i * .8 + phase);
            double target = Math.Max(Stub, trail[i] * ripple * (1 - i * .035)) * BarHeight;
            bars[i].Height = Approach(bars[i].Height, target, .5);
        }
        glow.Opacity = Approach(glow.Opacity, GlowFloor + (1 - GlowFloor) * trail[0], .01);
    }

    private static double Approach(double value, double target, double snap) =>
        Math.Abs(target - value) < snap ? target : value + (target - value) * (target > value ? .65 : .2);

    /// <summary>
    /// Уровень фона одной стороны — минимум сглаженного уровня (пик за 200 мс) за последние 2 с:
    /// в паузах между словами он опускается к фону, а одиночный тихий тик его не сбивает. Вверх фон
    /// идёт не быстрее <see cref="RiseDbPerSecond"/> и не выше <see cref="MaxDb"/> — громкая непрерывная
    /// речь не «съедает» волну. Подобрано на записи 2026-10-28_10-31 (JBL Quantum 350): молчание
    /// стало 0,1–0,25 высоты вместо 0,7, речь — 0,9–1.
    /// </summary>
    public sealed class NoiseFloor
    {
        public const double RiseDbPerSecond = 10, MinDb = -70, MaxDb = -24, StartDb = -45;
        private const int SmoothTicks = 4, WindowTicks = 40; // при тике 50 мс: 200 мс и 2 с
        private readonly Queue<double> _recent = new(), _window = new();
        public double Db { get; private set; } = StartDb;

        /// <summary>Учитывает пик и возвращает высоту волны 0…1 относительно фона.</summary>
        public double Level(float peak, double elapsedSeconds)
        {
            double db = peak <= 0 || !float.IsFinite(peak) ? MinDb : Math.Max(MinDb, 20 * Math.Log10(peak));
            Push(_recent, db, SmoothTicks);
            Push(_window, _recent.Max(), WindowTicks);
            double target = _window.Min();
            Db = Math.Clamp(target < Db ? target : Math.Min(target, Db + RiseDbPerSecond * elapsedSeconds), MinDb, MaxDb);
            double gate = Db + GateDb;
            return Math.Clamp((db - gate) / (TopDb - gate), 0, 1);
        }

        private static void Push(Queue<double> queue, double value, int capacity)
        {
            queue.Enqueue(value);
            if (queue.Count > capacity) queue.Dequeue();
        }
    }

    /// <summary>Абсолютная шкала −66…0 дБ — для индикаторов самопроверки и вердикта «слышно».</summary>
    public static double Level(float peak) =>
        peak <= 0 || !float.IsFinite(peak) ? 0 : Math.Clamp((20 * Math.Log10(peak) - FloorDb) / -FloorDb, 0, 1);
}
