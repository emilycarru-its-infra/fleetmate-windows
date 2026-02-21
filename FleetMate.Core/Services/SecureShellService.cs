using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FleetMate.Core.Models.Reporting;
using FleetMate.Core.Services.Reporting;
using FleetMate.Core.Models;
using Renci.SshNet;
using Renci.SshNet.Common;
using Serilog;

namespace FleetMate.Core.Services;

/// <summary>
/// SecureShell service for remote command execution on fleet devices
/// Uses SSH.NET library with private key authentication
/// </summary>
public class SecureShellService : IDisposable
{
    private readonly SecureShellConfig _config;
    private readonly ReportMateService? _reportMate;
    private readonly PrivateKeyFile _privateKey;
    private readonly SemaphoreSlim _connectionThrottle;

    public SecureShellService(SecureShellConfig config, ReportMateService? reportMate = null)
    {
        _config = config;
        _reportMate = reportMate;

        // Try multiple key sources in order:
        // 1. Environment variable (SECURE_SHELL_PRIVATE_KEY from Key Vault)
        // 2. Key Vault (if configured)
        // 3. File path

        string? keyContent = config.GetPrivateKeyFromEnv();
        string keySource = "environment variable";

        if (string.IsNullOrEmpty(keyContent) && !string.IsNullOrEmpty(config.KeyVaultName))
        {
            keyContent = GetKeyFromKeyVault(config.KeyVaultName);
            keySource = $"Key Vault ({config.KeyVaultName})";
        }

        if (!string.IsNullOrEmpty(keyContent))
        {
            try
            {
                // Load key from content (handles OpenSSH format)
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(keyContent));
                _privateKey = new PrivateKeyFile(stream);
                Log.Information("Loaded SecureShell private key from {Source}", keySource);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load SecureShell private key from {Source}. Falling back to file.", keySource);
                _privateKey = LoadKeyFromFile();
            }
        }
        else
        {
            _privateKey = LoadKeyFromFile();
        }

