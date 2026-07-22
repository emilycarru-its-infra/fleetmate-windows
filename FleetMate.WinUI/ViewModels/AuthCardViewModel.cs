using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models;

namespace FleetMate.WinUI.ViewModels;

/// <summary>
/// Display model for one connected system's auth status. Shared by the Dashboard
/// and Settings pages so the auth-card rendering lives in one place.
/// </summary>
public sealed class AuthCardViewModel
{
    public string Name { get; init; } = "";
    public string Icon { get; init; } = "";
    public string Category { get; init; } = "";
    public string StatusLabel { get; init; } = "";
    public Brush StatusBrush { get; init; } = new SolidColorBrush(Colors.Gray);
    public string User { get; init; } = "";
    public Visibility UserVisibility => string.IsNullOrEmpty(User) ? Visibility.Collapsed : Visibility.Visible;

    public static AuthCardViewModel From(AuthSystemStatus s) => new()
    {
        Name = s.SystemId.DisplayName(),
        Icon = s.SystemId.Icon(),
        Category = s.SystemId.Category().DisplayName(),
        StatusLabel = s.State.StatusLabel,
        StatusBrush = new SolidColorBrush(ColorFromName(s.State.StatusColor)),
        User = s.State.User ?? s.User ?? "",
    };

    private static Color ColorFromName(string name) => name switch
    {
        "Green" => Colors.Green,
        "Gold" => Colors.Gold,
        "DodgerBlue" => Colors.DodgerBlue,
        "Orange" => Colors.Orange,
        "Red" => Colors.Red,
        _ => Colors.Gray,
    };
}
