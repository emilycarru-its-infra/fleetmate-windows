using System.Windows;
using FleetMate.Config;
using FleetMate.GUI.Views;
using FleetMate.Models;
using FleetMate.Services;
using Serilog;

namespace FleetMate.GUI;

public partial class App : Application
{
    public FleetMateConfig Config { get; private set; } = null!;
    public GraphService? GraphService { get; private set; }
    public SnipeService? SnipeService { get; private set; }
    public TdxService? TdxService { get; private set; }
    
    // MARK: - TDX SSO State
    public bool IsTdxSsoAuthenticated => TdxService?.IsSsoAuthenticated ?? false;
    public string? TdxAuthenticatedUserName => TdxService?.AuthenticatedUserName;
    
    // MARK: - Cached Data
    // Data caches with timestamps to avoid reloading on tab switches
    
    public List<IntuneDevice> CachedDevices { get; set; } = new();
    public List<SnipeAsset> CachedAssets { get; set; } = new();
    public List<TdxTicket> CachedTickets { get; set; } = new();
    public List<EntraUser> CachedUsers { get; set; } = new();
    public List<EntraGroup> CachedGroups { get; set; } = new();
    
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
                
                Log.Information("TDX SSO authentication successful for {UserName}", result.UserName);
                onComplete?.Invoke(true);
            }
            else
            {
                Log.Warning("TDX SSO authentication failed: {Error}", result.Error);
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
        Log.Information("Signed out of TDX SSO");
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

        // Initialize services
        InitializeServices();

        var mainWindow = new MainWindow();
        mainWindow.Show();
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
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize services");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        GraphService?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
