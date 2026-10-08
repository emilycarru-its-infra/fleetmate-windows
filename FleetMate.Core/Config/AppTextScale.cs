using System.Globalization;

namespace FleetMate.Core.Config;

/// <summary>
/// Text size (Settings › Appearance), as a multiple of the normal size. The
/// range and step match the macOS client: 90% to 160% in 5% steps.
/// </summary>
public static class AppTextScale
{
    public const double Min = 0.9;
    public const double Max = 1.6;
    public const double Step = 0.05;
    public const double Default = 1.0;

    public static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Round(Math.Min(Math.Max(value, Min), Max) / Step) * Step : Default;

    /// <summary>Readout for the slider, e.g. "115%".</summary>
    public static string Label(double value) => $"{(int)Math.Round(Clamp(value) * 100)}%";

    public static double Parse(string? stored) =>
        double.TryParse(stored, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? Clamp(value) : Default;

    public static string Format(double value) => Clamp(value).ToString("0.00", CultureInfo.InvariantCulture);

    public static bool IsDefault(double value) => Math.Abs(Clamp(value) - Default) < 0.001;
}
