using System.CommandLine;
using System.CommandLine.Invocation;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FleetMate.Core.Models.Projects;
using FleetMate.Core.Services.Projects;

namespace FleetMate.Commands.Projects;

internal static class DevOpsResourceCommand
{
    private static readonly string[] PullRequestActions = ["pr complete", "pr approve", "pr set-vote", "pr comment"];

    private static readonly Dictionary<string, int> Votes = new(StringComparer.Ordinal)
    {
        ["approve"] = 10,
        ["approve-with-suggestions"] = 5,
        ["wait-for-author"] = -5,
        ["reject"] = -10,
        ["reset"] = 0,
    };

    private sealed record Route(string Name, string Method, string Path, string Version, string Scope);

    private static Route[] Routes(string group) =>
        DevOpsOperationCatalog.Operations.Split('\n')
            .Select(line => line.TrimEnd('\r').Split('|'))
            .Where(fields => fields[0] == group)
            .Select(fields => new Route(fields[1], fields[2], fields[3], fields[4],
                fields.Length > 5 ? fields[5] : "project"))
            .ToArray();

    internal static Command Create(string group, AzureDevOpsService? service)
    {
        var routes = Routes(group);
        var names = routes.Select(route => route.Name)
            .Concat(group == "repos" ? PullRequestActions : [])
            .Order(StringComparer.Ordinal);
        var description = group == "repos" ? "Manage Azure Repos and pull requests" : "Manage Azure Pipelines, builds and releases";
        var command = new Command(group, description + "\nOperations:\n  " +
            string.Join("\n  ", names.Append("api")) +
            "\nUse --body or --body-file for a request payload and --query NAME=VALUE for filters.");
        var operation = new Argument<string[]>("operation", "Operation, for example 'pr create', 'runs list' or 'api' (see help)")
        {
            Arity = ArgumentArity.OneOrMore
        };
        command.AddArgument(operation);

        var projectOption = AddString(command, "--project", "Azure DevOps project name or ID (defaults to the configured project)");
        var repositoryOption = AddString(command, "--repository", "Repository name or ID");
        var idOption = new Option<int?>("--id", "Primary resource ID");
        command.AddOption(idOption);
        var resourceOption = AddString(command, "--resource", "Nested resource ID or name, such as a reviewer ID, run ID or tag");
        var sourceOption = AddString(command, "--source-branch", "Source branch name or refs/heads reference (PR creation)");
        var targetOption = AddString(command, "--target-branch", "Target branch name or refs/heads reference (PR creation)");
        var titleOption = AddString(command, "--title", "Title of the pull request");
        var descriptionOption = AddString(command, "--description", "Description of the pull request");
        var draftOption = AddFlag(command, "--draft", "Create a draft pull request");
        var voteOption = AddString(command, "--vote", "approve, approve-with-suggestions, wait-for-author, reject or reset");
        var textOption = AddString(command, "--text", "Pull request comment text");
        var bodyOption = AddString(command, "--body", "JSON request body");
        var fileOption = AddString(command, "--body-file", "Path to a file containing the JSON request body");
        var queryOption = new Option<string[]>("--query", "Add a query parameter as NAME=VALUE (repeatable)");
        command.AddOption(queryOption);
        var apiPathOption = AddString(command, "--api-path", "Relative path under _apis (for the api operation)");
        var methodOption = AddString(command, "--method", "HTTP method for the api operation (default: GET)");
        var versionOption = AddString(command, "--api-version", "API version (overrides the operation default)");
        var orgOption = AddFlag(command, "--organization-scope", "Use organization scope for the api operation");
        var releasesOption = AddFlag(command, "--releases", "Use the Classic Release service for the api operation");
        var patchOption = AddFlag(command, "--json-patch", "Send an application/json-patch+json body");
        var outputOption = AddString(command, "--output-file", "Write the response to a file, for example a downloaded log");
        var jsonOption = AddFlag(command, "--json", "Output JSON (the operation response is JSON when provided by Azure DevOps)");

        command.SetHandler(async (InvocationContext context) =>
        {
            var options = context.ParseResult;
            try
            {
                var name = string.Join(" ", options.GetValueForArgument(operation));
                var repository = options.GetValueForOption(repositoryOption);
                var id = options.GetValueForOption(idOption);
                var project = options.GetValueForOption(projectOption) ?? service?.ConfiguredProject;
                var asJson = options.GetValueForOption(jsonOption);
                if (group == "repos" && PullRequestActions.Contains(name))
                {
                    if (options.GetValueForOption(bodyOption) is not null || options.GetValueForOption(fileOption) is not null
                        || options.GetValueForOption(outputOption) is not null)
                        throw new ArgumentException("This action takes named options. See --help.");
                    await PullRequestActionAsync(name, service, project, Required(repository, "--repository"),
                        RequiredId(id), options.GetValueForOption(voteOption), options.GetValueForOption(textOption), asJson);
                    return;
                }

                var route = name == "api"
                    ? ApiRoute(group, options.GetValueForOption(apiPathOption), options.GetValueForOption(methodOption),
                        options.GetValueForOption(orgOption), options.GetValueForOption(releasesOption))
                    : routes.FirstOrDefault(route => route.Name == name)
                        ?? throw new ArgumentException($"Unknown operation. See fleetmate devops {group} --help.");

                var path = RequestPath(route, repository, id, options.GetValueForOption(resourceOption),
                    options.GetValueForOption(versionOption), options.GetValueForOption(queryOption) ?? []);
                var body = await RequestBodyAsync(group, name, options.GetValueForOption(bodyOption),
                    options.GetValueForOption(fileOption), options.GetValueForOption(sourceOption), options.GetValueForOption(targetOption),
                    options.GetValueForOption(titleOption), options.GetValueForOption(descriptionOption), options.GetValueForOption(draftOption),
                    options.GetValueForOption(resourceOption), options.GetValueForOption(textOption));
                if (route.Scope != "org") project = Required(project, "--project (or configure a default project)");
                var devOps = service ?? throw new InvalidOperationException("Azure DevOps is not configured.");
                if (name == "pr auto-complete" && body is null)
                {
                    var identity = await devOps.GetCurrentIdentityAsync();
                    if (!identity.IsResolved) throw new InvalidOperationException("Could not resolve your Azure DevOps identity.");
                    body = JsonSerializer.Serialize(new { autoCompleteSetBy = new { id = identity.Id } });
                }
                var data = await devOps.RequestResourceAsync(route.Method, path, project, route.Scope == "org",
                    route.Scope == "release", body, options.GetValueForOption(patchOption) ? "application/json-patch+json" : "application/json");

                if (options.GetValueForOption(outputOption) is { } file)
                {
                    await File.WriteAllBytesAsync(file, data);
                    return;
                }

                if (group == "repos" && name == "pr create" && !asJson)
                {
                    using var result = JsonDocument.Parse(data);
                    if (result.RootElement.TryGetProperty("pullRequestId", out var number) && number.TryGetInt32(out var prId))
                    {
                        Console.WriteLine(devOps.ResourcePullRequestUrl(project!, repository!, prId));
                        return;
                    }
                }

                Console.WriteLine(data.Length == 0 ? "null" : Encoding.UTF8.GetString(data));
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                context.ExitCode = 1;
            }
        });
        return command;
    }

