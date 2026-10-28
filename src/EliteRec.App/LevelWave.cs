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
/// Шкала дБ (−66…0), иначе тихая речь почти не видна; тишина — короткая капсула <see cref="Stub"/>,
/// а не пустота. Фон за говорящей стороной подсвечивается (<see cref="LeftGlow"/> / <see cref="RightGlow"/>).
/// </summary>
public sealed class LevelWave
{
    public const int BarCount = 16;
    public const double BarHeight = 260, Stub = .03, GlowFloor = .25;
    private const double FloorDb = -66;
    private readonly double[] _micTrail = new double[BarCount], _sysTrail = new double[BarCount];
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
        Step(now, _micTrail, LeftBars, LeftGlow, paused ? 0 : microphonePeak, 0);
        Step(now, _sysTrail, RightBars, RightGlow, paused ? 0 : systemPeak, 1.7);
    }

    private static void Step(double now, double[] trail, IReadOnlyList<LevelBar> bars, LevelBar glow, float peak, double phase)
    {
        Array.Copy(trail, 0, trail, 1, trail.Length - 1);
        trail[0] = Level(peak);
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

    public static double Level(float peak) =>
        peak <= 0 || !float.IsFinite(peak) ? 0 : Math.Clamp((20 * Math.Log10(peak) - FloorDb) / -FloorDb, 0, 1);
}
