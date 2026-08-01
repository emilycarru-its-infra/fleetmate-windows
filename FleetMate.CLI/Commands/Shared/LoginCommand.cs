using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
using FleetMate.Core.Services.Tickets;
using Spectre.Console;

namespace FleetMate.Commands.Shared;

/// <summary>
/// <c>fleetmate login</c> — one sign-in for the whole FleetMate estate, and a
/// health check across every system.
///
/// On Windows there is usually nothing to sign in to. The account broker
/// redeems the device's Primary Refresh Token, so on a managed machine every
/// system below authenticates silently as the operator and this command is
/// really an auth status matrix. Where the broker cannot help — an unjoined
/// machine, or a resource the operator has never consented to — it says which
/// system needs what, rather than reporting a flat failure.
/// </summary>
public static class LoginCommand
{
    private enum AuthStatus { Ok, Failed, Unverified }

    private sealed record SystemReport(string System, AuthStatus Status, string Detail);

    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("login",
            "Sign in to Entra and verify auth across every FleetMate system");

        var checkOnly = new Option<bool>("--check",
            "Only verify auth; never prompt, even if a system needs interactive consent");
        var asJson = new Option<bool>(new[] { "--json", "-j" }, "Output as JSON");
        var desktop = new Option<bool>("--desktop",
            "Use the desktop app's secretless configuration (ignore all locally stored credentials)");
        var strict = new Option<bool>("--strict",
            "Fail when any configured system cannot authenticate or read data");
        var deferBrowser = new Option<bool>("--defer-browser",
            "Defer browser-only systems to the desktop WebView smoke runner");

        command.AddOption(checkOnly);
        command.AddOption(asJson);
        command.AddOption(desktop);
        command.AddOption(strict);
        command.AddOption(deferBrowser);

        command.SetHandler(async (InvocationContext context) =>
        {
            var effectiveConfig = context.ParseResult.GetValueForOption(desktop)
                ? FleetMateConfig.LoadDesktop()
                : config;
            context.ExitCode = await ExecuteAsync(
                effectiveConfig,
                context.ParseResult.GetValueForOption(checkOnly),
                context.ParseResult.GetValueForOption(asJson),
                context.ParseResult.GetValueForOption(strict),
                context.ParseResult.GetValueForOption(deferBrowser));
        });