    private static Option<string?> AddString(Command command, string name, string description)
    {
        var option = new Option<string?>(name, description);
        command.AddOption(option);
        return option;
    }

    private static Option<bool> AddFlag(Command command, string name, string description)
    {
        var option = new Option<bool>(name, description);
        command.AddOption(option);
        return option;
    }

    private static async Task PullRequestActionAsync(string name, AzureDevOpsService? service, string? project,
        string repository, int id, string? vote, string? text, bool json)
    {
        project = Required(project, "--project (or configure a default project)");
        var devOps = service ?? throw new InvalidOperationException("Azure DevOps is not configured.");
        PullRequestActionResult result;
        switch (name)
        {
            case "pr approve":
                result = await devOps.ApprovePullRequestAsync(repository, id, project);
                break;
            case "pr set-vote":
                if (!Votes.TryGetValue(vote ?? "", out var value))
                    throw new ArgumentException("Choose a supported --vote value. See --help.");
                result = await devOps.VoteAsync(repository, id, project, value);
                break;
            case "pr comment":
                result = await devOps.CommentPullRequestAsync(repository, id, Required(text, "--text"), project);
                break;
            default:
                result = await devOps.CompletePullRequestAsync(repository, id, project);
                break;
        }

        if (!result.Success)
            throw new InvalidOperationException(result.Error ?? "Azure DevOps rejected the operation.");

        if (name == "pr complete")
        {
            var data = await devOps.RequestResourceAsync("GET",
                $"_apis/git/repositories/{Uri.EscapeDataString(repository)}/pullrequests/{id}?api-version=7.1",
                project, organizationScope: false, releases: false);
            Console.WriteLine(Encoding.UTF8.GetString(data));
            return;
        }

        Console.WriteLine(json ? "null" : "Azure DevOps accepted the operation.");
    }

