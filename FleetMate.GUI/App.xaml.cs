using System.Windows;
using FleetMate.Config;
using FleetMate.GUI.Views;
using FleetMate.Models;
using FleetMate.Models.Graph;
using FleetMate.Models.Snipe;
using FleetMate.Models.Tdx;
using FleetMate.Models.AzureDevOps;
using FleetMate.Models.ReportMate;
using FleetMate.Services;
using Serilog;

namespace FleetMate.GUI;

public partial class App : Application
{
    public FleetMateConfig Config { get; private set; } = null!;
    public AuthManager AuthManager { get; private set; } = null!;
    public GraphService? GraphService { get; private set; }
    public SnipeService? SnipeService { get; private set; }
    public TdxService? TdxService { get; private set; }
    public AzureDevOpsService? DevOpsService { get; private set; }
    public ReportMateService? ReportMateService { get; private set; }
    
    // MARK: - TDX SSO State
    public bool IsTdxSsoAuthenticated => TdxService?.IsSsoAuthenticated ?? false;
    public string? TdxAuthenticatedUserName => TdxService?.AuthenticatedUserName;
    
    // MARK: - DevOps SSO State
    public bool IsDevOpsSsoAuthenticated => DevOpsService?.IsSsoAuthenticated ?? false;
    public string? DevOpsAuthenticatedUserName => DevOpsService?.SsoUserName;
    
    // MARK: - Cached Data
    // Data caches with timestamps to avoid reloading on tab switches
    
    public List<IntuneDevice> CachedDevices { get; set; } = new();
    public List<SnipeAsset> CachedAssets { get; set; } = new();
    public List<TdxTicket> CachedTickets { get; set; } = new();
    public List<EntraUser> CachedUsers { get; set; } = new();
    public List<EntraGroup> CachedGroups { get; set; } = new();
    public List<WorkItem> CachedWorkItems { get; set; } = new();
    public List<Sprint> CachedSprints { get; set; } = new();
    
    // Cache timestamps
    private DateTime? _devicesCacheTime;
    private DateTime? _assetsCacheTime;
    private DateTime? _ticketsCacheTime;
    private DateTime? _usersCacheTime;
    private DateTime? _groupsCacheTime;
    
    /// <summary>Cache duration from config</summary>
    private TimeSpan CacheDuration => TimeSpan.FromMinutes(Config?.CacheMinutes ?? 5);
    
    /// <summary>Check if devices cache is valid</summary>
    public bool IsDevicesCacheValid => _devicesCacheTime.HasValue && 
        DateTime.Now - _devicesCacheTime.Value < CacheDuration;
    
    /// <summary>Check if assets cache is valid</summary>
    public bool IsAssetsCacheValid => _assetsCacheTime.HasValue && 
        DateTime.Now - _assetsCacheTime.Value < CacheDuration;
    
    /// <summary>Check if tickets cache is valid</summary>
    public bool IsTicketsCacheValid => _ticketsCacheTime.HasValue && 
        DateTime.Now - _ticketsCacheTime.Value < CacheDuration;
    
    /// <summary>Check if users cache is valid</summary>
    public bool IsUsersCacheValid => _usersCacheTime.HasValue && 
        DateTime.Now - _usersCacheTime.Value < CacheDuration;
    
    /// <summary>Check if groups cache is valid</summary>
    public bool IsGroupsCacheValid => _groupsCacheTime.HasValue && 
        DateTime.Now - _groupsCacheTime.Value < CacheDuration;
    
    /// <summary>Update devices cache</summary>
    public void UpdateDevicesCache(List<IntuneDevice> devices)
    {
        CachedDevices = devices;
        _devicesCacheTime = DateTime.Now;
    }
    
    /// <summary>Update assets cache</summary>
    public void UpdateAssetsCache(List<SnipeAsset> assets)
    {
        CachedAssets = assets;
        _assetsCacheTime = DateTime.Now;
    }
    
    /// <summary>Update tickets cache</summary>
    public void UpdateTicketsCache(List<TdxTicket> tickets)
    {
        CachedTickets = tickets;
        _ticketsCacheTime = DateTime.Now;
    }
    
    /// <summary>Update users cache</summary>
    public void UpdateUsersCache(List<EntraUser> users)
    {
        CachedUsers = users;
        _usersCacheTime = DateTime.Now;
    }
    
