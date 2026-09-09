using System.Security.Cryptography;
using System.Text;
using Serilog;

namespace FleetMate.Core.Services.Manage;

/// <summary>
/// Keeps the Remote Desktop password for the fleet admin account encrypted
/// with DPAPI for the current Windows user, in the Manage state folder. It
/// is only ever handed to cmdkey for the target being opened; it is never
/// written to the registry, a log, or a connection file.
/// </summary>
public class RdpCredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FleetMate.Manage.Rdp.v1");

    public string Path { get; }

    public RdpCredentialStore(string? root = null)
    {
        Path = System.IO.Path.Combine(root ?? ManageStateStore.DefaultRoot, "rdp-credential.bin");
    }

    public bool HasCredential => File.Exists(Path);

    public void Save(string password)
    {
        if (string.IsNullOrEmpty(password)) { Delete(); return; }
        var dir = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(Path, protectedBytes);
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var raw = ProtectedData.Unprotect(File.ReadAllBytes(Path), Entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(raw);
            return value.Length == 0 ? null : value;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Stored Remote Desktop credential could not be read; treating as unset");
            return null;
        }
    }

    public void Delete()
    {
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch (Exception ex) { Log.Warning(ex, "Could not delete the stored Remote Desktop credential"); }
    }
}
