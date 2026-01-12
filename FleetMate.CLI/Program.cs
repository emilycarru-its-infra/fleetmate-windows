using System.CommandLine;
using FleetMate.Commands;
using FleetMate.Config;
using FleetMate.Services;
using Serilog;
using Serilog.Events;

namespace FleetMate;

class Program
{
    static async Task<int> Main(string[] args)
    {
        // Load configuration
        var config = FleetMateConfig.Load();
        
        // Setup logging
        var logPath = Path.Combine(config.LogPath, "fleetmate-.log");
        Directory.CreateDirectory(config.LogPath);
        
        var logLevel = Enum.TryParse<LogEventLevel>(config.LogLevel, true, out var level) 
            ? level : LogEventLevel.Information;
        
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            .WriteTo.File(logPath, 
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        
        try
        {
            // Create services
            using var reportMate = new ReportMateService(
                config.ReportMateUrl, 
                config.ReportMatePassphrase,
                config.CacheMinutes);
            
            var pkgInfoService = new PkgInfoService(config);
            
            // Create Snipe-IT service if configured
            SnipeService? snipeService = null;
            if (!string.IsNullOrEmpty(config.SnipeUrl) && !string.IsNullOrEmpty(config.SnipeApiKey))
            {
                snipeService = new SnipeService(config.SnipeUrl, config.SnipeApiKey);
            }

            // Create SSH service if configured
            SshService? sshService = null;
            if (config.Ssh != null)
            {
                var keyPath = config.Ssh.ResolvedKeyPath;
                if (File.Exists(keyPath))
                {
                    try
                    {
                        sshService = new SshService(config.Ssh, reportMate);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to initialize SSH service");
                    }
                }
            }

            // Create Azure DevOps service if configured
            AzureDevOpsService? adoService = null;
            if (!string.IsNullOrEmpty(config.AzureDevOps?.Organization) &&
                !string.IsNullOrEmpty(config.AzureDevOps?.Project))
            {
                adoService = new AzureDevOpsService(config.AzureDevOps);
            }

            // Create Microsoft Graph service if configured
            GraphService? graphService = null;
            if (config.Graph?.UseAzureCliAuth == true)
            {
                graphService = new GraphService(config.Graph);
            }

            // Create TeamDynamix service if configured
            TdxService? tdxService = null;
            if (config.Tdx != null && config.Tdx.AppId > 0)
            {
                tdxService = new TdxService(config.Tdx);
            }

            // Build command tree
            var rootCommand = new RootCommand("FleetMate - Fleet orchestration, inventory, deployment monitoring, and troubleshooting")
            {
                Name = "fleetmate"
            };
            
            // Add global options
            var verboseOption = new Option<bool>(
                aliases: new[] { "-v", "--verbose" },
                description: "Enable verbose output");
            rootCommand.AddGlobalOption(verboseOption);
            
            var jsonOption = new Option<bool>(
                aliases: new[] { "--json" },
                description: "Output in JSON format");
            rootCommand.AddGlobalOption(jsonOption);
            
            // Fleet monitoring commands (from aautil)
            rootCommand.AddCommand(ErrorsCommand.Create(reportMate, pkgInfoService));
            rootCommand.AddCommand(TroubleshootCommand.Create(reportMate, pkgInfoService, config));
            rootCommand.AddCommand(DeviceCommand.Create(reportMate));
            
            // Local QA commands (will wrap PowerShell)
            rootCommand.AddCommand(TestCommand.Create(config));
            rootCommand.AddCommand(LintCommand.Create(config, pkgInfoService));
            rootCommand.AddCommand(ValidateCommand.Create(config, pkgInfoService));
            
            // Utility commands
            rootCommand.AddCommand(StatusCommand.Create(config, reportMate));
            
            // Snipe-IT asset management
            rootCommand.AddCommand(SnipeCommand.Create(snipeService));

            // SSH remote execution
            rootCommand.AddCommand(SshCommand.Create(sshService, reportMate));

            // Azure DevOps integration
            rootCommand.AddCommand(DevOpsCommand.Create(adoService, reportMate));

            // Intune device management
            rootCommand.AddCommand(IntuneCommand.Create(graphService, reportMate));
            // Entra ID (Azure AD) user/group management
            rootCommand.AddCommand(EntraCommand.Create(graphService, reportMate));

            // TeamDynamix (Ticketing)
            rootCommand.AddCommand(TdxCommand.Create(tdxService, reportMate));

            var result = await rootCommand.InvokeAsync(args);
            
            // Dispose services
            snipeService?.Dispose();
            sshService?.Dispose();
            adoService?.Dispose();
            graphService?.Dispose();
            tdxService?.Dispose();

            return result;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "FleetMate crashed");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Fatal error: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }
}