        return command;
    }

    private static async Task<int> ExecuteAsync(FleetMateConfig config, bool check, bool json, bool strict, bool deferBrowser)
    {
        var reports = new List<SystemReport>();

        // --check must never prompt. Clearing the provider guarantees it: the
        // token source falls back to "no window to prompt in" and fails fast
        // rather than blocking a scripted run on a dialog nobody will answer.
        if (check)
        {
            EntraTokenSource.ParentWindowProvider = null;
        }

        // ── Entra — the trust anchor everything else rides on ────────────────
        var identity = await ProbeEntraAsync(config);
        reports.Add(identity);
        var entraOk = identity.Status == AuthStatus.Ok;

        // ── Graph / Intune / Entra directory ─────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.Graph?.TenantId))
        {
            reports.Add(await ProbeGraphAsync(config, entraOk));
        }

        // ── Azure DevOps ─────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.AzureDevOps?.Organization))
        {
            reports.Add(await ProbeDevOpsAsync(config));
        }

        // ── Snipe-IT ─────────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.SnipeUrl))
        {
            reports.Add(await ProbeSnipeAsync(config));
        }

        // ── ReportMate ───────────────────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(config.ReportMateUrl))
        {
            reports.Add(await ProbeReportMateAsync(config));
        }

        // ── TeamDynamix ──────────────────────────────────────────────────────
        if (!deferBrowser && !string.IsNullOrWhiteSpace(config.Tdx?.BaseUrl))
        {
            reports.Add(await ProbeTdxAsync(config));
        }

        if (json) PrintJson(reports);
        else PrintMatrix(reports, check);

        // Only a broken trust anchor is a failure exit. A single unreachable
        // service should not fail a scripted health check that is really asking
        // "is my sign-in good?".
        return strict
            ? (reports.All(r => r.Status == AuthStatus.Ok) ? 0 : 1)
            : (entraOk ? 0 : 1);
    }

    // MARK: - Probes

    private static async Task<SystemReport> ProbeEntraAsync(FleetMateConfig config)
    {
        var source = EntraTokenSource.Shared
            ?? EntraTokenSource.Configure(config.Graph?.TenantId, config.EntraClientId);

        try
        {
            var token = await source.GetTokenAsync("https://graph.microsoft.com");
            var (upn, tenant) = ReadIdentity(token);
            var detail = upn ?? "signed in";
            if (tenant != null) detail += $" · tenant {ShortId(tenant)}";
            return new SystemReport("Entra", AuthStatus.Ok, detail);
        }
        catch (EntraTokenException ex)
        {
            return new SystemReport("Entra", AuthStatus.Failed, ex.Message.OneLine());
        }
        catch (Exception ex)
        {
            return new SystemReport("Entra", AuthStatus.Failed, ex.Message.OneLine());
        }
    }

    private static async Task<SystemReport> ProbeGraphAsync(FleetMateConfig config, bool entraOk)
    {
        if (!entraOk)
            return new SystemReport("Graph / Intune", AuthStatus.Failed, "needs an Entra sign-in");

        try
        {
            using var graph = new GraphService(config.Graph!, config.Elevation);
            var devices = await graph.GetManagedDevicesAsync(limit: 1);
            return new SystemReport("Graph / Intune", AuthStatus.Ok,
                devices.Count > 0 ? "reachable · managed devices visible" : "reachable");
        }
        catch (Exception ex)
        {
            return new SystemReport("Graph / Intune", AuthStatus.Failed, ex.Message.OneLine());
        }
    }

    private static async Task<SystemReport> ProbeDevOpsAsync(FleetMateConfig config)
    {
        try
        {
            using var devops = new AzureDevOpsService(config.AzureDevOps!);
            var ok = await devops.VerifyAuthAsync();
            return new SystemReport("Azure DevOps",
                ok ? AuthStatus.Ok : AuthStatus.Failed,
                ok ? config.AzureDevOps!.Organization ?? "" : "auth failed — Entra sign-in required");
        }
        catch (Exception ex)
        {
            return new SystemReport("Azure DevOps", AuthStatus.Failed, ex.Message.OneLine());
        }
    }

    private static async Task<SystemReport> ProbeSnipeAsync(FleetMateConfig config)
    {
        using var snipe = SnipeService.FromConfig(config);
        try
        {
            var assets = await snipe.GetAssetsAsync();
            return new SystemReport("Snipe-IT", AuthStatus.Ok,
                snipe.UsesOidc
                    ? $"SSO identity valid · {assets.Count} assets"
                    : $"legacy API key · {assets.Count} assets");
        }
        catch (Exception ex)
        {
            return Classify("Snipe-IT", ex, snipe.UsesOidc);
        }
    }

    private static async Task<SystemReport> ProbeReportMateAsync(FleetMateConfig config)
    {
        using var reportMate = ReportMateService.FromConfig(config);
        try
        {
            var devices = await reportMate.GetDevicesAsync();
            return new SystemReport("ReportMate", AuthStatus.Ok,
                reportMate.UsesOidc
                    ? $"SSO identity valid · {devices.Count} devices"
                    : $"legacy passphrase · {devices.Count} devices");
        }
        catch (Exception ex)
        {
            return Classify("ReportMate", ex, reportMate.UsesOidc);
        }
    }

    private static async Task<SystemReport> ProbeTdxAsync(FleetMateConfig config)
    {
        try
        {
            var sso = new TdxSsoService(config.Tdx!.BaseUrl!);
            var result = await sso.TrySilentSsoAsync();

            return result.Success
                ? new SystemReport("TeamDynamix", AuthStatus.Ok,
                    $"SSO as {result.UserName ?? result.UserEmail ?? "operator"}")
                : new SystemReport("TeamDynamix", AuthStatus.Failed,
                    "silent SSO produced no token — sign in from the app");
        }
        catch (Exception ex)
        {
            return new SystemReport("TeamDynamix", AuthStatus.Failed, ex.Message.OneLine());
        }
    }

    /// <summary>
    /// Separate "you are not signed in" from "you are signed in but lack a role"
    /// from "the service is unreachable". Collapsing these sends people to
    /// re-authenticate over a permissions problem, which cannot help.
    /// </summary>
    private static SystemReport Classify(string system, Exception ex, bool usesOidc)
    {
        if (ex is EntraTokenException)
            return new SystemReport(system, AuthStatus.Failed,
                "Entra sign-in required — run `fleetmate login`");

        var message = ex.Message.OneLine();

        if (message.Contains("401") || message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return new SystemReport(system, AuthStatus.Failed,
                usesOidc ? "token rejected — check the audience and your role assignment" : "credential rejected");

        if (message.Contains("403") || message.Contains("Forbidden", StringComparison.OrdinalIgnoreCase))
            return new SystemReport(system, AuthStatus.Failed,
                "signed in, but no role on this resource");

        return new SystemReport(system, AuthStatus.Failed, message);
    }

    // MARK: - Identity helpers

    /// <summary>Read the UPN and tenant from an access token's claims, best-effort.</summary>
    internal static (string? Upn, string? TenantId) ReadIdentity(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length < 2) return (null, null);

            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            var remainder = payload.Length % 4;
            if (remainder > 0) payload += new string('=', 4 - remainder);

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var root = doc.RootElement;

            string? upn = null;
            foreach (var claim in new[] { "upn", "preferred_username", "unique_name", "email" })
            {
                if (root.TryGetProperty(claim, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    upn = v.GetString();
                    if (!string.IsNullOrEmpty(upn)) break;
                }
            }

            string? tenant = root.TryGetProperty("tid", out var tid) && tid.ValueKind == JsonValueKind.String
                ? tid.GetString()
                : null;

            return (upn, tenant);
        }
        catch
        {
            // A token we cannot parse is still a token that worked.
            return (null, null);
        }
    }

    internal static string ShortId(string id) => id.Length > 8 ? id[..8] + "…" : id;

    // MARK: - Output

    private static void PrintMatrix(List<SystemReport> reports, bool check)
    {
        var table = new Table { Border = TableBorder.Rounded };
        table.Title = new TableTitle(check ? "[cyan]Auth check[/]" : "[cyan]FleetMate sign-in[/]");
        table.AddColumn("System");
        table.AddColumn("Status");
        table.AddColumn("Detail");

        foreach (var r in reports)
        {
            var status = r.Status switch
            {
                AuthStatus.Ok => "[green]✓ ok[/]",
                AuthStatus.Unverified => "[yellow]● unverified[/]",
                _ => "[red]✗ failed[/]",
            };
            table.AddRow(Markup.Escape(r.System), status, Markup.Escape(r.Detail));
        }

        AnsiConsole.Write(table);

        if (reports.Any(r => r.Status == AuthStatus.Failed))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[dim]FleetMate authenticates as you — there is no service account to fall back to. " +
                "A failure above means that system needs your sign-in or a role assignment.[/]");
        }
    }

    private static void PrintJson(List<SystemReport> reports)
    {
        var payload = reports.ToDictionary(
            r => r.System,
            r => new Dictionary<string, string>
            {
                ["status"] = r.Status.ToString().ToLowerInvariant(),
                ["detail"] = r.Detail,
            });

        Console.WriteLine(JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class LoginStringExtensions
{
    /// <summary>Collapse a multi-line exception message into one table-friendly line.</summary>
    public static string OneLine(this string s)
    {
        var line = s.Split('\n', '\r').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim() ?? s;
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
