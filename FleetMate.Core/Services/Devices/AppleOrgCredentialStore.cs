using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Devices;

namespace FleetMate.Core.Services.Devices;

/// <summary>
/// Apple School and Business Manager API profiles, kept in Windows Credential
/// Manager as generic credentials for the signed-in user — never in policy,
/// config files or the registry. One credential per profile, its target
/// prefixed so the profile list is an enumeration of them.
/// </summary>
public sealed class AppleOrgCredentialStore
{
    public const string TargetPrefix = "FleetMate:AppleOrg:";

    /// <summary>The secret half of a profile.</summary>
    public sealed record Secret(string ClientId, string KeyId, string PrivateKeyPem);

    public List<AppleOrgProfile> Profiles()
    {
        var found = new List<AppleOrgProfile>();
        if (!CredEnumerate(TargetPrefix + "*", 0, out var count, out var list)) return found;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var cred = Marshal.PtrToStructure<CREDENTIAL>(Marshal.ReadIntPtr(list, i * IntPtr.Size));
                var name = cred.TargetName[TargetPrefix.Length..];
                if (ReadBlob(cred) is { } secret) found.Add(new AppleOrgProfile(name, secret.ClientId));
            }
        }
        finally { CredFree(list); }

        // Two profiles holding the same credential are one organization;
        // reading both would count every device twice.
        return found.GroupBy(p => p.ClientId).Select(g => g.First())
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Secret? Load(string profileName)
    {
        if (!CredRead(TargetPrefix + profileName, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try { return ReadBlob(Marshal.PtrToStructure<CREDENTIAL>(ptr)); }
        finally { CredFree(ptr); }
    }

    /// <summary>Store a profile. The PEM text is kept; the key file itself is not.</summary>
    public void Save(string profileName, string clientId, string keyId, string privateKeyPem)
    {
        if (string.IsNullOrWhiteSpace(profileName)) throw new ArgumentException("A profile needs a name.");
        if (!privateKeyPem.Contains("PRIVATE KEY")) throw new ArgumentException("That file is not a PEM private key.");

        var blob = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            new Secret(clientId.Trim(), keyId.Trim(), privateKeyPem.Trim())));
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = TargetPrefix + profileName.Trim(),
                CredentialBlobSize = blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = clientId.Trim(),
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { handle.Free(); }
    }

    public void Delete(string profileName) => CredDelete(TargetPrefix + profileName, CRED_TYPE_GENERIC, 0);

    private static Secret? ReadBlob(CREDENTIAL cred)
    {
        if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return null;
        var bytes = new byte[cred.CredentialBlobSize];
        Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
        try { return JsonSerializer.Deserialize<Secret>(bytes); }
        catch (JsonException) { return null; }
    }

    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
