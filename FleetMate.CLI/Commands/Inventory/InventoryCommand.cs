using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FleetMate.Core.Config;
using FleetMate.Core.Services.Inventory;

namespace FleetMate.Commands.Inventory;

/// <summary>
/// Backend-neutral inventory operations: the same names reach Snipe-IT today and
/// Cellarum after the switch, chosen by <c>inventory_backend</c>. Mirrors
/// InventoryCommand.swift in fleetmate-macintosh.
/// </summary>
internal static class InventoryCommand
{
    private sealed record Target(string Method, string Path);

    private sealed record Route(string Group, string Name, Target? Snipe, Target? Cellarum)
    {
        public Target? For(InventoryBackend backend) => backend == InventoryBackend.Snipe ? Snipe : Cellarum;
    }

    private static readonly Route[] Routes = InventoryOperationCatalog.Operations.Split('\n')
        .Select(line => line.TrimEnd('\r').Split('|'))
        .Where(fields => fields.Length == 4)
        .Select(fields => new Route(fields[0], fields[1], Parse(fields[2]), Parse(fields[3])))
        .ToArray();

    private static Target? Parse(string value)
    {
        var space = value.IndexOf(' ');
        return value == "-" || space < 0 ? null : new Target(value[..space], value[(space + 1)..]);
    }

    internal static string Help()
    {
        var lines = new List<string> { "Operations (S = Snipe-IT, C = Cellarum):" };
        var lastGroup = "";
        foreach (var route in Routes)
        {
            if (route.Group != lastGroup)
            {
                lines.Add($"  {route.Group}");
                lastGroup = route.Group;
            }
            var marks = (route.Snipe is not null ? "S" : " ") + (route.Cellarum is not null ? "C" : " ");
            lines.Add($"    [{marks}] {route.Name}");
        }
        lines.Add("  files");
        lines.Add("    [SC] upload");
        lines.Add("");
        lines.Add("Also: backend (which system is active), operations [--json] (routes on the active backend),");
        lines.Add("and api --method M --api-path P for any endpoint outside the named operations.");
        lines.Add("Set inventory_backend: cellarum to move every operation to Cellarum.");
        return string.Join("\n", lines);
    }

