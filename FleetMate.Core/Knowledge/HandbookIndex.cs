using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FleetMate.Core.Knowledge;

/// <summary>One Handbook page, read from FleetMate's own copy of the Handbook repository.</summary>
public sealed record HandbookPage(
    string Path,
    string Title,
    IReadOnlyList<string> Sections,
    string SitePath,
    IReadOnlyList<string> Headings,
    string Body,
    string? LastModified,
    string? LastModifiedBy)
{
    /// <summary>
    /// True when <see cref="Body"/> is only the catalog's summary and keywords;
    /// the full text is read from FleetMate's copy when the page is opened.
    /// </summary>
    public bool IsSummary { get; init; }

    /// <summary>Section folders as a reader would say them: "Devices › Enrollment".</summary>
    public string Breadcrumb => string.Join(" › ", Sections.Select(Humanize));

    internal static string Humanize(string slug) =>
        string.Join(" ", slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}

/// <summary>A thing on screen described to the Handbook: which property, and its value.</summary>
public sealed record HandbookFacet(string Label, string Term);

/// <summary>Pages that matched one facet, for a card that groups them.</summary>
public sealed record HandbookFacetGroup(string Label, IReadOnlyList<HandbookPage> Pages);

/// <summary>
/// Every Handbook page, searchable. Built from the Hugo content folder of
/// FleetMate's own always-current copy of the Handbook (macOS parity).
/// </summary>
public sealed partial class HandbookIndex
{
    public static readonly HandbookIndex Empty = new(Array.Empty<HandbookPage>());

    public IReadOnlyList<HandbookPage> Pages { get; }
    private readonly (string Title, string Headings, string Path, string Body)[] _lowered;

    public HandbookIndex(IReadOnlyList<HandbookPage> pages)
    {
        Pages = pages;
        _lowered = pages.Select(p => (p.Title.ToLowerInvariant(), string.Join("\n", p.Headings).ToLowerInvariant(),
            p.Path.ToLowerInvariant(), p.Body.ToLowerInvariant())).ToArray();
    }

    /// <summary>Folders never indexed: retired systems, kept on the site as history (macOS parity).</summary>
    public static readonly IReadOnlySet<string> SkippedSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "legacy" };

    private static bool IsSkipped(string relativePath) =>
        SkippedSections.Contains(relativePath.Split('/', 2)[0]);

    /// <summary>Read every <c>.md</c> page under <paramref name="contentRoot"/>, except the skipped sections.</summary>
    public static HandbookIndex Load(string contentRoot)
    {
        if (!Directory.Exists(contentRoot)) return Empty;
        var pages = new List<HandbookPage>();
        foreach (var file in Directory.EnumerateFiles(contentRoot, "*.md", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(contentRoot, file).Replace('\\', '/');
            if (IsSkipped(relative)) continue;
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            if (Page(relative, text) is { } page) pages.Add(page);
        }
        return new HandbookIndex(pages.OrderBy(p => p.Path, StringComparer.Ordinal).ToList());
    }

    /// <summary>
    /// Read the pipeline-built catalog (<c>website/data/catalog.json</c>): one
    /// small file instead of every page. Bodies are left out; the reader loads a
    /// page's text from disk when it opens. Null when there is no usable catalog,
    /// so the caller falls back to <see cref="Load"/>.
    /// </summary>
    public static HandbookIndex? LoadCatalog(string catalogPath)
    {
        CatalogFile? catalog;
        try
        {
            if (!File.Exists(catalogPath)) return null;
            catalog = JsonSerializer.Deserialize<CatalogFile>(File.ReadAllText(catalogPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
        if (catalog?.Pages is not { Count: > 0 } entries) return null;
        var pages = entries
            .Where(p => !string.IsNullOrEmpty(p.Path) && !string.IsNullOrEmpty(p.Title) && !IsSkipped(p.Path))
            .Select(p => new HandbookPage(
                p.Path!, p.Title!,
                (p.Section ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries),
                p.Url ?? "/",
                p.Headings ?? new List<string>(),
                // Searchable text until the page is opened.
                string.Join("\n", new[] { p.Summary ?? "" }.Concat(p.Keywords ?? new List<string>())),
                string.IsNullOrEmpty(p.LastModified) ? null : p.LastModified,
                string.IsNullOrEmpty(p.LastModifiedBy) ? null : p.LastModifiedBy)
            { IsSummary = true })
            .ToList();
        return new HandbookIndex(pages);
    }

    /// <summary>The full page, parsed from its Markdown file; null when it is not on disk.</summary>
    public static HandbookPage? FullPage(string relativePath, string contentRoot)
    {
        var root = System.IO.Path.GetFullPath(contentRoot);
        var file = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        // A catalog path never leaves the content folder.
        if (!file.StartsWith(root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return null;
        try { return File.Exists(file) ? Page(relativePath, File.ReadAllText(file)) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>
    /// Pages for a typed query: every word must appear somewhere; titles count
    /// most, then headings, path and body (macOS parity).
    /// </summary>
    public IReadOnlyList<HandbookPage> Search(string query, int limit = 8)
    {
        var words = (query ?? "").ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2).ToList();
        if (words.Count == 0) return Array.Empty<HandbookPage>();
        var scored = new List<(int Index, int Score)>();
        for (var i = 0; i < _lowered.Length; i++)
        {
            var l = _lowered[i];
            var total = 0;
            var all = true;
            foreach (var word in words)
            {
                var score = (l.Title.Contains(word) ? 12 : 0) + (l.Headings.Contains(word) ? 5 : 0)
                            + (l.Path.Contains(word) ? 3 : 0) + (l.Body.Contains(word) ? 1 : 0);
                if (score == 0) { all = false; break; }
                total += score;
            }
            if (all) scored.Add((i, total));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index)
            .Take(limit).Select(x => Pages[x.Index]).ToList();
    }

    private sealed class CatalogFile
    {
        [JsonPropertyName("pages")] public List<CatalogPage>? Pages { get; set; }
    }

    private sealed class CatalogPage
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("section")] public string? Section { get; set; }
        [JsonPropertyName("headings")] public List<string>? Headings { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }
        [JsonPropertyName("lastmod")] public string? LastModified { get; set; }
        [JsonPropertyName("lastmod_by")] public string? LastModifiedBy { get; set; }
    }

    /// <summary>One page from its path under the content folder and its text; null for a title-less stub.</summary>
    public static HandbookPage? Page(string relativePath, string text)
    {
        var (fields, rawBody) = FrontMatter.Split(text);
        var components = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (components.Count == 0) return null;
        var file = components[^1];
        components.RemoveAt(components.Count - 1);
        var isIndex = file is "_index.md" or "index.md";

        // Separators and other title-less stubs are navigation, not pages.
        if (!fields.TryGetValue("title", out var title) || string.IsNullOrWhiteSpace(title)) return null;

        var slug = fields.TryGetValue("slug", out var s) ? s : file[..^3];
        var parts = isIndex ? components : components.Append(slug).ToList();
        var sitePath = "/" + string.Join("/", parts) + (components.Count == 0 && isIndex ? "" : "/");
        var body = Shortcode().Replace(rawBody, "");
        var headings = body.Split('\n')
            .Where(l => l.StartsWith('#'))
            .Select(l => l.TrimStart('#').Trim())
            .ToList();

        return new HandbookPage(relativePath, title, components, sitePath, headings, body,
            fields.GetValueOrDefault("lastmod_date") ?? fields.GetValueOrDefault("lastmod") ?? fields.GetValueOrDefault("date"),
            fields.GetValueOrDefault("lastmod_by"));
    }

    /// <summary>
    /// Pages about a thing, given the words that describe it. Any term may
    /// match, but a page needs a title, heading or path hit to count, so a
    /// passing mention in a long page does not surface it. Pages touching
    /// more of the terms rank above pages that repeat one.
    /// </summary>
    public IReadOnlyList<HandbookPage> Related(IEnumerable<string> terms, int limit = 5)
    {
        var wanted = Wanted(terms);
        if (wanted.Count == 0) return Array.Empty<HandbookPage>();

        var scored = new List<(int Index, int Score)>();
        for (var i = 0; i < _lowered.Length; i++)
        {
            var l = _lowered[i];
            int strong = 0, weak = 0, distinct = 0;
            foreach (var term in wanted)
            {
                var hit = Strength(l, term);
                if (hit > 0) distinct++;
                strong += hit;
                if (l.Body.Contains(term)) weak++;
            }
            if (strong > 0) scored.Add((i, distinct * 100 + strong + weak));
        }
        return scored.OrderByDescending(x => x.Score).ThenBy(x => x.Index)
            .Take(limit).Select(x => Pages[x.Index]).ToList();
    }

    /// <summary>
    /// The related pages, grouped under the first facet (in the order given)
    /// that each page matches by title, heading or path. Ranking and the limit
    /// are those of <see cref="Related"/>.
    /// </summary>
    public IReadOnlyList<HandbookFacetGroup> RelatedByFacet(IReadOnlyList<HandbookFacet> facets, int limit = 5)
    {
        var pages = Related(facets.Select(f => f.Term), limit);
        var groups = facets.Select(f => (f.Label, Pages: new List<HandbookPage>())).ToList();
        foreach (var page in pages)
        {
            var index = Pages.ToList().IndexOf(page);
            var l = _lowered[index];
            for (var i = 0; i < facets.Count; i++)
            {
                var term = Normalize(facets[i].Term);
                if (term.Length >= 3 && Strength(l, term) > 0)
                {
                    groups[i].Pages.Add(page);
                    break;
                }
            }
        }
        // A label used twice (two facets with one label) shows once.
        return groups.Where(g => g.Pages.Count > 0)
            .GroupBy(g => g.Label)
            .Select(g => new HandbookFacetGroup(g.Key, g.SelectMany(x => x.Pages).Distinct().ToList()))
            .ToList();
    }

    private static int Strength((string Title, string Headings, string Path, string Body) l, string term) =>
        (l.Title.Contains(term) ? 10 : 0) + (l.Headings.Contains(term) ? 4 : 0) + (l.Path.Contains(term) ? 3 : 0);

    private static HashSet<string> Wanted(IEnumerable<string> terms) =>
        terms.Select(Normalize).Where(t => t.Length >= 3).ToHashSet();

    private static string Normalize(string? term) => (term ?? "").Trim().ToLowerInvariant();

    [GeneratedRegex(@"\{\{[<%].*?[%>]\}\}", RegexOptions.Singleline)]
    private static partial Regex Shortcode();
}

/// <summary>The words that describe an asset to the Handbook.</summary>
public static class HandbookAssetFacets
{
    /// <summary>
    /// Model family, category, platform, management service and fleet, in that
    /// order. The family drops the parenthesised generation:
    /// "Laptop Pro (14-inch, 2023)" becomes "Laptop Pro".
    /// </summary>
    public static IReadOnlyList<HandbookFacet> For(string? model, string? category, string? platform,
        string? managementService, string? fleet)
    {
        var family = (model ?? "").Split('(')[0].Trim();
        return new[]
            {
                new HandbookFacet("Model family", family),
                new HandbookFacet("Category", category ?? ""),
                new HandbookFacet("Platform", platform ?? ""),
                new HandbookFacet("Management service", managementService ?? ""),
                new HandbookFacet("Fleet", fleet ?? ""),
            }
            .Where(f => f.Term.Trim().Length >= 3)
            .ToList();
    }
}

/// <summary>Where a page is published, when the site address is configured.</summary>
public static class HandbookSite
{
    public static Uri? PageUrl(string? siteUrl, HandbookPage page)
    {
        if (string.IsNullOrWhiteSpace(siteUrl) || !Uri.TryCreate(siteUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
            return null;
        if (root.Scheme != Uri.UriSchemeHttps && root.Scheme != Uri.UriSchemeHttp) return null;
        return new Uri(root, page.SitePath.TrimStart('/'));
    }
}