        _connectionThrottle = new SemaphoreSlim(_config.MaxConcurrentConnections);
    }

    /// <summary>
    /// Attempt to retrieve SecureShell key from Azure Key Vault using az CLI
    /// </summary>
    private static string? GetKeyFromKeyVault(string vaultName)
    {
        try
        {
            var azPath = FindAzureCli();
            if (azPath == null) return null;

            var psi = new ProcessStartInfo
            {
                FileName = azPath,
                Arguments = $"keyvault secret show --vault-name {vaultName} --name SecureShellPrivateKey --query value -o tsv",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            {
                Log.Debug("Retrieved SecureShell key from Key Vault {Vault}", vaultName);
                return output.Trim();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to get SecureShell key from Key Vault");
        }
        return null;
    }

    private PrivateKeyFile LoadKeyFromFile()
    {
        var keyPath = _config.ResolvedKeyPath;
        if (!File.Exists(keyPath))
        {
            throw new FileNotFoundException(
                $"SecureShell private key not found. Set SECURE_SHELL_PRIVATE_KEY environment variable, " +
                $"configure secureShell.keyVaultName, or ensure key exists at: {keyPath}");
        }

        try
        {
            Log.Information("Loaded SecureShell private key from {Path}", keyPath);
            return new PrivateKeyFile(keyPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to load SecureShell private key from {keyPath}: {ex.Message}", ex);
        }
    }

    #region Host Key Management

    /// <summary>
    /// Remove a host's entry from the known_hosts file
    /// This is needed when a device is reimaged and its host key changes
    /// </summary>
    public bool RemoveHostKey(string host)
    {
        var knownHostsPath = _config.ResolvedKnownHostsPath;
        
        if (!File.Exists(knownHostsPath))
        {
            Log.Debug("known_hosts file not found at {Path}", knownHostsPath);
            return false;
        }

        try
        {
            var lines = File.ReadAllLines(knownHostsPath);
            var originalCount = lines.Length;
            
            // Match lines starting with the host (IP or hostname)
            // Format: "hostname/ip key-type base64-key" or "[hostname]:port key-type base64-key"
            var hostPattern = new Regex($@"^{Regex.Escape(host)}\s|^\[{Regex.Escape(host)}\]:\d+\s", RegexOptions.IgnoreCase);
            
            var filteredLines = lines.Where(line => !hostPattern.IsMatch(line)).ToArray();
            
            if (filteredLines.Length < originalCount)
            {
                // Create backup
                var backupPath = knownHostsPath + ".old";
                File.Copy(knownHostsPath, backupPath, overwrite: true);
                
                File.WriteAllLines(knownHostsPath, filteredLines);
                Log.Information("Removed stale host key for {Host} from known_hosts ({Removed} entries)", 
                    host, originalCount - filteredLines.Length);
                return true;
            }
            
            Log.Debug("No host key found for {Host} in known_hosts", host);
            return false;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to remove host key for {Host}", host);
            return false;
        }
    }

    /// <summary>
    /// Check if an exception indicates a host key verification failure
    /// </summary>
    private static bool IsHostKeyVerificationError(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        
        // Check common host key error patterns
        return message.Contains("host key") ||
               message.Contains("key exchange") ||
               message.Contains("identification has changed") ||
               message.Contains("hostkey") ||
               message.Contains("fingerprint") ||
               (ex is SshConnectionException && message.Contains("key"));
    }

    /// <summary>
    /// Configure host key handling on a connection
    /// </summary>
    private void ConfigureHostKeyHandling(ConnectionInfo connectionInfo, string host)
    {
        if (_config.AcceptAllHostKeys)
        {
            // Accept all host keys - useful for dynamic fleet environments
            connectionInfo.HostKeyAlgorithms.Clear();
            Log.Debug("Configured to accept all host keys for {Host}", host);
        }
    }

    /// <summary>
    /// Remove stale host key using ssh-keygen (native OpenSSH tool)
    /// This also works for the Windows OpenSSH client
    /// </summary>
    public bool RemoveHostKeyNative(string host)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh-keygen",
                Arguments = $"-R {host}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            process.WaitForExit(5000);
            
            if (process.ExitCode == 0)
            {
                Log.Information("Removed stale host key for {Host} using ssh-keygen", host);
                return true;
            }
            
            var stderr = process.StandardError.ReadToEnd();
            Log.Debug("ssh-keygen returned {ExitCode}: {Error}", process.ExitCode, stderr);
            return false;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to run ssh-keygen for {Host}", host);
            return false;
        }
    }

    /// <summary>
    /// Clean stale host keys for a host using both methods
    /// </summary>
    public bool CleanStaleHostKey(string host)
    {
        var result = RemoveHostKey(host);
        
        // Also try native method in case the file format is different
        if (RemoveHostKeyNative(host))
            result = true;
            
        return result;
    }

    #endregion

    /// <summary>
    /// Find Azure CLI executable path
    /// </summary>
    private static string? FindAzureCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft SDKs", "Azure", "CLI2", "wbin", "az.cmd"),
            @"C:\Program Files\Microsoft SDKs\Azure\CLI2\wbin\az.cmd",
            @"C:\Program Files (x86)\Microsoft SDKs\Azure\CLI2\wbin\az.cmd"
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate)) return candidate;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var azCmd = Path.Combine(dir, "az.cmd");
            if (File.Exists(azCmd)) return azCmd;
        }
        return null;
    }

    /// <summary>
    /// Resolve a device identifier to an IP address
    /// </summary>
    public async Task<(string ip, Device? device)> ResolveHostAsync(string hostOrDevice)
    {
        // If it looks like an IP address, return it directly
        if (System.Net.IPAddress.TryParse(hostOrDevice, out _))
        {
            // Try to find device info for this IP
            Device? device = null;
            if (_reportMate != null)
            {
                var devices = await _reportMate.GetDevicesAsync();
                device = devices.FirstOrDefault(d => d.IpAddress == hostOrDevice);
            }
            return (hostOrDevice, device);
        }

        // Try to resolve via ReportMate
        if (_reportMate != null)
        {
            var device = await _reportMate.FindDeviceAsync(hostOrDevice);
            if (device != null)
            {
                // Device found - try to get IP from device, or fetch network module
                if (!string.IsNullOrEmpty(device.IpAddress))
                {
                    Log.Debug("Resolved {Query} to IP {Ip} ({DeviceName})",
                        hostOrDevice, device.IpAddress, device.DisplayName);
                    return (device.IpAddress, device);
                }
                
                // Fetch network module to get IP address
                var networkInfo = await _reportMate.GetDeviceNetworkAsync(device.SerialNumber);
                if (networkInfo?.PrimaryIpv4 != null)
                {
                    Log.Debug("Resolved {Query} to IP {Ip} ({DeviceName}) via network module",
                        hostOrDevice, networkInfo.PrimaryIpv4, device.DisplayName);
                    return (networkInfo.PrimaryIpv4, device);
                }
                
                // Fall back to hostname if available
                if (!string.IsNullOrEmpty(device.Hostname))
                {
                    Log.Debug("Resolved {Query} to hostname {Hostname} ({DeviceName})",
                        hostOrDevice, device.Hostname, device.DisplayName);
                    return (device.Hostname, device);
                }
            }
        }

        // Assume it's a hostname that can be resolved by DNS
        Log.Debug("Using {Host} as hostname (no IP resolution)", hostOrDevice);
        return (hostOrDevice, null);
    }

    /// <summary>
    /// Execute a command on a single host
    /// </summary>
    public async Task<SecureShellResult> ExecuteAsync(string hostOrDevice, string command, string? username = null)
    {
        return await ExecuteWithRetryAsync(hostOrDevice, command, username, retryOnHostKey: true);
    }

    /// <summary>
    /// Execute a command with optional host key retry logic
    /// </summary>
    private async Task<SecureShellResult> ExecuteWithRetryAsync(
        string hostOrDevice, 
        string command, 
        string? username, 
        bool retryOnHostKey)
    {
        var result = new SecureShellResult
        {
            Command = command,
            Username = username ?? _config.DefaultUsername,
            StartedAt = DateTime.UtcNow
        };

        var sw = Stopwatch.StartNew();

        try
        {
            var (ip, device) = await ResolveHostAsync(hostOrDevice);
            result.Host = ip;
            result.DeviceName = device?.DisplayName;

            try
            {
                await ExecuteOnClientAsync(result, ip, command);
            }
            catch (Exception ex) when (_config.AutoCleanStaleHostKeys && retryOnHostKey && IsHostKeyVerificationError(ex))
            {
                // Host key verification failed - likely a reimaged device
                Log.Information("Host key verification failed for {Host}, cleaning stale key and retrying...", ip);
                
                if (CleanStaleHostKey(ip))
                {
                    // Retry once after cleaning the stale key
                    result.Error = null;
                    result.Connected = false;
                    
                    await ExecuteOnClientAsync(result, ip, command);
                    Log.Information("Successfully connected to {Host} after cleaning stale host key", ip);
                }
                else
                {
                    // Couldn't clean the key, re-throw
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex;
            Log.Warning(ex, "SecureShell command failed on {Host}", result.Host);
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <summary>
    /// Execute command on an SSH client
    /// </summary>
    private async Task ExecuteOnClientAsync(SecureShellResult result, string ip, string command)
    {
        using var client = new SshClient(
            ip,
            _config.Port,
            result.Username,
            _privateKey);

        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds);
        
        // Configure host key handling if needed
        if (_config.AcceptAllHostKeys)
        {
            // Accept any host key by not validating it
            client.HostKeyReceived += (sender, e) => { e.CanTrust = true; };
        }

        await Task.Run(() => client.Connect());
        result.Connected = true;

        Log.Debug("Connected to {Host} as {User}", ip, result.Username);

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(_config.CommandTimeoutSeconds);

        var output = await Task.Run(() => cmd.Execute());

        result.ExitCode = cmd.ExitStatus ?? -1;
        result.Stdout = output ?? string.Empty;
        result.Stderr = cmd.Error ?? string.Empty;

        Log.Debug("Command on {Host} completed with exit code {ExitCode}", ip, result.ExitCode);
    }

    /// <summary>
    /// Execute a command on multiple hosts in parallel
    /// </summary>
    public async Task<SecureShellBatchResult> ExecuteBatchAsync(
        IEnumerable<string> hostsOrDevices,
        string command,
        string? username = null,
        bool stopOnError = false)
    {
        var batchResult = new SecureShellBatchResult();
        var sw = Stopwatch.StartNew();
        var hosts = hostsOrDevices.ToList();

        Log.Information("Starting batch execution on {Count} hosts", hosts.Count);

        var tasks = new List<Task>();
        var results = new System.Collections.Concurrent.ConcurrentBag<SecureShellResult>();

        using var cts = new CancellationTokenSource();

        foreach (var host in hosts)
        {
            var task = Task.Run(async () =>
            {
                await _connectionThrottle.WaitAsync(cts.Token);
                try
                {
                    if (cts.Token.IsCancellationRequested) return;

                    var result = await ExecuteAsync(host, command, username);
                    results.Add(result);

                    if (stopOnError && !result.Success)
                    {
                        Log.Warning("Stopping batch execution due to error on {Host}", host);
                        cts.Cancel();
                    }
                }
                finally
                {
                    _connectionThrottle.Release();
                }
            }, cts.Token);

            tasks.Add(task);
        }

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            Log.Information("Batch execution was cancelled");
        }

        sw.Stop();
        batchResult.Results = results.ToList();
        batchResult.TotalDuration = sw.Elapsed;

        Log.Information("Batch execution completed: {Success}/{Total} succeeded in {Duration:F2}s",
            batchResult.SuccessCount, batchResult.TotalCount, batchResult.TotalDuration.TotalSeconds);

        return batchResult;
    }

    /// <summary>
    /// Test SecureShell connectivity to a host
    /// </summary>
    public async Task<SecureShellTestResult> TestConnectionAsync(string hostOrDevice, string? username = null)
    {
        return await TestConnectionWithRetryAsync(hostOrDevice, username, retryOnHostKey: true);
    }

    /// <summary>
    /// Test connection with optional host key retry
    /// </summary>
    private async Task<SecureShellTestResult> TestConnectionWithRetryAsync(
        string hostOrDevice, 
        string? username, 
        bool retryOnHostKey)
    {
        var result = new SecureShellTestResult
        {
            Username = username ?? _config.DefaultUsername
        };

        var sw = Stopwatch.StartNew();

        try
        {
            var (ip, device) = await ResolveHostAsync(hostOrDevice);
            result.Host = ip;
            result.DeviceName = device?.DisplayName;

            try
            {
                await TestClientConnectionAsync(result, ip);
            }
            catch (Exception ex) when (_config.AutoCleanStaleHostKeys && retryOnHostKey && IsHostKeyVerificationError(ex))
            {
                Log.Information("Host key verification failed for {Host}, cleaning stale key and retrying...", ip);
                
                if (CleanStaleHostKey(ip))
                {
                    await TestClientConnectionAsync(result, ip);
                    Log.Information("Successfully connected to {Host} after cleaning stale host key", ip);
                }
                else
                {
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessage = ex.Message;
            Log.Warning("SecureShell test to {Host} failed: {Error}", result.Host, ex.Message);
        }
        finally
        {
            sw.Stop();
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <summary>
    /// Perform the actual connection test
    /// </summary>
    private async Task TestClientConnectionAsync(SecureShellTestResult result, string ip)
    {
        using var client = new SshClient(
            ip,
            _config.Port,
            result.Username,
            _privateKey);

        client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds);
        
        if (_config.AcceptAllHostKeys)
        {
            client.HostKeyReceived += (sender, e) => { e.CanTrust = true; };
        }

        await Task.Run(() => client.Connect());

        result.Success = client.IsConnected;
        result.ServerVersion = client.ConnectionInfo.ServerVersion;

        client.Disconnect();

        Log.Information("SecureShell test to {Host} ({Device}): Success - {Version}",
            ip, result.DeviceName ?? "unknown", result.ServerVersion);
    }

    /// <summary>
    /// Get Cimian logs from a remote device
    /// </summary>
    public async Task<SecureShellResult> GetLogsAsync(string hostOrDevice, int tailLines = 50, bool errorsOnly = false, string? username = null)
    {
        var logPath = @"C:\ProgramData\ManagedInstalls\Logs\ManagedSoftwareUpdate.log";
        var command = errorsOnly
            ? $"powershell -Command \"Get-Content '{logPath}' -Tail {tailLines} | Select-String -Pattern 'ERROR|WARN'\""
            : $"powershell -Command \"Get-Content '{logPath}' -Tail {tailLines}\"";

        return await ExecuteAsync(hostOrDevice, command, username);
    }

    public void Dispose()
    {
        _privateKey.Dispose();
        _connectionThrottle.Dispose();
    }
}
