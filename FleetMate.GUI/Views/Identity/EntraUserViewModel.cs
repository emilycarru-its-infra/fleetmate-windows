using System.Windows;
using System.Windows.Media;
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
                ? "SystemControlForegroundBaseLowBrush"
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
    public string AccountBadgeText => User.AccountEnabled switch
    {
        true => "● Active",
        false => "● Disabled",
        null => "● Unknown",
    };

    public Brush AccountBadgeBrush => User.AccountEnabled switch
    {
        true => new SolidColorBrush(Color.FromRgb(0x2D, 0xA4, 0x4E)),
        false => new SolidColorBrush(Color.FromRgb(0xD1, 0x3A, 0x3A)),
        null => Brushes.Gray,
    };

    /// <summary>Up to two letters for the avatar bubble.</summary>
    public string Initials
    {
        get
        {
            var letters = DisplayName
                .Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                .Take(2)
                .Select(part => part[0])
                .ToArray();

            return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
        }
    }

    /// <summary>Subtitle under the name: job title and department, whichever exist.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new[] { User.JobTitle, User.Department }
                .Where(p => !string.IsNullOrWhiteSpace(p));

            return string.Join(" · ", parts);
        }
    }

    public List<UserPropertySection> Sections =>
    [
        new()
        {
            Title = "Identity",
            Rows =
            [
                new() { Label = "Display name", Value = User.DisplayName },
                new() { Label = "User principal name", Value = User.UserPrincipalName },
                new() { Label = "Object ID", Value = User.Id },
                new() { Label = "Given name", Value = User.GivenName },
                new() { Label = "Surname", Value = User.Surname },
            ],
        },
        new()
        {
            Title = "Job information",
            Rows =
            [
                new() { Label = "Job title", Value = User.JobTitle },
                new() { Label = "Department", Value = User.Department },
                new() { Label = "Company", Value = User.CompanyName },
                new() { Label = "Employee ID", Value = User.EmployeeId },
                new() { Label = "Employee type", Value = User.EmployeeType },
                new() { Label = "Office", Value = User.OfficeLocation },
            ],
        },
        new()
        {
            Title = "Contact information",
            Rows =
            [
                new() { Label = "Mail", Value = User.Mail },
                new() { Label = "Mobile", Value = User.MobilePhone },
            ],
        },
        new()
        {
            Title = "Settings",
            Rows =
            [
                new() { Label = "Account enabled", Value = User.AccountEnabled?.ToString() },
                new() { Label = "Usage location", Value = User.UsageLocation },
                new() { Label = "Created", Value = User.CreatedDateTime?.ToString("yyyy-MM-dd") },
            ],
        },
        new()
        {
            Title = "On-premises",
            Rows =
            [
                new() { Label = "SAM account name", Value = User.OnPremisesSamAccountName },
                new() { Label = "Distinguished name", Value = User.OnPremisesDistinguishedName },
                new() { Label = "Sync enabled", Value = User.OnPremisesSyncEnabled?.ToString() },
            ],
        },
    ];
}
