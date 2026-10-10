using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace FleetMate.Core.Services.Terminal;

/// <summary>
/// Files only their owner can read: the agent brief, the session briefs and
/// the context file describe the person's work and stay theirs. Each folder
/// and file gets a protected ACL with one entry, full control for the current
/// user, so nothing is inherited from the profile above it (which also lets
/// SYSTEM and Administrators in).
/// </summary>
public static class PrivateFile
{
    private static SecurityIdentifier Owner => WindowsIdentity.GetCurrent().User!;

    /// <summary>Create <paramref name="directory"/> if needed and restrict it, and what is created inside it, to the current user.</summary>
    public static void EnsureDirectory(string directory)
    {
        var info = new DirectoryInfo(directory);
        if (!info.Exists) info.Create();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Owner);
        security.AddAccessRule(new FileSystemAccessRule(Owner, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    /// <summary>
    /// Write <paramref name="bytes"/> as an owner-only file, by way of a
    /// temporary file moved into place, so a reader never sees half a file.
    /// Only the file is restricted: a folder that already exists keeps its
    /// own ACL (FleetMate's agent folder is made owner-only by
    /// <see cref="EnsureDirectory"/>, never a folder it merely writes into).
    /// </summary>
    public static void Write(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        Restrict(temp);
        File.Move(temp, path, overwrite: true);
    }

    public static void Write(string path, string text) => Write(path, new UTF8Encoding(false).GetBytes(text));

    /// <summary>Give <paramref name="path"/> the owner-only ACL itself, whatever its folder allows.</summary>
    public static void Restrict(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(Owner);
        security.AddAccessRule(new FileSystemAccessRule(Owner, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    /// <summary>True when only the current user has any access to <paramref name="path"/> (a file or a folder).</summary>
    public static bool IsOwnerOnly(string path)
    {
        FileSystemSecurity security = Directory.Exists(path)
            ? new DirectoryInfo(path).GetAccessControl()
            : new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        return rules.Count > 0 && rules.All(r => r.IdentityReference.Equals(Owner));
    }
}
