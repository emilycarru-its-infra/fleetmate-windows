using FleetMate.Core.Models;
using FleetMate.Core.Models.Manage;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>Copies a local file to a machine. SSH implements it with SFTP.</summary>
public interface IRemoteFileCopier
{
    Task<SecureShellResult> UploadAsync(string ip, string localPath, string remotePath, CancellationToken cancellationToken,
        string? username = null, string? deviceName = null);
}

/// <summary>
/// "Install Package…" in Manage (macOS parity): the package is copied to
/// each machine and installed silently with the Windows installer for its
/// kind (the Mac copies a .pkg and runs installer -pkg as root). The copy
/// is removed afterwards whether the install worked or not.
/// </summary>
public static class PackageInstall
{
    /// <summary>The package kinds Windows installs unattended, for the file picker.</summary>
    public static readonly IReadOnlyList<string> Extensions = new[] { ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle" };

    public const string PickerFilter = "Windows packages (*.msi;*.msix;*.msixbundle;*.appx;*.appxbundle)|*.msi;*.msix;*.msixbundle;*.appx;*.appxbundle";

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the copy lands: the system temp folder, under a unique name.</summary>
    public static string RemotePath(string localPath, Guid id) =>
        $@"C:\Windows\Temp\FleetMate-{id:N}{Path.GetExtension(localPath).ToLowerInvariant()}";

    /// <summary>The same path as SFTP on Windows OpenSSH spells it: /C:/Windows/Temp/….</summary>
    public static string SftpPath(string remotePath) => "/" + remotePath.Replace('\\', '/');

    /// <summary>
    /// The PowerShell that installs the copied package and removes it. An MSI
    /// runs through msiexec with no UI; 3010 (restart required) counts as
    /// success and says so. MSIX and APPX are provisioned for every user.
    /// </summary>
    public static string Script(string remotePath, string packageName)
    {
        var path = PsQuote(remotePath);
        var name = PsQuote(packageName);
        var install = Path.GetExtension(remotePath).ToLowerInvariant() switch
        {
            ".msi" => $$"""
                $p = Start-Process -FilePath msiexec.exe -ArgumentList @('/i', {{path}}, '/qn', '/norestart') -Wait -PassThru
                $code = $p.ExitCode
                if ($code -eq 3010) { Write-Output ("Installed " + {{name}} + " (restart required)"); $code = 0 }
                elseif ($code -eq 0) { Write-Output ("Installed " + {{name}}) }
                else { Write-Error ("msiexec exited " + $code) }
                """,
            _ => $$"""
                Add-AppxProvisionedPackage -Online -PackagePath {{path}} -SkipLicense | Out-Null
                Write-Output ("Installed " + {{name}})
                $code = 0
                """,
        };
        return $$"""
            $ErrorActionPreference = 'Stop'
            $code = 1
            try {
            {{install}}
            } catch {
                Write-Error $_
                $code = 1
            } finally {
                Remove-Item -LiteralPath {{path}} -Force -ErrorAction SilentlyContinue
            }
            exit $code
            """;
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}

public partial class CommandRunner
{
    /// <summary>
    /// Copy <paramref name="localPath"/> to each target and install it,
    /// streaming each machine's progress like any other run.
    /// </summary>
    public async Task InstallPackageAsync(IReadOnlyList<RunTarget> targets, string localPath, IRemoteFileCopier copier,
        IRunObserver observer, CancellationToken cancellationToken)
    {
        if (targets.Count == 0) return;
        var packageName = Path.GetFileName(localPath);
        using var gate = new SemaphoreSlim(Math.Max(1, Concurrency));

        var tasks = targets.Select(async target =>
        {
            var serial = target.Computer.Serial;
            try { await gate.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                observer.Finished(serial, CommandRunStatus.Cancelled, null, "", null);
                return;
            }

            try
            {
                observer.Started(serial);
                var remotePath = PackageInstall.RemotePath(localPath, Guid.NewGuid());
                observer.Output(serial, $"Copying {packageName} to {target.Computer.DisplayName}…\n");
                var copy = await copier.UploadAsync(target.Ip, localPath, remotePath, cancellationToken,
                    deviceName: target.Computer.DisplayName);
                if (copy.Outcome != SecureShellOutcome.Success)
                {
                    observer.Finished(serial, MapStatus(copy.Outcome), copy.ExitCode, copy.Stderr,
                        copy.ErrorMessage ?? "The package could not be copied.");
                    return;
                }

                observer.Output(serial, $"Installing {packageName}…\n");
                var result = await _runner.RunAsync(target.Ip, Wrap(PackageInstall.Script(remotePath, packageName)),
                    chunk => observer.Output(serial, chunk), cancellationToken, deviceName: target.Computer.DisplayName);
                observer.Finished(serial, MapStatus(result.Outcome), result.ExitCode, result.Stderr, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Package install on {Host} threw", target.Ip);
                observer.Finished(serial, cancellationToken.IsCancellationRequested ? CommandRunStatus.Cancelled : CommandRunStatus.Failed, null, "", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
    }
}
