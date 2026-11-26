namespace FleetMate.Core.Shared;

/// <summary>One bar or slice; <see cref="Label"/> is also the filter value a click applies.</summary>
public sealed record ChartSlice(string Label, int Value)
{
    /// <summary>
    /// The value a widget click hands its tab: a label ending in "(count)"
    /// filters on the name before it, as on the Mac.
    /// </summary>
    public static string FilterValue(string label)
    {
        var cut = label.IndexOf(" (", StringComparison.Ordinal);
        return cut < 0 ? label : label[..cut];
    }
}
