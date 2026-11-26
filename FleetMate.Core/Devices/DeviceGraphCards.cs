using FleetMate.Core.Development;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Shared;

namespace FleetMate.Core.Devices;

/// <summary>The filter categories a Devices graph click hands the list.</summary>
public static class DeviceGraphCategory
{
    /// <summary>Filter the list to a platform, named as the widgets name it ("Macintosh").</summary>
    public const string Platform = "Platform";
    /// <summary>Filter the list to a compliance state ("Compliant", "Non-Compliant").</summary>
    public const string Compliance = "Compliance";
}

/// <summary>
/// The Devices figures, matching the macOS app: platforms, compliance, stale
/// check-ins and ReportMate's install errors. Both Windows apps count with these.
/// </summary>
public static class DeviceFigures
{
    /// <summary>Days without an Intune check-in after which a device counts as not synced, as on the Mac.</summary>
    public const int StaleDays = 30;

    /// <summary>Each platform's own colour, so a platform keeps it whatever its rank.</summary>
    private static readonly Dictionary<string, ChartTone> PlatformTones = new()
    {
        ["Macintosh"] = ChartTone.Orange,
        ["Windows"] = ChartTone.Blue,
        ["iOS/iPadOS"] = ChartTone.Purple,
        ["Android"] = ChartTone.Green,
        ["Linux"] = ChartTone.Teal,
        ["ChromeOS"] = ChartTone.Brown,
    };

    private static readonly ChartTone[] PlatformFallbackTones =
        { ChartTone.Blue, ChartTone.Purple, ChartTone.Orange, ChartTone.Teal, ChartTone.Brown, ChartTone.Gray };

    /// <summary>The platform name the widgets show for an Intune operating system.</summary>
    public static string PlatformLabel(string? operatingSystem) => operatingSystem switch
    {
        "macOS" => "Macintosh",
        "iOS" or "iPadOS" => "iOS/iPadOS",
        _ => operatingSystem ?? "",
    };

    /// <summary>Devices per platform, most first, top six, each with its fixed colour.</summary>
    public static List<(ChartSlice Slice, ChartTone Tone)> PlatformBreakdown(IEnumerable<IntuneDevice> devices) => devices
        .GroupBy(d => PlatformLabel(d.OperatingSystem))
        .Where(g => g.Key.Length > 0)
        .Select(g => new ChartSlice(g.Key, g.Count()))
        .OrderByDescending(s => s.Value)
        .Take(6)
        .Select((s, i) => (s, PlatformTones.TryGetValue(s.Label, out var t) ? t : PlatformFallbackTones[i % PlatformFallbackTones.Length]))
        .ToList();

    /// <summary>Non-compliant is Intune's own "noncompliant"; every other state counts as compliant, as on the Mac.</summary>
    public static bool IsNonCompliant(IntuneDevice device) =>
        string.Equals(device.ComplianceState, "noncompliant", StringComparison.OrdinalIgnoreCase);

    /// <summary>Compliant and Non-Compliant, leaving out a side with no devices.</summary>
    public static List<(ChartSlice Slice, ChartTone Tone)> ComplianceBreakdown(IReadOnlyCollection<IntuneDevice> devices)
    {
        var nonCompliant = devices.Count(IsNonCompliant);
        return new List<(ChartSlice, ChartTone)>
        {
            (new ChartSlice("Compliant", devices.Count - nonCompliant), ChartTone.Green),
            (new ChartSlice("Non-Compliant", nonCompliant), ChartTone.Orange),
        }.Where(x => x.Item1.Value > 0).ToList();
    }

    /// <summary>Devices whose last check-in is more than <see cref="StaleDays"/> days before <paramref name="now"/>; a device with no check-in is not counted.</summary>
    public static int StaleCount(IEnumerable<IntuneDevice> devices, DateTime now) =>
        devices.Count(d => d.LastSyncDateTime is { } t && (now - t.ToUniversalTime()).TotalDays > StaleDays);

    /// <summary>Errors per category, summed over the devices each error hits, top eight.</summary>
    public static List<ChartSlice> ErrorCategorySlices(IEnumerable<ErrorSummary> errors) => errors
        .GroupBy(e => e.Category)
        .Select(g => new ChartSlice(CategoryLabel(g.Key), g.Sum(e => e.DeviceCount)))
        .OrderByDescending(s => s.Value)
        .Take(8)
        .ToList();

