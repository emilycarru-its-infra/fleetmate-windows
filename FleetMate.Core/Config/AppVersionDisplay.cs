using System.Reflection;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Config;

/// <summary>
/// The version string the About pane shows. A release build is stamped with
/// <c>-p:InformationalVersion=YYYY.MM.DD.HHMM</c> (see build.ps1), the same
/// value <c>fleetmate --version</c> reports. A local build carries the SDK's
/// default <c>1.0.0+&lt;commit&gt;</c> and reads "dev", with the commit it was
/// built from when that is known. The assembly version is never shown: it is
/// numeric-only and reads 1.0.0 on every local build.
/// </summary>
public static partial class AppVersionDisplay
{
    public static string Format(string? informationalVersion)
    {
        var value = informationalVersion?.Trim() ?? "";
        var plus = value.IndexOf('+');
        var version = plus >= 0 ? value[..plus] : value;
        var metadata = plus >= 0 ? value[(plus + 1)..] : "";

        if (StampPattern().IsMatch(version)) return version;
        if (CommitPattern().IsMatch(metadata)) return $"dev ({metadata[..Math.Min(7, metadata.Length)]})";
        return "dev";
    }

    /// <summary>The display version of <paramref name="assembly"/>.</summary>
    public static string For(Assembly assembly) =>
        Format(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    [GeneratedRegex(@"^20\d{2}\.\d{1,2}\.\d{1,2}\.\d{1,4}$")]
    private static partial Regex StampPattern();

    [GeneratedRegex(@"^[0-9a-fA-F]{7,40}$")]
    private static partial Regex CommitPattern();
}
