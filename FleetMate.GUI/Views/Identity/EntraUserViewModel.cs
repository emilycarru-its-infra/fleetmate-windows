using System.Windows;
using System.Windows.Media;
using FleetMate.Core.Identity;
using FleetMate.Core.Models.Identity;

namespace FleetMate.GUI.Views.Identity;

/// <summary>One labelled field in the inspector's property list.</summary>
public sealed class UserPropertyRow
{
    public required string Label { get; init; }
    public string? Value { get; init; }

    /// <summary>
    /// Empty fields render as an em dash rather than blank, so a row that has no
    /// value is visibly "not set" instead of looking like a layout gap.
    /// </summary>
    public string DisplayValue => string.IsNullOrWhiteSpace(Value) ? "—" : Value!;

    /// <summary>
    /// Dim an unset value so the eye skips it.
    ///
    /// Resolved defensively: <see cref="Application.Current"/> is null outside a
    /// running app (tests, designer), and a property getter that throws there
    /// would take the whole pane down rather than degrade.
    /// </summary>
    public Brush ValueBrush
    {
        get
        {
            var key = string.IsNullOrWhiteSpace(Value)
                ? "SystemControlForegroundBaseMediumBrush"
                : "SystemControlForegroundBaseHighBrush";

            return Application.Current?.TryFindResource(key) as Brush
                ?? (string.IsNullOrWhiteSpace(Value) ? Brushes.Gray : Brushes.Black);
        }
    }
}

/// <summary>A titled group of property rows, mirroring the Azure portal's sections.</summary>
public sealed class UserPropertySection
{
    public required string Title { get; init; }
    public required List<UserPropertyRow> Rows { get; init; }

    /// <summary>
    /// Hide a section whose fields are all empty. An estate that does not sync
    /// from on-premises should not see an On-premises panel full of dashes.
    /// </summary>
    public Visibility Visibility =>
        Rows.Any(r => !string.IsNullOrWhiteSpace(r.Value)) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// An Entra user as the inspector shows them.
///
/// The property grouping mirrors the Azure portal — Identity, Job information,
/// Contact information, Settings, On-premises — so someone who knows that page
/// can find the same field in the same place here.
/// </summary>
public sealed class EntraUserViewModel
{
    public required EntraUser User { get; init; }

    public string DisplayName => User.DisplayName;
    public string UserPrincipalName => User.UserPrincipalName;
    public string? Mail => User.Mail;
    public string? JobTitle => User.JobTitle;
    public string? Department => User.Department;

    /// <summary>
    /// The enabled badge.
    ///
    /// Three states, not two: Graph omits accountEnabled unless it is asked for,
    /// and null has to read as "unknown" rather than silently picking a side. A
    /// hardcoded "Active" hid exactly this — the badge was decoration, not data.
    /// </summary>
    public string AccountBadgeText => "● " + EntraUserProfile.StateText(EntraUserProfile.State(User));

    public Brush AccountBadgeBrush => User.AccountEnabled switch
    {
        true => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
        false => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        null => Brushes.Gray,
    };

    /// <summary>Up to two letters for the avatar bubble.</summary>
    /// <summary>The rules are <see cref="EntraUserProfile"/>'s, shared with the WinUI app.</summary>
    public string Initials => EntraUserProfile.Initials(DisplayName);

    public string Subtitle => EntraUserProfile.Subtitle(User);

    public List<UserPropertySection> Sections => EntraUserProfile.Sections(User)
        .Select(section => new UserPropertySection
        {
            Title = section.Title,
            Rows = section.Rows.Select(row => new UserPropertyRow { Label = row.Label, Value = row.Value }).ToList(),
        })
        .ToList();
}
