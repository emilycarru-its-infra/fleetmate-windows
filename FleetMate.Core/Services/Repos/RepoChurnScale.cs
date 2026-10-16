namespace FleetMate.Core.Services.Repos;

public enum RepoChurnScale { Automatic, Linear, Compressed }

/// <summary>Presentation-only scaling; statistics and hover values remain unchanged.</summary>
public static class RepoChurnScaling
{
    public static RepoChurnScale Resolve(RepoChurnScale choice, IReadOnlyList<RepoStatsReport.TimelinePoint> points)
    {
        if (choice != RepoChurnScale.Automatic) return choice;
        var values = points.SelectMany(p => new[] { (double)p.Added, p.Removed }).Where(v => v > 0).Order().ToArray();
        if (values.Length == 0) return RepoChurnScale.Linear;
        var median = values.Length % 2 == 0
            ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2
            : values[values.Length / 2];
        return values[^1] >= 10_000 && values[^1] / median >= 100
            ? RepoChurnScale.Compressed : RepoChurnScale.Linear;
    }

    public static double Transform(double value, RepoChurnScale scale) => scale == RepoChurnScale.Compressed
        ? Math.Sign(value) * Math.Log10(1 + Math.Abs(value)) : value;

    public static double Inverse(double value, RepoChurnScale scale) => scale == RepoChurnScale.Compressed
        ? Math.Sign(value) * (Math.Pow(10, Math.Abs(value)) - 1) : value;

    public static IReadOnlyList<int> Ticks(int peak, RepoChurnScale scale)
    {
        if (scale != RepoChurnScale.Compressed) return new[] { 0, peak };
        var largestExponent = (int)Math.Floor(Math.Log10(Math.Max(peak, 1)));
        return new[] { 0 }.Concat(Enumerable.Range(1, 3)
            .Select(n => (int)Math.Ceiling(largestExponent * n / 3.0))
            .Distinct().Order().Select(exponent => (int)Math.Pow(10, exponent))).ToArray();
    }
}
