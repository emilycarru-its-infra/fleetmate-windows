using FleetMate.Core.Models.Identity;

namespace FleetMate.WinUI.ViewModels;

/// <summary>Row display model for the Identity → Groups list (Entra groups).</summary>
public sealed class GroupRowViewModel
{
    public EntraGroup Group { get; }

    public GroupRowViewModel(EntraGroup group) => Group = group;

    public string Name => string.IsNullOrEmpty(Group.DisplayName) ? "(group)" : Group.DisplayName;
    public string Members => Group.MemberCount?.ToString() ?? "";

    public string TypeLabel
    {
        get
        {
            var parts = new List<string>();
            if (Group.GroupTypes.Any(t => t.Equals("Unified", StringComparison.OrdinalIgnoreCase))) parts.Add("M365");
            else if (Group.SecurityEnabled == true) parts.Add("Security");
            if (!string.IsNullOrEmpty(Group.MembershipRule)) parts.Add("Dynamic");
            return parts.Count > 0 ? string.Join(" · ", parts) : "—";
        }
    }

    public bool Matches(string q) =>
        Name.Contains(q, StringComparison.OrdinalIgnoreCase)
        || (Group.Mail?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
        || (Group.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
}
