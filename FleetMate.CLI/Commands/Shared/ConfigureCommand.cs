using System.CommandLine;
using FleetMate.Core.Config;
using Spectre.Console;

namespace FleetMate.Commands.Shared;

/// <summary>
/// Configure FleetMate credentials - stores in Windows Registry (CSP OMA-URI style)
/// </summary>
public static class ConfigureCommand
{
    public static Command Create(FleetMateConfig config)
    {
        var command = new Command("configure", "Configure FleetMate credentials (stores in Windows Registry)");
        
        // Subcommands
        command.AddCommand(CreateSetCommand());
        command.AddCommand(CreateShowCommand(config));
        command.AddCommand(CreateClearCommand());
        command.AddCommand(CreateImportCommand());
        
        // Default behavior: interactive setup or status
        command.SetHandler(() =>
        {
            ShowConfigurationStatus(config);
        });
        
        return command;
    }
    
    private static Command CreateSetCommand()
    {
        var setCommand = new Command("set", "Set credentials (interactive or via options)");
        
        var passphraseOption = new Option<string?>(
            aliases: ["--passphrase", "-p"],
            description: "ReportMate API passphrase");
        
        var urlOption = new Option<string?>(
            aliases: ["--url", "-u"],
            description: "ReportMate API URL (optional, uses default if not specified)");
        
        var snipeUrlOption = new Option<string?>(
            aliases: ["--snipe-url"],
            description: "Snipe-IT API URL");
        
        var snipeKeyOption = new Option<string?>(
            aliases: ["--snipe-key"],
            description: "Snipe-IT API key");
        
        var interactiveOption = new Option<bool>(
            aliases: ["--interactive", "-i"],
            description: "Interactive mode - prompt for values");
        
        setCommand.AddOption(passphraseOption);
        setCommand.AddOption(urlOption);
        setCommand.AddOption(snipeUrlOption);
        setCommand.AddOption(snipeKeyOption);
        setCommand.AddOption(interactiveOption);
        
        setCommand.SetHandler((passphrase, url, snipeUrl, snipeKey, interactive) =>
        {
            if (interactive || (string.IsNullOrEmpty(passphrase) && string.IsNullOrEmpty(snipeUrl)))
            {
                RunInteractiveSetup();
            }
            else
            {
                SetCredentials(url, passphrase, snipeUrl, snipeKey);
            }
        }, passphraseOption, urlOption, snipeUrlOption, snipeKeyOption, interactiveOption);
        
        return setCommand;
    }
    
    private static Command CreateShowCommand(FleetMateConfig config)
    {
        var showCommand = new Command("show", "Show current configuration (masks secrets)");
        
        var revealOption = new Option<bool>(
            aliases: ["--reveal", "-r"],
            description: "Show actual secret values (use with caution)");
        
        showCommand.AddOption(revealOption);
        
        showCommand.SetHandler((reveal) =>
        {
            ShowConfiguration(config, reveal);
        }, revealOption);
        
        return showCommand;
    }
    
    private static Command CreateClearCommand()
    {
        var clearCommand = new Command("clear", "Remove all credentials from registry");
        
        var forceOption = new Option<bool>(
            aliases: ["--force", "-f"],
            description: "Skip confirmation prompt");
        
        clearCommand.AddOption(forceOption);
        
        clearCommand.SetHandler((force) =>
        {
            if (!force)
            {
                if (!AnsiConsole.Confirm("Clear all FleetMate credentials from registry?", false))
                {
                    AnsiConsole.MarkupLine("[yellow]Aborted[/]");
                    return;
                }
            }
            
            FleetMateConfig.ClearRegistry();
            AnsiConsole.MarkupLine("[green]✓[/] Registry credentials cleared");
        }, forceOption);
        
        return clearCommand;
    }
    
    private static Command CreateImportCommand()
    {
        var importCommand = new Command("import", "Import credentials from terraform.tfvars file");
        
        var fileOption = new Option<string?>(
            aliases: ["--file", "-f"],
            description: "Path to terraform.tfvars file (defaults to repo infrastructure/terraform.tfvars)");
        
        importCommand.AddOption(fileOption);
        
        importCommand.SetHandler((file) =>
        {
            ImportFromTerraformVars(file);
        }, fileOption);
        
        return importCommand;
    }
    
