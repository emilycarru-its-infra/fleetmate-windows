using FleetMate.Core.Models.Identity;

namespace FleetMate.Core.Identity;

/// <summary>One label and value in a user's profile; an empty value shows as a dash.</summary>
public sealed record UserPropertyRow(string Label, string? Value)
{
    public bool HasValue => !string.IsNullOrWhiteSpace(Value);
    public string DisplayValue => HasValue ? Value! : "—";
}

/// <summary>A titled group of profile rows; shown only when one of them has a value.</summary>
public sealed record UserPropertySection(string Title, IReadOnlyList<UserPropertyRow> Rows)
{
    public bool HasValues => Rows.Any(r => r.HasValue);
}

/// <summary>Whether an account can sign in, as the user inspector badges it.</summary>
public enum AccountState { Active, Disabled, Unknown }

/// <summary>
/// How the Identity tab presents an Entra user, the same in both apps: the
/// initials avatar, the job subtitle, the account badge and the property sections.
/// </summary>
public static class EntraUserProfile
{
    public static string Initials(string? displayName)
    {
        var letters = (displayName ?? "")
            .Split(new[] { ' ', '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Take(2)
            .Select(part => part[0])
            .ToArray();
        return letters.Length == 0 ? "?" : new string(letters).ToUpperInvariant();
    }

    public static string Subtitle(EntraUser user) =>
        string.Join(" · ", new[] { user.JobTitle, user.Department }.Where(p => !string.IsNullOrWhiteSpace(p)));

    public static AccountState State(EntraUser user) => user.AccountEnabled switch
    {
        true => AccountState.Active,
        false => AccountState.Disabled,
        null => AccountState.Unknown,
    };

    public static string StateText(AccountState state) => state switch
    {
        AccountState.Active => "Active",
        AccountState.Disabled => "Disabled",
        _ => "Unknown",
    };

    /// <summary>The account action the inspector offers: enable a disabled account, otherwise disable.</summary>
    public static string ToggleLabel(EntraUser user) => user.AccountEnabled == false ? "Enable Account" : "Disable Account";

    public static IReadOnlyList<UserPropertySection> Sections(EntraUser user) => new[]
    {
        new UserPropertySection("Identity", new UserPropertyRow[]
        {
            new("Display name", user.DisplayName),
            new("User principal name", user.UserPrincipalName),
            new("Object ID", user.Id),
            new("Given name", user.GivenName),
            new("Surname", user.Surname),
        }),
        new UserPropertySection("Job information", new UserPropertyRow[]
        {
            new("Job title", user.JobTitle),
            new("Department", user.Department),
            new("Company", user.CompanyName),
            new("Employee ID", user.EmployeeId),
            new("Employee type", user.EmployeeType),
            new("Office", user.OfficeLocation),
        }),
        new UserPropertySection("Contact information", new UserPropertyRow[]
        {
            new("Mail", user.Mail),
            new("Mobile", user.MobilePhone),
        }),
        new UserPropertySection("Settings", new UserPropertyRow[]
        {
            new("Account enabled", user.AccountEnabled?.ToString()),
            new("Usage location", user.UsageLocation),
            new("Created", user.CreatedDateTime?.ToString("yyyy-MM-dd")),
        }),
        new UserPropertySection("On-premises", new UserPropertyRow[]
        {
            new("SAM account name", user.OnPremisesSamAccountName),
            new("Distinguished name", user.OnPremisesDistinguishedName),
            new("Sync enabled", user.OnPremisesSyncEnabled?.ToString()),
        }),
    };
}
