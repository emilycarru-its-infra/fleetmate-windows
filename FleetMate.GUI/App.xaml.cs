using System.IO;
using System.Windows;
using FleetMate.Core.Config;
using FleetMate.GUI.Views;
using FleetMate.GUI.Views.Devices;
using FleetMate.GUI.Views.Inventory;
using FleetMate.GUI.Views.Tickets;
using FleetMate.GUI.Views.Projects;
using FleetMate.GUI.Views.Identity;
using FleetMate.GUI.Views.Shared;
using FleetMate.Core.Models;
using FleetMate.Core.Models.Devices;
using FleetMate.Core.Models.Identity;
using FleetMate.Core.Models.Inventory;
using FleetMate.Core.Models.Tickets;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services;
using FleetMate.Core.Services.Devices;
using FleetMate.Core.Services.Inventory;
using FleetMate.Core.Services.Tickets;
using FleetMate.Core.Services.Projects;
using FleetMate.Core.Services.Reporting;
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
    public DevOpsSsoService? DevOpsSsoService { get; private set; }
    public ReportMateService? ReportMateService { get; private set; }
    
    // MARK: - TDX SSO State
    public bool IsTdxSsoAuthenticated => TdxService?.IsSsoAuthenticated ?? false;
    public string? TdxAuthenticatedUserName => TdxService?.AuthenticatedUserName;
    
    // MARK: - DevOps SSO State
    public bool IsDevOpsSsoAuthenticated => DevOpsService?.IsSsoAuthenticated ?? false;
    public string? DevOpsAuthenticatedUserName => DevOpsService?.SsoUserName;
    public bool DevOpsProjectReady { get; private set; }
    private bool _hasAutoPromptedDevOpsSso;
    
    // MARK: - Deep Navigation
    public string? PendingNavigateDeviceId { get; set; }
    public int? PendingNavigateTicketId { get; set; }
    
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
    /// Attempt silent TDX SSO authentication (Phase 1 → Phase 1.5 → Phase 2 fallback).
    /// Called automatically on startup when TDX is configured with SSO.
    /// </summary>
    public async Task AttemptSilentTdxSsoAsync()
    {
        if (Config.Tdx == null || string.IsNullOrEmpty(Config.Tdx.BaseUrl) || !Config.Tdx.SsoEnabled)
        {
            Log.Debug("[tdx-sso] TDX SSO not configured or not enabled");
            return;
        }
        
        if (IsTdxSsoAuthenticated)
        {
            Log.Debug("[tdx-sso] Already authenticated, skipping silent SSO");
            return;
        }
        
        var baseUrl = Config.Tdx.BaseUrl;
        
        // Phase 1: Silent HttpClient SSO (Negotiate/Kerberos)
        AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Authenticating());
        Log.Information("[tdx-sso] Starting silent SSO sequence for {BaseUrl}", baseUrl);
        
        var result = await Task.Run(() => TdxSsoLoginWindow.TryPhase1SilentAsync(baseUrl));
        
        if (result is { Success: true, Token: not null })
        {
            HandleSilentSsoSuccess(result);
            return;
        }
        
        // Phase 1.5: Headless WebView2 SSO (must run on UI thread)
        Log.Information("[tdx-sso] Phase 1 failed — trying headless WebView2 (Phase 1.5)");
        result = await TdxSsoLoginWindow.TryPhase15HeadlessAsync(baseUrl);
        
        if (result is { Success: true, Token: not null })
        {
            HandleSilentSsoSuccess(result);
            return;
        }
        
        // Phase 2: Fall back to interactive window (user will see a login prompt)
        Log.Information("[tdx-sso] Phase 1.5 failed — falling back to interactive login (Phase 2)");
        ShowTdxSsoLogin();
    }
    
    /// <summary>
    /// Handle a successful silent SSO result (from Phase 1 or 1.5).
    /// </summary>
    private void HandleSilentSsoSuccess(TdxSsoResult result)
    {
        TdxService?.SetSsoToken(result.Token!, result.Expiry, result.UserEmail, result.UserName);
        _ticketsCacheTime = null;
        AuthManager.Update(AuthSystemId.Tdx, AuthTokenState.Valid(result.UserName, result.Expiry));
        Log.Information("[tdx-sso] ✓ Silent SSO successful — user={UserName}", result.UserName ?? "(unknown)");
    }
    
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
    /// Phase 1: Attempt silent SSO (az CLI → MSAL cache → refresh token).
    /// No UI shown. If it fails, falls through to Phase 1.5 headless WebView2.
    /// </summary>
    public async Task AttemptSilentDevOpsSsoAsync()
    {
        if (DevOpsSsoService == null || DevOpsService == null)
        {
            Log.Debug("[devops-sso] DevOps not configured, skipping silent SSO");
            return;
        }
        
        if (IsDevOpsSsoAuthenticated)
        {
            Log.Debug("[devops-sso] Already authenticated, skipping silent SSO");
            return;
        }
        
        Log.Information("[devops-sso] Phase 1: Starting silent token acquisition (az CLI → MSAL cache)");
        AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Authenticating());
        
        try
        {
            var result = await DevOpsSsoService.RefreshAccessTokenAsync();
            
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                Log.Information("[devops-sso] Phase 1: Silent token acquired — user={UserName}", result.UserName ?? "(unknown)");
                HandleDevOpsSsoSuccess(result);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Phase 1: Token acquisition failed");
        }
        
        Log.Information("[devops-sso] Phase 1 failed — trying headless WebView2 (Phase 1.5)");
        await AttemptHeadlessDevOpsSsoAsync();
    }
    
    /// <summary>
    /// Phase 1.5: Attempt SSO using a hidden WebView2 window.
    /// Enterprise SSO / WAM can intercept WebView2 requests to login.microsoftonline.com
    /// and handle auth silently (Kerberos/Windows Hello).
    /// </summary>
    private async Task AttemptHeadlessDevOpsSsoAsync()
    {
        if (DevOpsSsoService == null)
            return;
        
        Log.Information("[devops-sso] Phase 1.5: Starting headless WebView2 SSO attempt");
        
        DevOpsSsoResult? capturedResult = null;
        var tcs = new TaskCompletionSource<bool>();
        
        // Create a hidden window with a DevOpsSsoLoginWindow
        var ssoWindow = new DevOpsSsoLoginWindow(DevOpsSsoService)
        {
            WindowState = WindowState.Minimized,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -9999,
            Top = -9999
        };
        
        ssoWindow.AuthenticationCompleted += (_, result) =>
        {
            capturedResult = result;
            tcs.TrySetResult(true);
        };
        
        ssoWindow.AuthenticationCancelled += (_, _) =>
        {
            tcs.TrySetResult(false);
        };
        
        ssoWindow.Show();
        
        // Wait up to 15 seconds for headless auth to complete
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
        var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
        
        // Close the hidden window
        if (ssoWindow.IsLoaded)
        {
            try { ssoWindow.Close(); } catch { /* window may already be closed */ }
        }
        
        if (completedTask == tcs.Task && capturedResult is { Success: true, Token: not null })
        {
            Log.Information("[devops-sso] Phase 1.5: Headless SSO SUCCEEDED — user={UserName}", capturedResult.UserName ?? "(unknown)");
            HandleDevOpsSsoSuccess(capturedResult);
            return;
        }
        
        Log.Information("[devops-sso] Phase 1.5 failed or timed out — falling back to interactive login (Phase 2)");
        
        // Only auto-prompt once; subsequent attempts require user action
        if (!_hasAutoPromptedDevOpsSso)
        {
            _hasAutoPromptedDevOpsSso = true;
            ShowDevOpsSsoLogin();
        }
    }
    
    /// <summary>
    /// Handle successful DevOps SSO authentication from any phase.
    /// </summary>
    private void HandleDevOpsSsoSuccess(DevOpsSsoResult result)
    {
        DevOpsService?.SetSsoToken(result.Token!, result.Expiry, result.UserName);
        AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Valid(result.UserName, result.Expiry));
        
        // Auto-discover a default project (for sprints/boards, which are project-scoped)
        _ = DiscoverDevOpsProjectAsync();
    }
    
    /// <summary>
    /// Discover a default project for sprints/boards context.
    /// Work items use org-level WIQL and don't need this.
    /// </summary>
    private async Task DiscoverDevOpsProjectAsync()
    {
        if (DevOpsService == null) return;
        
        try
        {
            Log.Information("[devops-sso] Running project discovery (for sprints/boards context)");
            var discovered = await DevOpsService.DiscoverProjectAsync();
            Log.Information("[devops-sso] Default project: {Project}", discovered ?? "none");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Project discovery failed (non-fatal)");
        }
        
        DevOpsProjectReady = true;
    }

    /// <summary>
    /// Show DevOps SSO login window (Phase 2: interactive OAuth2 PKCE) and handle result.
    /// </summary>
    public void ShowDevOpsSsoLogin(Action<bool>? onComplete = null)
    {
        if (DevOpsSsoService == null)
        {
            Log.Warning("Cannot show DevOps SSO login - DevOpsSsoService not configured");
            onComplete?.Invoke(false);
            return;
        }

        var ssoWindow = new DevOpsSsoLoginWindow(DevOpsSsoService)
        {
            Owner = Current.MainWindow
        };

        ssoWindow.AuthenticationCompleted += (_, result) =>
        {
            if (result.Success && !string.IsNullOrEmpty(result.Token))
            {
                HandleDevOpsSsoSuccess(result);
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
        DevOpsSsoService?.ClearTokens();
        DevOpsService?.ClearSsoToken();
        DevOpsProjectReady = false;
        AuthManager.Update(AuthSystemId.DevOps, AuthTokenState.Configured());
        Log.Information("Signed out of DevOps SSO");
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Configure Serilog
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".fleetmate");
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "debug.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Load configuration
        Config = FleetMateConfig.Load();

        // Initialize auth manager
        AuthManager = new AuthManager(Config);

        // Initialize services
        InitializeServices();

        var mainWindow = new MainWindow();
        mainWindow.Show();

        // Attempt silent TDX SSO, then preload all data in the background
        _ = InitializeAndPreloadAsync();
    }

    /// <summary>
    /// Run silent SSO then preload all data.
    /// Silent SSO must complete first so that services have valid tokens
    /// before data preloading starts.
    /// </summary>
    private async Task InitializeAndPreloadAsync()
    {
        // Try silent TDX SSO first (Phase 1 → 1.5 → 2)
        try
        {
            await AttemptSilentTdxSsoAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[tdx-sso] Silent SSO sequence failed");
        }
        
        // Try silent DevOps SSO (Phase 1 → 1.5 → 2)
        try
        {
            await AttemptSilentDevOpsSsoAsync();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[devops-sso] Silent SSO sequence failed");
        }
        
        // Now preload all data (tickets/work items will use SSO tokens if obtained)
        await PreloadAllDataAsync();
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

        if (DevOpsService != null && DevOpsService.HasValidToken)
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
                
                // Initialize DevOpsSsoService if OAuth2 credentials are configured
                if (!string.IsNullOrEmpty(Config.AzureDevOps.ClientId) && !string.IsNullOrEmpty(Config.AzureDevOps.TenantId))
                {
                    DevOpsSsoService = new DevOpsSsoService(Config.AzureDevOps.ClientId, Config.AzureDevOps.TenantId);
                    Log.Information("DevOpsSsoService initialized");
                }
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