    internal static Command Create(FleetMateConfig config)
    {
        var command = new Command("inventory",
            "Inventory operations on the configured backend (Snipe-IT or Cellarum)\n\n" + Help());
        var operation = new Argument<string[]>("operation",
            "Group and operation, for example 'buyouts transition', 'files upload', 'operations', 'backend' or 'api'")
        {
            Arity = ArgumentArity.ZeroOrMore
        };
        command.AddArgument(operation);

        var idOption = AddString(command, "--id", "Primary record ID (asset ID, buyout ID, order ID, tag or serial for find-tag/find-serial)");
        var resourceOption = AddString(command, "--resource", "Nested record ID, such as an invoice, file or order line");
        var recordOption = AddString(command, "--record", "Record kind for files: asset, person, license, model, supplier, contract, purchase-order, lease-schedule, maintenance, location");
        var bodyOption = AddString(command, "--body", "JSON request body");
        var bodyFileOption = AddString(command, "--body-file", "Path to a file containing the JSON request body");
        var queryOption = new Option<string[]>("--query", "Add a query parameter as NAME=VALUE (repeatable)");
        command.AddOption(queryOption);
        var fileOption = AddString(command, "--file", "File to attach (files upload)");
        var notesOption = AddString(command, "--notes", "Note stored with an uploaded file");
        var domainOption = AddString(command, "--domain", "Cellarum domain holding an uploaded file (files upload on Cellarum)");
        var backendOption = AddString(command, "--backend", "Use this backend for one call instead of the configured one: snipe or cellarum");
        var apiPathOption = AddString(command, "--api-path", "Path relative to /api/v1 (for the api operation)");
        var methodOption = AddString(command, "--method", "HTTP method for the api operation (default: GET)");
        var jsonOption = new Option<bool>("--json", "Print the response JSON without reformatting");
        command.AddOption(jsonOption);
        var outputOption = AddString(command, "--output-file", "Write the response to a file, for example a PDF or a downloaded attachment");

        command.SetHandler(async (InvocationContext context) =>
        {
            var options = context.ParseResult;
            try
            {
                var json = options.GetValueForOption(jsonOption);
                var backendName = options.GetValueForOption(backendOption);
                var selected = backendName is null
                    ? InventoryBackends.Configured(config)
                    : InventoryBackends.Parse(backendName) ?? throw new ArgumentException("--backend is snipe or cellarum.");

                var words = options.GetValueForArgument(operation) ?? [];
                if (words.Length == 0)
                {
                    Console.WriteLine(Help());
                    return;
                }
                var group = words[0];
                var name = string.Join(" ", words.Skip(1));

                if (group == "backend" && name == "")
                {
                    PrintBackend(selected, config, json);
                    return;
                }
                if (group == "operations" && name == "")
                {
                    PrintOperations(selected, json);
                    return;
                }

                var output = options.GetValueForOption(outputOption);
                var record = options.GetValueForOption(recordOption);
                var id = options.GetValueForOption(idOption);

                if (group == "files" && name == "upload")
                {
                    using var uploader = new InventoryApi(config, selected);
                    var response = await UploadAsync(uploader, options.GetValueForOption(fileOption), record, id,
                        options.GetValueForOption(notesOption), options.GetValueForOption(domainOption));
                    await PrintResponseAsync(response, output, json);
                    return;
                }

                string method, template;
                if (group == "api" && name == "")
                {
                    var path = Required(options.GetValueForOption(apiPathOption), "--api-path");
                    var relative = path.StartsWith("/api/v1/", StringComparison.Ordinal) ? path["/api/v1/".Length..]
                        : path.TrimStart('/');
                    var requested = (options.GetValueForOption(methodOption) ?? "GET").ToUpperInvariant();
                    if (requested is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
                        throw new ArgumentException("--method is GET, POST, PUT, PATCH or DELETE.");
                    (method, template) = (requested, relative);
                }
                else
                {
                    var known = Routes.FirstOrDefault(route => route.Group == group && route.Name == name)
                        ?? throw new ArgumentException($"Unknown operation '{group} {name}'. See fleetmate inventory --help.");
                    var target = known.For(selected)
                        ?? throw new ArgumentException($"{selected.DisplayName()} has no '{group} {name}'. Use the api operation, or see fleetmate inventory operations.");
                    (method, template) = (target.Method, target.Path);
                }

                using var api = new InventoryApi(config, selected);
                var filled = Fill(template, selected, id, options.GetValueForOption(resourceOption), record);
                var body = await RequestBodyAsync(options.GetValueForOption(bodyOption), options.GetValueForOption(bodyFileOption));
                if (body is not null && method == "GET")
                    throw new ArgumentException("A GET takes no body; use --query.");
                var result = await api.SendAsync(method, filled, QueryItems(options.GetValueForOption(queryOption) ?? []),
                    body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"));
                await PrintResponseAsync(result, output, json);
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"Error: {error.Message}");
                context.ExitCode = 1;
            }
        });
        return command;
    }

    private static string Fill(string template, InventoryBackend backend, string? id, string? resource, string? record)
    {
        var path = template;
        if (path.Contains("{record}")) path = path.Replace("{record}", RecordName(backend, record));
        if (path.Contains("{id}")) path = path.Replace("{id}", InventoryApi.Escape(Required(id, "--id")));
        if (path.Contains("{resource}")) path = path.Replace("{resource}", InventoryApi.Escape(Required(resource, "--resource")));
        return path;
    }

    private static (string Snipe, string Cellarum) RecordKind(string? record)
    {
        var kind = Required(record, "--record");
        return InventoryOperationCatalog.RecordKinds.TryGetValue(kind, out var names)
            ? names
            : throw new ArgumentException($"--record is one of: {string.Join(", ", InventoryOperationCatalog.RecordKinds.Keys.Order(StringComparer.Ordinal))}.");
    }

    private static string RecordName(InventoryBackend backend, string? record)
    {
        var names = RecordKind(record);
        var value = backend == InventoryBackend.Snipe ? names.Snipe : names.Cellarum;
        return value != "-" ? value : throw new ArgumentException($"{backend.DisplayName()} keeps no files on a {record}.");
    }

    private static List<KeyValuePair<string, string>> QueryItems(string[] query) =>
        query.Select(item =>
        {
            var index = item.IndexOf('=');
            if (index <= 0) throw new ArgumentException("Query parameters use NAME=VALUE.");
            return new KeyValuePair<string, string>(item[..index], item[(index + 1)..]);
        }).ToList();

    private static async Task<string?> RequestBodyAsync(string? body, string? bodyFile)
    {
        if (body is not null && bodyFile is not null) throw new ArgumentException("Use either --body or --body-file.");
        var source = body ?? (bodyFile is null ? null : await File.ReadAllTextAsync(ExpandHome(bodyFile)));
        if (source is null) return null;
        try
        {
            using var _ = JsonDocument.Parse(source);
        }
        catch (JsonException)
        {
            throw new ArgumentException("The request body is not valid JSON.");
        }
        return source;
    }

    private static async Task<InventoryApi.Response> UploadAsync(InventoryApi api, string? file, string? record, string? id,
        string? notes, string? domain)
    {
        var path = ExpandHome(Required(file, "--file"));
        if (!File.Exists(path)) throw new ArgumentException($"Cannot read {path}.");
        var names = RecordKind(record);
        if (api.Backend == InventoryBackend.Cellarum)
        {
            if (names.Cellarum == "-") throw new ArgumentException($"Cellarum keeps no files on a {record}.");
            if (domain is null && record is not ("model" or "supplier" or "person"))
                throw new ArgumentException($"Cellarum needs --domain for a file on a {record}.");
        }
        return await api.UploadAsync(path, names.Snipe, names.Cellarum, Required(id, "--id"), notes, domain);
    }

    private static async Task PrintResponseAsync(InventoryApi.Response response, string? outputFile, bool json)
    {
        if (outputFile is not null)
        {
            var target = Path.GetFullPath(ExpandHome(outputFile));
            await File.WriteAllBytesAsync(target, response.Data);
            if (!json) Console.WriteLine($"Wrote {response.Data.Length} bytes to {target}");
            return;
        }
        var type = (response.ContentType ?? "").ToLowerInvariant();
        if (type.Length > 0 && !type.Contains("json") && !type.StartsWith("text/", StringComparison.Ordinal))
            throw new ArgumentException($"The response is {type}; pass --output-file to save it.");
        if (response.Data.Length == 0)
        {
            Console.WriteLine("null");
            return;
        }
        var text = Encoding.UTF8.GetString(response.Data);
        if (!json)
        {
            try
            {
                var node = JsonNode.Parse(text);
                Console.WriteLine(node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "null");
                return;
            }
            catch (JsonException)
            {
            }
        }
        Console.WriteLine(text);
    }

    private static void PrintBackend(InventoryBackend backend, FleetMateConfig config, bool json)
    {
        var snipe = backend == InventoryBackend.Snipe;
        var url = snipe ? config.SnipeUrl : config.CellarumUrl;
        var audience = snipe ? config.SnipeOidcAudience : config.CellarumOidcAudience;
#pragma warning disable CS0618
        var key = snipe ? config.SnipeApiKey : config.CellarumApiKey;
#pragma warning restore CS0618
        var auth = !string.IsNullOrEmpty(audience) ? "entra" : !string.IsNullOrEmpty(key) ? "api-key" : "none";
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(new SortedDictionary<string, string?>
            {
                ["auth"] = auth, ["backend"] = backend.Key(), ["url"] = url,
            }));
        else
            Console.WriteLine($"{backend.DisplayName()} · {url ?? "no URL configured"} · auth: {auth}");
    }

