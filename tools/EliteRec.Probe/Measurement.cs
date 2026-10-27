namespace EliteRec.Probe;

internal readonly record struct ClockPoint(double WallSeconds, double AudioSeconds);

internal static class Measurement
{
    // Сдвиг начала потока не влияет на наклон. Первые 3 с — прогрев сервера.
    // Наклон по доставленным отсчётам содержит джиттер: это не акустический тест синхронизации.
    internal static double? DriftPpm(IEnumerable<ClockPoint> samples)
    {
        var points = samples.Where(p => p.WallSeconds >= 3
            && double.IsFinite(p.AudioSeconds) && double.IsFinite(p.WallSeconds)).ToArray();
        if (points.Length < 8 || points[^1].WallSeconds - points[0].WallSeconds < 5) return null;
        double x = points.Average(p => p.WallSeconds), y = points.Average(p => p.AudioSeconds);
        double xx = points.Sum(p => (p.WallSeconds - x) * (p.WallSeconds - x));
        if (xx == 0) return null;
        double xy = points.Sum(p => (p.WallSeconds - x) * (p.AudioSeconds - y));
        return (xy / xx - 1) * 1e6;
    }

    internal static double? Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        double index = (sorted.Length - 1) * percentile;
        int lo = (int)index, hi = (int)Math.Ceiling(index);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }
}
