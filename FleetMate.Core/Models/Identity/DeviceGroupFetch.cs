namespace FleetMate.Core.Models.Identity;

/// <summary>
/// How many <c>Devices-</c> groups to pull.
///
/// One constant because the call sites used to disagree, and the disagreement
/// was invisible: the launch preload populated the group cache first and left it
/// valid, so the Identity page's own refresh was skipped entirely and the
/// preload's lower cap silently won. The list showed "100 of 100" no matter what
/// the page asked for.
///
/// Graph pagination handles the rest — <c>SearchGroupsAsync</c> follows
/// <c>@odata.nextLink</c> until it has the requested count, and the per-page
/// ceiling is clamped separately in GraphService.
/// </summary>
public static class DeviceGroupFetch
{
    public const int Limit = 1000;
}