    /// <summary>An error category's short label, as the Mac's chart names it.</summary>
    public static string CategoryLabel(ErrorCategory category) => category switch
    {
        ErrorCategory.NotFound => "Not Found",
        ErrorCategory.HashMismatch => "Hash Mismatch",
        ErrorCategory.DownloadFailed => "Download Failed",
        ErrorCategory.MsiFailure => "MSI Failure",
        ErrorCategory.SignatureRequired => "Sig Required",
        ErrorCategory.CatalogMissing => "Catalog Missing",
        ErrorCategory.MissingChocolatey => "No Chocolatey",
        ErrorCategory.MissingSbinInstaller => "No sbin-installer",
        ErrorCategory.InstallVerificationFailed => "Verify Failed",
        ErrorCategory.MissingInstallerLocation => "No Installer Loc",
        _ => category.ToString(),
    };
}

/// <summary>
/// ReportMate's install errors for the Errors by Category card: how many
/// devices report and the errors they report. Null figures mean not read yet.
/// </summary>
public sealed record DeviceErrorFigures(int? ReportingDevices, IReadOnlyList<ErrorSummary>? Errors, bool Loading);

/// <summary>
/// The Devices graph strip, matching the macOS app: the device KPIs, Platform
/// Distribution, Compliance and, when ReportMate is configured, Errors by
/// Category. Every card shares its row's height.
/// </summary>
public static class DeviceGraphCards
{
    private const string NoData = "No device data";

    public static List<GraphCard> Build(IReadOnlyCollection<IntuneDevice> devices, bool loading, DeviceErrorFigures? errors, DateTime nowUtc)
    {
        var waiting = loading && devices.Count == 0;
        var cards = new List<GraphCard>
        {
            new(GraphCardKind.Kpis, "Devices")
            {
                Kpis = new[]
                {
                    new GraphKpi("Managed Devices", devices.Count.ToString("N0"), ChartTone.Blue, waiting, null),
                    new GraphKpi("Non-Compliant", devices.Count(DeviceFigures.IsNonCompliant).ToString("N0"), ChartTone.Orange, waiting,
                        new GraphFilter(DeviceGraphCategory.Compliance, "Non-Compliant")),
                    new GraphKpi($"Not Synced in {DeviceFigures.StaleDays} Days", DeviceFigures.StaleCount(devices, nowUtc).ToString("N0"),
                        ChartTone.Gray, waiting, null),
                },
            },
        };

        var platforms = DeviceFigures.PlatformBreakdown(devices);
        cards.Add(Chart(new GraphCard(GraphCardKind.Treemap, "Platform Distribution")
        {
            Bars = platforms.Select(p => new GraphBar(p.Slice.Label, p.Slice.Value, p.Tone,
                new GraphFilter(DeviceGraphCategory.Platform, p.Slice.Label),
                $"{p.Slice.Label}, {p.Slice.Value:N0} device{(p.Slice.Value == 1 ? "" : "s")}")).ToList(),
        }, platforms.Count > 0, loading));

        var compliance = DeviceFigures.ComplianceBreakdown(devices);
        cards.Add(Chart(new GraphCard(GraphCardKind.Donut, "Compliance")
        {
            Bars = compliance.Select(c => new GraphBar(c.Slice.Label, c.Slice.Value, c.Tone,
                new GraphFilter(DeviceGraphCategory.Compliance, c.Slice.Label),
                $"{c.Slice.Label}, {c.Slice.Value:N0} device{(c.Slice.Value == 1 ? "" : "s")}")).ToList(),
        }, compliance.Count > 0, loading));

        if (errors != null) cards.Add(Errors(errors));
        return cards;
    }

    /// <summary>
    /// Errors by Category: the managed and error counts over a bar per
    /// category, or one all-clear line when nothing is failing.
    /// </summary>
    public static GraphCard Errors(DeviceErrorFigures figures)
    {
        var errors = figures.Errors ?? Array.Empty<ErrorSummary>();
        var categories = DeviceFigures.ErrorCategorySlices(errors);
        var reporting = figures.ReportingDevices ?? 0;
        var card = new GraphCard(GraphCardKind.Bars, "Errors by Category") { Loading = figures.Loading };
        if (categories.Count == 0)
        {
            return figures.Loading
                ? card with { Body = GraphCardBody.Loading }
                : card with { Body = GraphCardBody.AllClear, Message = "No errors reported", Detail = $"{reporting:N0} devices reporting" };
        }
        return card with
        {
            Kpis = new[]
            {
                new GraphKpi("Managed", reporting.ToString("N0"), ChartTone.Blue, false, null),
                new GraphKpi("Errors", errors.Count.ToString("N0"), ChartTone.Orange, false, null),
            },
            Bars = categories.Select(c => new GraphBar(c.Label, c.Value, ChartTone.Orange, null,
                $"{c.Label}, {c.Value:N0} device{(c.Value == 1 ? "" : "s")}")).ToList(),
        };
    }

    private static GraphCard Chart(GraphCard card, bool hasData, bool loading) =>
        hasData ? card with { Loading = loading }
        : loading ? card with { Loading = true, Body = GraphCardBody.Loading }
        : card with { Body = GraphCardBody.Empty, Message = NoData };
}