    private static void ShowConfigurationStatus(FleetMateConfig config)
    {
        var hasRegistry = FleetMateConfig.HasRegistryCredentials();
        
        AnsiConsole.MarkupLine("[bold blue]FleetMate Configuration[/]");
        AnsiConsole.WriteLine();
        
        var table = new Table();
        table.AddColumn("Source");
        table.AddColumn("Status");
        table.AddColumn("Details");
        
        // Registry status
        table.AddRow(
            "[cyan]Registry[/] (HKCU\\SOFTWARE\\FleetMate)",
            hasRegistry ? "[green]✓ Configured[/]" : "[yellow]○ Not set[/]",
            hasRegistry ? "Credentials stored" : "Run 'fleetmate configure set' or 'fleetmate configure import'"
        );
        
        // Environment variable status
        var envPassphrase = Environment.GetEnvironmentVariable("REPORTMATE_PASSPHRASE");
        table.AddRow(
            "[cyan]Environment[/] ($env:REPORTMATE_PASSPHRASE)",
            !string.IsNullOrEmpty(envPassphrase) ? "[green]✓ Set[/]" : "[dim]○ Not set[/]",
            !string.IsNullOrEmpty(envPassphrase) ? "Active for this session" : ""
        );
        
        // Effective configuration
        var effectivePassphrase = config.ReportMatePassphrase;
        table.AddRow(
            "[bold]Effective Config[/]",
            !string.IsNullOrEmpty(effectivePassphrase) ? "[green]✓ Ready[/]" : "[red]✗ Missing passphrase[/]",
            !string.IsNullOrEmpty(effectivePassphrase) ? $"URL: {config.ReportMateUrl}" : "FleetMate cannot connect to ReportMate"
        );
        
        AnsiConsole.Write(table);
        
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Credential precedence: Environment > Registry > .env files > config.yaml[/]");
        
        if (string.IsNullOrEmpty(effectivePassphrase))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Quick setup options:[/]");
            AnsiConsole.MarkupLine("  [cyan]fleetmate configure import[/]     - Import from terraform.tfvars");
            AnsiConsole.MarkupLine("  [cyan]fleetmate configure set -i[/]     - Interactive setup");
            AnsiConsole.MarkupLine("  [cyan]fleetmate configure set -p XXX[/] - Set passphrase directly");
        }
    }
    
    private static void ShowConfiguration(FleetMateConfig config, bool reveal)
    {
        AnsiConsole.MarkupLine("[bold blue]FleetMate Configuration Details[/]");
        AnsiConsole.WriteLine();
        
        var table = new Table();
        table.AddColumn("Setting");
        table.AddColumn("Value");
        table.AddColumn("Source");
        
        // ReportMate URL
        table.AddRow(
            "ReportMate URL",
            config.ReportMateUrl ?? "[dim](not set)[/]",
            DetermineSource("REPORTMATE_URL", "ReportMateUrl")
        );
        
        // ReportMate Passphrase
        var passphrase = config.ReportMatePassphrase ?? "";
        var displayPassphrase = reveal ? passphrase : MaskSecret(passphrase);
        table.AddRow(
            "ReportMate Passphrase",
            displayPassphrase,
            DetermineSource("REPORTMATE_PASSPHRASE", "ReportMatePassphrase")
        );
        
        // Snipe-IT
        if (!string.IsNullOrEmpty(config.SnipeUrl))
        {
            table.AddRow(
                "Snipe-IT URL",
                config.SnipeUrl,
                DetermineSource("SNIPE_URL", "SnipeUrl")
            );
            
            var snipeKey = config.SnipeApiKey ?? "";
            var displaySnipeKey = reveal ? snipeKey : MaskSecret(snipeKey);
            table.AddRow(
                "Snipe-IT API Key",
                displaySnipeKey,
                DetermineSource("SNIPE_API_KEY", "SnipeApiKey")
            );
        }
        
        AnsiConsole.Write(table);
    }
    
    private static string DetermineSource(string envVar, string registryValue)
    {
        // Check environment variable first (highest priority)
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(envVar)))
            return "[green]Environment[/]";
        
        // Check registry
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\FleetMate");
            if (key?.GetValue(registryValue) != null)
                return "[cyan]Registry[/]";
        }
        catch { }
        
        // Must be from config file or default
        return "[dim]Config/Default[/]";
    }
    
    private static string MaskSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return "[dim](not set)[/]";
        if (secret.Length <= 8) return new string('*', secret.Length);
        return $"{secret[..4]}{'*'.ToString().PadLeft(secret.Length - 8, '*')}{secret[^4..]}";
    }
    
    private static void RunInteractiveSetup()
    {
        AnsiConsole.MarkupLine("[bold blue]FleetMate Credential Setup[/]");
        AnsiConsole.WriteLine();
        
        var url = AnsiConsole.Prompt(
            new TextPrompt<string>("ReportMate API URL:")
                .DefaultValue("https://reportmate.example.com")
                .ShowDefaultValue());
        
        var passphrase = AnsiConsole.Prompt(
            new TextPrompt<string>("ReportMate Passphrase:")
                .Secret('*'));
        
        var configureSnipe = AnsiConsole.Confirm("Configure Snipe-IT integration?", false);
        
        string? snipeUrl = null;
        string? snipeKey = null;
        
        if (configureSnipe)
        {
            snipeUrl = AnsiConsole.Ask<string>("Snipe-IT URL:");
            snipeKey = AnsiConsole.Prompt(new TextPrompt<string>("Snipe-IT API Key:").Secret('*'));
        }
        
        SetCredentials(url, passphrase, snipeUrl, snipeKey);
    }
    
    private static void SetCredentials(string? url, string? passphrase, string? snipeUrl, string? snipeKey)
    {
        try
        {
            FleetMateConfig.SaveToRegistry(url, passphrase, snipeUrl, snipeKey);
            
            AnsiConsole.MarkupLine("[green]✓[/] Credentials saved to registry");
            AnsiConsole.MarkupLine("[dim]Location: HKCU\\SOFTWARE\\FleetMate[/]");
            
            // Verify by reloading
            var config = FleetMateConfig.Load();
            if (!string.IsNullOrEmpty(config.ReportMatePassphrase))
            {
                AnsiConsole.MarkupLine("[green]✓[/] Configuration verified - FleetMate is ready");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Failed to save credentials: {ex.Message}");
        }
    }
    
    private static void ImportFromTerraformVars(string? file)
    {
        // Find terraform.tfvars
        string? tfvarsPath = null;
        
        if (!string.IsNullOrEmpty(file))
        {
            tfvarsPath = file;
        }
        else
        {
            // Try to find repo root
            var current = Directory.GetCurrentDirectory();
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, "infrastructure", "terraform.tfvars");
                if (File.Exists(candidate))
                {
                    tfvarsPath = candidate;
                    break;
                }
                var parent = Directory.GetParent(current);
                if (parent == null) break;
                current = parent.FullName;
            }
            
            tfvarsPath ??= Path.Combine(Directory.GetCurrentDirectory(), "infrastructure", "terraform.tfvars");
        }
        
        if (!File.Exists(tfvarsPath))
        {
            AnsiConsole.MarkupLine($"[red]✗[/] File not found: {tfvarsPath}");
            AnsiConsole.MarkupLine("[yellow]Hint:[/] Run [cyan].githooks/post-checkout --force[/] to generate terraform.tfvars from Key Vault");
            return;
        }
        
        AnsiConsole.MarkupLine($"[dim]Importing from: {tfvarsPath}[/]");
        
        try
        {
            var lines = File.ReadAllLines(tfvarsPath);
            string? passphrase = null;
            string? apiUrl = null;
            
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("reportmate_passphrase"))
                {
                    passphrase = ExtractTfValue(trimmed);
                }
                else if (trimmed.StartsWith("reportmate_api_url"))
                {
                    apiUrl = ExtractTfValue(trimmed);
                }
            }
            
            if (string.IsNullOrEmpty(passphrase))
            {
                AnsiConsole.MarkupLine("[red]✗[/] Could not find reportmate_passphrase in terraform.tfvars");
                return;
            }
            
            FleetMateConfig.SaveToRegistry(apiUrl, passphrase);
            
            AnsiConsole.MarkupLine("[green]✓[/] Imported credentials from terraform.tfvars");
            AnsiConsole.MarkupLine("[dim]Stored in: HKCU\\SOFTWARE\\FleetMate[/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗[/] Import failed: {ex.Message}");
        }
    }
    
    private static string? ExtractTfValue(string line)
    {
        // Parse: key = "value" or key = "value" # comment
        var eqIndex = line.IndexOf('=');
        if (eqIndex < 0) return null;
        
        var valuepart = line[(eqIndex + 1)..].Trim();
        
        // Remove quotes
        if (valuepart.StartsWith('"') && valuepart.Contains('"', StringComparison.Ordinal))
        {
            var endQuote = valuepart.IndexOf('"', 1);
            if (endQuote > 0)
            {
                return valuepart[1..endQuote];
            }
        }
        
        return valuepart.Trim('"', '\'');
    }
}