    /// <summary>Update groups cache</summary>
    public void UpdateGroupsCache(List<EntraGroup> groups)
    {
        CachedGroups = groups;
        _groupsCacheTime = DateTime.Now;
    }
    
    /// <summary>Invalidate all caches</summary>
    public void InvalidateAllCaches()
    {
        _devicesCacheTime = null;
        _assetsCacheTime = null;
        _ticketsCacheTime = null;
        _usersCacheTime = null;
        _groupsCacheTime = null;
        CachedDevices.Clear();
        CachedAssets.Clear();
        CachedTickets.Clear();
        CachedUsers.Clear();
        CachedGroups.Clear();
    }
    
    /// <summary>
    /// Reload all data — called by Dashboard refresh button
    /// </summary>
    public Task ReloadAllDataAsync() => PreloadAllDataAsync();
    
    // MARK: - TDX SSO Authentication
    
    /// <summary>
    /// Show TDX SSO login window and handle result
    /// </summary>
    public void ShowTdxSsoLogin(Action<bool>? onComplete = null)
    {
        if (Config.Tdx == null || string.IsNullOrEmpty(Config.Tdx.BaseUrl))
        {
            Log.Warning("Cannot show TDX SSO login - TDX not configured");
            onComplete?.Invoke(false);
            return;
        }
        
        var ssoWindow = new TdxSsoLoginWindow(Config.Tdx.BaseUrl)
        {
            Owner = Current.MainWindow
        };
        
        ssoWindow.AuthenticationCompleted += (_, result) =>
        {
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                TdxService?.SetSsoToken(
                    result.Token,
                    result.Expiry,
                    result.UserEmail,
                    result.UserName
                );
                
                // Clear tickets cache to reload with new auth
                _ticketsCacheTime = null;
                
                AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Valid(result.UserName, result.Expiry));
                Log.Information("TDX SSO authentication successful for {UserName}", result.UserName);
                onComplete?.Invoke(true);
            }
            else
            {
                Log.Warning("TDX SSO authentication failed: {Error}", result.Error);
                if (result.Error != null)
                    AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Failed(result.Error));
                onComplete?.Invoke(false);
            }
        };
        
        ssoWindow.AuthenticationCancelled += (_, _) =>
        {
            Log.Debug("TDX SSO authentication cancelled");
            onComplete?.Invoke(false);
        };
        
        ssoWindow.ShowDialog();
    }
    
    /// <summary>
    /// Sign out of TDX SSO
    /// </summary>
    public void SignOutTdxSso()
    {
        TdxService?.ClearSsoToken();
        _ticketsCacheTime = null;
        CachedTickets.Clear();
        AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Configured());
        Log.Information("Signed out of TDX SSO");
    }

    // MARK: - DevOps SSO Authentication

    /// <summary>
    /// Show DevOps SSO login window (OAuth2 PKCE) and handle result.
    /// </summary>
    public void ShowDevOpsSsoLogin(Action<bool>? onComplete = null)
    {
        if (Config.AzureDevOps == null
            || string.IsNullOrEmpty(Config.AzureDevOps.ClientId)
            || string.IsNullOrEmpty(Config.AzureDevOps.TenantId))
        {
            Log.Warning("Cannot show DevOps SSO login - ClientId/TenantId not configured");
            onComplete?.Invoke(false);
            return;
        }

        var ssoWindow = new DevOpsSsoLoginWindow(Config.AzureDevOps.ClientId, Config.AzureDevOps.TenantId)
        {
            Owner = Current.MainWindow
        };

        ssoWindow.AuthenticationCompleted += (_, result) =>
        {
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                DevOpsService?.SetSsoToken(result.Token, result.Expiry, result.UserName);
                AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Valid(result.UserName, result.Expiry));
                Log.Information("DevOps SSO authentication successful for {UserName}", result.UserName);
                onComplete?.Invoke(true);
            }
            else
            {
                Log.Warning("DevOps SSO authentication failed: {Error}", result.Error);
                if (result.Error != null)
                    AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Failed(result.Error));
                onComplete?.Invoke(false);
            }
        };

        ssoWindow.AuthenticationCancelled += (_, _) =>
        {
            Log.Debug("DevOps SSO authentication cancelled");
            onComplete?.Invoke(false);
        };

        ssoWindow.ShowDialog();
    }

    /// <summary>
    /// Sign out of DevOps SSO.
    /// </summary>
    public void SignOutDevOpsSso()
    {
        DevOpsService?.ClearSsoToken();
        AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Configured());
        Log.Information("Signed out of DevOps SSO");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .CreateLogger();

        // Load configuration
        Config = FleetMateConfig.Load();

        // Initialize auth manager
        AuthManager = new AuthManager(Config);

        // Initialize services
        InitializeServices();

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // Background preloading of data
        _ = PreloadAllDataAsync();
    }

    /// <summary>
    /// Preload all data in the background on startup
    /// </summary>
    private async Task PreloadAllDataAsync()
    {
        var tasks = new List<Task>();

        if (GraphService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var devices = await GraphService.GetManagedDevicesAsync();
                    Dispatcher.Invoke(() => UpdateDevicesCache(devices));
                    Log.Information("Preloaded {Count} devices", devices.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload devices"); }
            }));

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var groups = await GraphService.SearchGroupsAsync("Devices-", 100);
                    Dispatcher.Invoke(() => UpdateGroupsCache(groups));
                    Log.Information("Preloaded {Count} groups", groups.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload groups"); }
            }));
        }

        if (SnipeService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var assets = await SnipeService.GetAssetsAsync();
                    Dispatcher.Invoke(() => UpdateAssetsCache(assets));
                    Log.Information("Preloaded {Count} assets", assets.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload assets"); }
            }));
        }

        if (TdxService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var tickets = await TdxService.SearchTicketsAsync(new TicketSearchRequest { MaxResults = 500 }, 500);
                    Dispatcher.Invoke(() => UpdateTicketsCache(tickets));
                    Log.Information("Preloaded {Count} tickets", tickets.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload tickets"); }
            }));
        }

        if (DevOpsService != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var items = await DevOpsService.GetWorkItemsAsync(limit: 200);
                    Dispatcher.Invoke(() => CachedWorkItems = items);
                    Log.Information("Preloaded {Count} work items", items.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload work items"); }
            }));

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var sprints = await DevOpsService.GetSprintsAsync();
                    Dispatcher.Invoke(() => CachedSprints = sprints);
                    Log.Information("Preloaded {Count} sprints", sprints.Count);
                }
                catch (Exception ex) { Log.Warning(ex, "Failed to preload sprints"); }
            }));
        }

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks);
            Log.Information("Background preloading complete");
        }

        // Probe auth status for all configured systems
        await AuthManager.ProbeAllAsync(GraphService, TdxService, SnipeService, DevOpsService);
    }

    private void InitializeServices()
    {
        try
        {
            // Initialize GraphService if configured
            if (Config.Graph != null && !string.IsNullOrEmpty(Config.Graph.TenantId))
            {
                GraphService = new GraphService(Config.Graph);
                Log.Information("GraphService initialized");
            }

            // Initialize SnipeService if configured
            if (!string.IsNullOrEmpty(Config.SnipeUrl) && !string.IsNullOrEmpty(Config.SnipeApiKey))
            {
                SnipeService = new SnipeService(Config.SnipeUrl, Config.SnipeApiKey, Config.CacheMinutes);
                Log.Information("SnipeService initialized");
            }

            // Initialize TdxService if configured
            if (Config.Tdx != null && !string.IsNullOrEmpty(Config.Tdx.BaseUrl))
            {
                TdxService = new TdxService(Config.Tdx);
                Log.Information("TdxService initialized");
            }

            // Initialize AzureDevOpsService if configured
            if (Config.AzureDevOps != null && !string.IsNullOrEmpty(Config.AzureDevOps.Organization))
            {
                DevOpsService = new AzureDevOpsService(Config.AzureDevOps);
                Log.Information("AzureDevOpsService initialized");
            }

            // Initialize ReportMateService if configured
            if (!string.IsNullOrEmpty(Config.ReportMateUrl))
            {
                ReportMateService = new ReportMateService(Config.ReportMateUrl, Config.ReportMatePassphrase, Config.CacheMinutes);
                Log.Information("ReportMateService initialized");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize services");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GraphService?.Dispose();
        DevOpsService?.Dispose();
        ReportMateService?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
