using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// The address of the work account Windows is signed in with, read from the
/// device rather than asked of anyone. Every silent sign-in uses it: it is the
/// address a TeamDynamix session must belong to, the tile picked in Entra's
/// account picker, and the login hint for a token.
///
/// No one source is reliable on its own. The sign-in broker (WAM) will not
/// surface the operating-system account in a disconnected remote session, and
/// a cloud-only account has no UPN on its logon token. So the sources are tried
/// in order and the first address wins:
/// <list type="number">
/// <item>The Windows logon: <c>GetUserNameEx(NameUserPrincipal)</c>, then the UPN claim on the logon token.</item>
/// <item>The broker's operating-system account (the identity behind the device's primary refresh token).</item>
/// <item>The registry: the IdentityStore cache for this user's SID, then the
/// address the device was Entra-joined with (CloudDomainJoin JoinInfo).</item>
/// </list>
/// Only when all of them are empty is the address unknown.
/// </summary>
public static class WindowsAccount
{
    /// <summary>One place an address may be read from.</summary>
    public sealed record Source(string Name, Func<CancellationToken, Task<string?>> Read);

    /// <summary>The first source that yields an address, and its name; (null, null) when none does.</summary>
    public static async Task<(string? Upn, string? From)> FirstAsync(IEnumerable<Source> sources, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Normalize(await source.Read(ct)) is { } upn) return (upn, source.Name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Log.Debug(ex, "[account] {Source} gave no address", source.Name);
            }
        }
        return (null, null);
    }

    /// <summary>
    /// The sources in order, with the broker's account between the logon and
    /// the registry when a broker is given.
    /// </summary>
    public static IEnumerable<Source> Sources(Func<CancellationToken, Task<string?>>? broker)
    {
        yield return new Source("Windows logon", _ => Task.FromResult(LogonUpn()));
        if (broker != null) yield return new Source("sign-in broker", broker);
        yield return new Source("registry", _ => Task.FromResult(RegistryUpn()));
    }

    /// <summary>The address from the logon and the registry, without the broker.</summary>
    public static string? ResolveWithoutBroker() => LogonUpn() ?? RegistryUpn();

    /// <summary>A trimmed, lower-cased address, or null for anything that is not one (a down-level <c>DOMAIN\user</c> name included).</summary>
    public static string? Normalize(string? value)
    {
        var trimmed = value?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) || !trimmed.Contains('@') || trimmed.Contains('\\') ? null : trimmed;
    }

    /// <summary><c>GetUserNameEx(NameUserPrincipal)</c>, then the UPN claim on the logon token.</summary>
    public static string? LogonUpn()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var size = 512u;
            var buffer = new StringBuilder((int)size);
            if (GetUserNameEx(NameUserPrincipal, buffer, ref size) && Normalize(buffer.ToString()) is { } upn)
                return upn;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Log.Debug(ex, "[account] GetUserNameEx is unavailable");
        }

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var claim = identity.Claims.FirstOrDefault(c =>
                c.Type == "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn");
            return Normalize(claim?.Value) ?? Normalize(identity.Name);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[account] Could not read the Windows identity");
            return null;
        }
    }

    /// <summary>
    /// The Entra account Windows recorded for this user: the IdentityStore
    /// cache under the user's own SID, then the address the device was joined with.
    /// </summary>
    public static string? RegistryUpn()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return IdentityStoreUpn() ?? JoinInfoUpn();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[account] Could not read the Entra account from the registry");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? IdentityStoreUpn()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        if (string.IsNullOrEmpty(sid)) return null;
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"SOFTWARE\Microsoft\IdentityStore\Cache\{sid}\IdentityCache\{sid}");
        return Normalize(key?.GetValue("UserName") as string);
    }

    [SupportedOSPlatform("windows")]
    private static string? JoinInfoUpn()
    {
        using var joins = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\CloudDomainJoin\JoinInfo");
        if (joins == null) return null;
        foreach (var name in joins.GetSubKeyNames())
        {
            using var join = joins.OpenSubKey(name);
            if (Normalize(join?.GetValue("UserEmail") as string) is { } upn) return upn;
        }
        return null;
    }

    private const int NameUserPrincipal = 8;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int nameFormat, StringBuilder userName, ref uint userNameSize);
}
