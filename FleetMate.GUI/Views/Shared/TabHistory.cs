namespace FleetMate.GUI.Views.Shared;

/// <summary>
/// Back and Forward over the tabs visited, as FleetMate for Mac keeps it:
/// visiting a tab clears Forward, and moving through history records nothing.
/// </summary>
internal sealed class TabHistory
{
    private readonly List<string> _back = new();
    private readonly List<string> _forward = new();
    private const int Limit = 50;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Record leaving <paramref name="from"/> for <paramref name="to"/>.</summary>
    public void Visit(string from, string to)
    {
        if (from == to) return;
        _back.Add(from);
        if (_back.Count > Limit) _back.RemoveAt(0);
        _forward.Clear();
    }

    /// <summary>The tab to show for Back from <paramref name="current"/>, or null.</summary>
    public string? Back(string current) => Move(_back, _forward, current);

    /// <summary>The tab to show for Forward from <paramref name="current"/>, or null.</summary>
    public string? Forward(string current) => Move(_forward, _back, current);

    private static string? Move(List<string> from, List<string> to, string current)
    {
        if (from.Count == 0) return null;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(current);
        return target;
    }
}

/// <summary>
/// The zoom saved before zoom and Text size were one setting. Read once to carry
/// it over; <see cref="FleetMate.Core.Config.AppTextScale"/> owns the range and step.
/// </summary>
internal static class ZoomScale
{
    public const double Min = 0.9;
    public const double Max = 1.6;
    public const double Default = 1.0;

    public static double Clamp(double value) => Math.Round(Math.Min(Max, Math.Max(Min, value)), 2);

    public static string StatePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FleetMate", "zoom.txt");

    public static double Load()
    {
        try
        {
            return double.TryParse(System.IO.File.ReadAllText(StatePath).Trim(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? Clamp(value)
                : Default;
        }
        catch (Exception)
        {
            return Default;
        }
    }

    public static void Save(double value)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(StatePath)!);
            System.IO.File.WriteAllText(StatePath, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Could not save the zoom level");
        }
    }
}
