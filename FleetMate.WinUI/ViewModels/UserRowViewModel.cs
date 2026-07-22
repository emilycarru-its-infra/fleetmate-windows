using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using FleetMate.Core.Models.Identity;

namespace FleetMate.WinUI.ViewModels;

/// <summary>Row display model for the Identity → Users list (Entra users).</summary>
public sealed class UserRowViewModel
{
    public EntraUser User { get; }

    public UserRowViewModel(EntraUser user) => User = user;

    public string Name => string.IsNullOrEmpty(User.DisplayName) ? "(user)" : User.DisplayName;
    public string Upn => User.UserPrincipalName;
    public string JobTitle => User.JobTitle ?? User.Department ?? "—";
    public string StatusLabel => User.AccountEnabled == false ? "Disabled" : "Enabled";
    public Brush StatusBrush => new SolidColorBrush(User.AccountEnabled == false ? Colors.Red : Colors.Green);

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || Upn.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (User.Mail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (User.Department?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
}
