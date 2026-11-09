namespace FleetMate.Core.Models.Projects;

/// <summary>A provider-qualified repository identity, independent of its display label.</summary>
public sealed record RepositoryFilterScope(PullRequestSource Source, string Container, string? Repository = null)
{
    public string Label => string.IsNullOrEmpty(Repository) ? Container : $"{Container}/{Repository}";
    public string Key => $"repository/{(Source == PullRequestSource.GitHub ? "github" : "devops")}/{Encode(Container)}/{Encode(Repository ?? "")}";
    public bool Matches(string? selection, bool allowRepositoryOnly = false) => selection == null
        || string.Equals(selection, Key, StringComparison.OrdinalIgnoreCase)
        || string.Equals(selection, Label, StringComparison.OrdinalIgnoreCase)
        || allowRepositoryOnly && string.Equals(selection, Repository, StringComparison.OrdinalIgnoreCase);

    private static string Encode(string value) => Uri.EscapeDataString(value.ToLowerInvariant());

    public static RepositoryFilterScope? FromKey(string? key)
    {
        var parts = key?.Split('/');
        if (parts is not { Length: 4 } || parts[0] != "repository") return null;
        var source = parts[1] switch { "github" => PullRequestSource.GitHub, "devops" => PullRequestSource.AzureDevOps, _ => (PullRequestSource?)null };
        if (source == null) return null;
        try { return new(source.Value, Uri.UnescapeDataString(parts[2]), Uri.UnescapeDataString(parts[3]) is { Length: > 0 } repo ? repo : null); }
        catch (UriFormatException) { return null; }
    }
}

public sealed record RepositoryFilterEntry(RepositoryFilterScope Scope, int Count);

public static class RepositoryFilterHierarchy
{
    public static List<RepositoryFilterEntry> Count<T>(IEnumerable<T> items, Func<T, RepositoryFilterScope> scope, Func<T, int>? weight = null) => items
        .GroupBy(item => scope(item).Key)
        .Select(group => new RepositoryFilterEntry(scope(group.First()), group.Sum(item => weight?.Invoke(item) ?? 1)))
        .OrderBy(entry => entry.Scope.Source)
        .ThenBy(entry => entry.Scope.Container, StringComparer.OrdinalIgnoreCase)
        .ThenBy(entry => entry.Scope.Repository, StringComparer.OrdinalIgnoreCase).ToList();
}
