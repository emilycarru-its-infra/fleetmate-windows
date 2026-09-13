using System.CommandLine;
using FleetMate.Core.Config;
using FleetMate.Core.Services;
using Spectre.Console;

namespace FleetMate.Commands.Shared;

/// <summary>
/// Explicit-domain elevation — the command-line face of the aze protocol.
///
/// Every call runs as the domain's managed identity (DevOps-Terraform, -Devices,
/// -Identity, -Systems, -Cloud, -Security) inside an elevation session container.
/// Nothing here uses the operator's own directory roles or PIM: the operator only
/// needs elevation-operators membership to start the session, and the identity's
/// token never leaves Azure — only the JSON result comes back.
///
/// Requests are restricted to <c>az rest</c> against Graph or ARM, the same shape
/// <see cref="ElevationHttpHandler"/> builds, so the ElevationSession guard applies.
/// </summary>
public static class ElevateCommand
{
    public static Command Create(ElevationConfig? config)
    {
        var command = new Command("elevate", "Run Graph/ARM requests as a domain managed identity (aze)");
        command.AddCommand(CreateStatusCommand(config));
        command.AddCommand(CreateRestCommand(config));
        command.AddCommand(CreateStopCommand(config));
        return command;
    }

    private static readonly string DomainList =
        string.Join(", ", Enum.GetValues<GraphDomain>().Select(d => d.Slug()));

    private static bool TryParseDomain(string value, out GraphDomain domain)
    {
        foreach (var d in Enum.GetValues<GraphDomain>())
        {
            if (string.Equals(d.Slug(), value, StringComparison.OrdinalIgnoreCase))
            {
                domain = d;
                return true;
            }
        }
        domain = default;
        return false;
    }

    private static ElevationSession? SessionOrReport(ElevationConfig? config)
    {
        if (config is { IsConfigured: true }) return new ElevationSession(config);
        AnsiConsole.MarkupLine("[red]Elevation is not configured.[/] Set elevation.resourceGroup, acrImage, transcriptAccount and identityPrefix.");
        Environment.ExitCode = 1;
        return null;
    }

    private static Command CreateStatusCommand(ElevationConfig? config)
    {
        var command = new Command("status", "Show each domain's managed identity and session state");
        command.SetHandler(async () =>
        {
            var session = SessionOrReport(config);
            if (session is null) return;

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Domain");
            table.AddColumn("Managed identity");
            table.AddColumn("Session");

            foreach (var d in Enum.GetValues<GraphDomain>())
            {
                string state;
                try { state = await session.GetSessionStateAsync(d) ?? "none"; }
                catch (Exception ex) { state = "error: " + ex.Message; }
                var shown = state == "Running" ? "[green]Running[/]" : Markup.Escape(state);
                table.AddRow(d.Slug(), Markup.Escape(session.IdentityNameFor(d)), shown);
            }
            AnsiConsole.Write(table);
        });
        return command;
    }

    private static Command CreateRestCommand(ElevationConfig? config)
    {
        var command = new Command("rest", "Send one Graph/ARM request as the domain's managed identity");

        var domainArg = new Argument<string>("domain", $"Elevation domain: {DomainList}");
        var methodArg = new Argument<string>("method", "HTTP method: get, post, patch, put, delete");
        var urlArg = new Argument<string>("url", "Full Graph or ARM URL, e.g. https://graph.microsoft.com/v1.0/me");
        var bodyOption = new Option<string?>(["--body", "-b"], "JSON request body");
        command.AddArgument(domainArg);
        command.AddArgument(methodArg);
        command.AddArgument(urlArg);
        command.AddOption(bodyOption);

        command.SetHandler(async (string domainName, string method, string url, string? body) =>
        {
            if (!TryParseDomain(domainName, out var domain))
            {
                AnsiConsole.MarkupLine($"[red]Unknown domain '{Markup.Escape(domainName)}'.[/] Use one of: {DomainList}");
                Environment.ExitCode = 1;
                return;
            }

            var verb = method.ToLowerInvariant();
            if (verb is not ("get" or "post" or "patch" or "put" or "delete"))
            {
                AnsiConsole.MarkupLine($"[red]Unsupported method '{Markup.Escape(method)}'.[/]");
                Environment.ExitCode = 1;
                return;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !(uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("management.azure.com", StringComparison.OrdinalIgnoreCase)))
            {
                AnsiConsole.MarkupLine("[red]Only https://graph.microsoft.com and https://management.azure.com URLs are allowed.[/]");
                Environment.ExitCode = 1;
                return;
            }

            var session = SessionOrReport(config);
            if (session is null) return;

            var cmd = $"az rest --method {verb} --uri {SingleQuote(url)}";
            if (!string.IsNullOrEmpty(body))
                cmd += $" --headers Content-Type=application/json --body {SingleQuote(body)}";
            cmd += " -o json";

            try
            {
                var (output, code) = await session.ExecAsync(domain, cmd);
                Console.WriteLine(output);
                if (code != 0) Environment.ExitCode = code;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                Environment.ExitCode = 1;
            }
        }, domainArg, methodArg, urlArg, bodyOption);

        return command;
    }

    private static Command CreateStopCommand(ElevationConfig? config)
    {
        var command = new Command("stop", "End a domain's elevation session before its TTL");
        var domainArg = new Argument<string>("domain", $"Elevation domain: {DomainList}");
        command.AddArgument(domainArg);

        command.SetHandler(async (string domainName) =>
        {
            if (!TryParseDomain(domainName, out var domain))
            {
                AnsiConsole.MarkupLine($"[red]Unknown domain '{Markup.Escape(domainName)}'.[/] Use one of: {DomainList}");
                Environment.ExitCode = 1;
                return;
            }
            var session = SessionOrReport(config);
            if (session is null) return;

            try
            {
                var stopped = await session.StopSessionAsync(domain);
                AnsiConsole.MarkupLine(stopped
                    ? $"[green]Stopped[/] the {domain.Slug()} session."
                    : $"No {domain.Slug()} session was running.");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
                Environment.ExitCode = 1;
            }
        }, domainArg);

        return command;
    }

    // Single-quote so the container shell does not expand $top/$filter/$ref/etc.
    private static string SingleQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