    private static void PrintOperations(InventoryBackend backend, bool json)
    {
        var available = Routes.Select(route => (route, target: route.For(backend)))
            .Where(pair => pair.target is not null).ToList();
        if (json)
        {
            var rows = available.Select(pair => new SortedDictionary<string, string>
            {
                ["group"] = pair.route.Group, ["method"] = pair.target!.Method,
                ["operation"] = pair.route.Name, ["path"] = pair.target.Path,
            }).Append(new SortedDictionary<string, string>
            {
                ["group"] = "files", ["method"] = "POST", ["operation"] = "upload",
                ["path"] = backend == InventoryBackend.Snipe ? "{record}/{id}/files" : "attachments",
            });
            Console.WriteLine(JsonSerializer.Serialize(new SortedDictionary<string, object>
            {
                ["backend"] = backend.Key(), ["operations"] = rows.ToList(),
            }, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }
        Console.WriteLine($"{backend.DisplayName()} operations:");
        foreach (var (route, target) in available)
            Console.WriteLine($"  {route.Group} {route.Name}".PadRight(40) + $"{target!.Method} {target.Path}");
    }

    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    private static string Required(string? value, string option) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"{option} is required.") : value;

    private static Option<string?> AddString(Command command, string name, string description)
    {
        var option = new Option<string?>(name, description);
        command.AddOption(option);
        return option;
    }
}