    private static Route ApiRoute(string group, string? value, string? method, bool org, bool releases)
    {
        var path = Required(value, "--api-path");
        if (path.StartsWith("/_apis/", StringComparison.Ordinal)) path = path["/_apis/".Length..];
        var areas = group == "repos" ? new[] { "git", "policy", "wit" }
            : ["pipelines", "build", "distributedtask", "release", "task", "test", "testresults", "serviceendpoint", "securityroles"];
        var segments = path.Split('/');
        var httpMethod = (method ?? "GET").ToUpperInvariant();
        if (!areas.Contains(segments[0]) || segments.Any(IsInvalidApiSegment)
            || httpMethod is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD"))
            throw new ArgumentException($"Use a relative API resource in the {group} area and a supported HTTP method.");
        return new Route("api", httpMethod, path, "7.1", releases || segments[0] == "release" ? "release" : (org ? "org" : "project"));
    }

    private static bool IsInvalidApiSegment(string segment)
    {
        var decoded = Uri.UnescapeDataString(segment);
        return decoded is "" or "." or ".." || decoded.Any(char.IsControl)
            || decoded.IndexOfAny(['/', '\\', '?', '#', '%']) >= 0;
    }

    private static string RequestPath(Route route, string? repository, int? id, string? resource, string? version, string[] query)
    {
        var path = route.Path;
        if (path.Contains("{repository}", StringComparison.Ordinal))
            path = path.Replace("{repository}", Uri.EscapeDataString(Required(repository, "--repository")), StringComparison.Ordinal);
        if (path.Contains("{id}", StringComparison.Ordinal))
            path = path.Replace("{id}", RequiredId(id).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (path.Contains("{resource}", StringComparison.Ordinal))
            path = path.Replace("{resource}", Uri.EscapeDataString(Required(resource, "--resource")), StringComparison.Ordinal);

        var parameters = new List<string> { "api-version=" + Uri.EscapeDataString(version ?? route.Version) };
        foreach (var item in query)
        {
            var equals = item.IndexOf('=');
            if (equals < 1) throw new ArgumentException("Query parameters use NAME=VALUE.");
            var key = item[..equals];
            if (key.Equals("api-version", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Use --api-version to select the API version.");
            parameters.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(item[(equals + 1)..]));
        }

        if (route.Name is "ref lock" or "ref unlock")
            parameters.Add("filter=" + Uri.EscapeDataString(BranchRef(resource, "--resource")["refs/".Length..]));

        if (route.Name == "build artifact show")
            parameters.Add("artifactName=" + Uri.EscapeDataString(Required(resource, "--resource")));

        return "_apis/" + path + "?" + string.Join("&", parameters);
    }

    private static async Task<string?> RequestBodyAsync(string group, string operation, string? body, string? file,
        string? source, string? target, string? title, string? description, bool draft, string? resource, string? text)
    {
        if (body is not null && file is not null)
            throw new ArgumentException("Use either --body or --body-file.");
        if (file is not null) body = await File.ReadAllTextAsync(file);
        if (body is not null)
        {
            using var document = JsonDocument.Parse(body);
            return body;
        }

        if (group == "repos" && operation == "pr create")
        {
            var sourceRef = BranchRef(source, "--source-branch");
            var targetRef = BranchRef(target, "--target-branch");
            if (sourceRef == targetRef) throw new ArgumentException("Source and target branches must differ.");
            return JsonSerializer.Serialize(new
            {
                sourceRefName = sourceRef,
                targetRefName = targetRef,
                title = Required(title, "--title"),
                description = description ?? "",
                isDraft = draft,
            });
        }

        if (group == "pipelines" && operation is "approval approve" or "approval reject")
        {
            if (!Guid.TryParse(Required(resource, "--resource"), out var id))
                throw new ArgumentException("--resource must be an approval UUID.");
            return JsonSerializer.Serialize(new[]
            {
                new { approvalId = id, status = operation == "approval approve" ? "approved" : "rejected", comment = text ?? "" },
            });
        }

        return (group, operation) switch
        {
            ("repos", "pr abandon") => """{"status":"abandoned"}""",
            ("repos", "pr reopen") => """{"status":"active"}""",
            ("repos", "pr ready") => """{"isDraft":false}""",
            ("repos", "pr draft") => """{"isDraft":true}""",
            ("repos", "ref lock") => """{"isLocked":true}""",
            ("repos", "ref unlock") => """{"isLocked":false}""",
            ("pipelines", "build cancel" or "runs cancel") => """{"status":"cancelling"}""",
            ("pipelines", "run") => "{}",
            _ => null,
        };
    }

    private static string Required(string? value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{name} is required.") : value;

    private static int RequiredId(int? id) =>
        id is > 0 ? id.Value : throw new ArgumentException("--id must be a positive integer.");

    private static string BranchRef(string? value, string option)
    {
        var branch = Required(value, option);
        const string prefix = "refs/heads/";
        if (branch == prefix || (branch.StartsWith("refs/", StringComparison.Ordinal) && !branch.StartsWith(prefix, StringComparison.Ordinal)))
            throw new ArgumentException($"{option} must name a branch.");
        return branch.StartsWith(prefix, StringComparison.Ordinal) ? branch : prefix + branch;
    }
}
