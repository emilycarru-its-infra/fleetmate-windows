using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Config;
using Microsoft.Win32;

namespace FleetMate.GUI.Views.Development.Repos;

/// <summary>Property-change plumbing for the Repos models.</summary>
public abstract class RepoObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// The Repos workspace's remembered choices — panel, sidebar sort, grouping,
/// filter, collapsed groups, Insights period — beside FleetMate's other
/// per-user preferences in the registry.
/// </summary>
internal static class RepoWorkspacePreferences
{
    /// <summary>Tests point this at a throwaway key so they never touch the person's own choices.</summary>
    internal static string? KeyPathOverride { get; set; }

    private static string KeyPath => KeyPathOverride ?? AppEdition.Current.UserRegistryPath;

    public static string? Read(string name)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue("repos." + name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static void Write(string name, string? value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (value == null) key.DeleteValue("repos." + name, throwOnMissingValue: false);
            else key.SetValue("repos." + name, value);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A preference that cannot be stored is simply not remembered.
        }
    }

    public static T ReadEnum<T>(string name, T fallback) where T : struct, Enum =>
        Enum.TryParse<T>(Read(name), out var value) ? value : fallback;
}

/// <summary>Explorer, clipboard and path display for the Repos views.</summary>
internal static class RepoShell
{
    /// <summary>Opens Explorer with <paramref name="path"/> selected, or the folder itself.</summary>
    public static void Reveal(string path)
    {
        try
        {
            var start = File.Exists(path)
                ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + path } }
                : new ProcessStartInfo("explorer.exe") { ArgumentList = { path } };
            start.UseShellExecute = false;
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    public static void Copy(string text)
    {
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    /// <summary><c>C:\Users\me\Developer\x</c> → <c>~\Developer\x</c>.</summary>
    public static string Abbreviate(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return path.StartsWith(home + "\\", StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    public static string Relative(DateTimeOffset? date)
    {
        if (date is not { } d) return "—";
        var span = DateTimeOffset.Now - d;
        if (span.TotalSeconds < 60) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} d ago";
        return d.ToLocalTime().ToString("d MMM yyyy");
    }
}

/// <summary>
/// The Repos palette. Nothing destructive or removed is red: deletions and
/// warnings are orange, as in FleetMate for Mac.
/// </summary>
internal static class RepoBrushes
{
    public static readonly Brush Added = Frozen(0x1A, 0x9E, 0x8F);
    public static readonly Brush Removed = Frozen(0xE0, 0x7A, 0x1F);
    public static readonly Brush Warning = Frozen(0xE0, 0x7A, 0x1F);
    public static readonly Brush Success = Frozen(0x2D, 0xA4, 0x4E);
    public static readonly Brush Other = Frozen(0x8E, 0x8E, 0x93);

    /// <summary>Series colours in a fixed order, never cycled into red.</summary>
    public static readonly Brush[] Series =
    {
        Frozen(0x00, 0x78, 0xD4), Frozen(0xE0, 0x7A, 0x1F), Frozen(0x1A, 0x9E, 0x8F), Frozen(0x88, 0x5A, 0xC8),
        Frozen(0x9C, 0x6B, 0x3E), Frozen(0x3C, 0xB3, 0x71), Frozen(0x4B, 0x5F, 0xC7),
    };

    /// <summary>The app's own accent, which bars and badges fill with.</summary>
    public static Brush Accent =>
        Application.Current?.TryFindResource("FleetMateAccentBrush") as Brush ?? Series[0];

    public static Brush Secondary =>
        Application.Current?.TryFindResource("SystemControlForegroundBaseMediumBrush") as Brush ?? Brushes.Gray;

    public static Brush Text =>
        Application.Current?.TryFindResource("SystemControlForegroundBaseHighBrush") as Brush ?? Brushes.Black;

    public static Brush Subtle =>
        Application.Current?.TryFindResource("SubtleFillBrush") as Brush ?? Brushes.LightGray;

    public static Brush Lane(int index) => Series[((index % Series.Length) + Series.Length) % Series.Length];

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
