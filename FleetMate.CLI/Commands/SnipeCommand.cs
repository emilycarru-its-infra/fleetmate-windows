#nullable disable warnings
using System.CommandLine;
using FleetMate.Models.Snipe;
using FleetMate.Services;
using Spectre.Console;

namespace FleetMate.Commands;

public static class SnipeCommand
{
    public static Command Create(SnipeService? snipe)
    {
        var command = new Command("snipe", "Snipe-IT asset management commands");
        
        // Always add subcommands so help text shows them
        // Each subcommand will check if snipe is null and show config error
        command.AddCommand(CreateAssetsCommand(snipe));
        command.AddCommand(CreateAssetCommand(snipe));
        command.AddCommand(CreateUsersCommand(snipe));
        command.AddCommand(CreateUserCommand(snipe));
        command.AddCommand(CreateLocationsCommand(snipe));
        command.AddCommand(CreateModelsCommand(snipe));
        command.AddCommand(CreateCategoriesCommand(snipe));
        command.AddCommand(CreateLicensesCommand(snipe));
        command.AddCommand(CreateManufacturersCommand(snipe));
        command.AddCommand(CreateStatusLabelsCommand(snipe));
        command.AddCommand(CreateAccessoriesCommand(snipe));
        command.AddCommand(CreateConsumablesCommand(snipe));
        command.AddCommand(CreateComponentsCommand(snipe));
        command.AddCommand(CreateActivityCommand(snipe));
        command.AddCommand(CreateCheckoutCommand(snipe));
        command.AddCommand(CreateCheckinCommand(snipe));
        command.AddCommand(CreateAuditCommand(snipe));
        
        if (snipe == null)
        {
            command.SetHandler(() =>
            {
                AnsiConsole.MarkupLine("[red]Snipe-IT is not configured.[/]");
                AnsiConsole.MarkupLine("Set [cyan]SNIPE_URL[/] and [cyan]SNIPE_API_KEY[/] environment variables,");
                AnsiConsole.MarkupLine("or add them to your config file (~/.fleetmate/config.yaml).");
            });
        }
        
        return command;
    }
    
    private static bool EnsureConfigured(SnipeService? snipe)
    {
        if (snipe != null) return true;
        
        AnsiConsole.MarkupLine("[red]Snipe-IT is not configured.[/]");
        AnsiConsole.MarkupLine("Set [cyan]SNIPE_URL[/] and [cyan]SNIPE_API_KEY[/] environment variables,");
        AnsiConsole.MarkupLine("or add them to your config file (~/.fleetmate/config.yaml).");
        return false;
    }

    #region Assets Commands
    
    private static Command CreateAssetsCommand(SnipeService? snipe)
    {
        var command = new Command("assets", "List assets/hardware");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term (name, asset tag, serial, etc.)");
        
        var statusOption = new Option<int?>(
            aliases: ["--status", "-t"],
            description: "Filter by status label ID");
        
        var locationOption = new Option<int?>(
            aliases: ["--location", "-l"],
            description: "Filter by location ID");
        
        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Number of results to display");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(statusOption);
        command.AddOption(locationOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, status, location, limit, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var assets = await snipe.GetAssetsAsync(search: search, statusId: status, locationId: location);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(assets.Take(limit), 
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (assets.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No assets found[/]");
                return;
            }
            
            var displayAssets = assets.Take(limit).ToList();
            AnsiConsole.MarkupLine($"[dim]Showing {displayAssets.Count} of {assets.Count} assets[/]");
            Console.WriteLine();
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Asset Tag");
            table.AddColumn("Name");
            table.AddColumn("Serial");
            table.AddColumn("Model");
            table.AddColumn("Status");
            table.AddColumn("Assigned To");
            
            foreach (var asset in displayAssets)
            {
                var statusMarkup = GetStatusMarkup(asset.StatusLabel);
                var assigned = asset.AssignedTo?.Name ?? "[dim]-[/]";
                
                table.AddRow(
                    asset.Id.ToString(),
                    asset.AssetTag ?? "",
                    asset.Name ?? "",
                    asset.Serial ?? "[dim]-[/]",
                    asset.Model?.Name ?? "[dim]-[/]",
                    statusMarkup,
                    assigned
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, statusOption, locationOption, limitOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateAssetCommand(SnipeService? snipe)
    {
        var command = new Command("asset", "Get details for a specific asset");
        
        var queryArg = new Argument<string>(
            name: "query",
            description: "Asset ID, asset tag, or serial number");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddArgument(queryArg);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (query, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            SnipeAsset? asset = null;
            
            // Try by ID first
            if (int.TryParse(query, out var id))
            {
                asset = await snipe.GetAssetAsync(id);
            }
            
            // Try by asset tag
            asset ??= await snipe.GetAssetByTagAsync(query);
            
            // Try by serial
            asset ??= await snipe.GetAssetBySerialAsync(query);
            
            if (asset == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No asset found for:[/] {query}");
                return;
            }
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(asset,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            DisplayAssetDetails(asset);
        }, queryArg, jsonOption);
        
        return command;
    }
    
    private static void DisplayAssetDetails(SnipeAsset asset)
    {
        var panel = new Panel($"[bold]{asset.Name ?? asset.AssetTag}[/]")
            .Header("[cyan]Asset Details[/]")
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
        Console.WriteLine();
        
        var table = new Table();
        table.Border = TableBorder.Rounded;
        table.AddColumn("Property");
        table.AddColumn("Value");
        
        table.AddRow("ID", asset.Id.ToString());
        table.AddRow("Asset Tag", asset.AssetTag ?? "-");
        table.AddRow("Name", asset.Name ?? "-");
        table.AddRow("Serial", asset.Serial ?? "-");
        table.AddRow("Model", asset.Model?.Name ?? "-");
        table.AddRow("Category", asset.Category?.Name ?? "-");
        table.AddRow("Manufacturer", asset.Manufacturer?.Name ?? "-");
        table.AddRow("Status", GetStatusMarkup(asset.StatusLabel));
        table.AddRow("Assigned To", asset.AssignedTo?.Name ?? "-");
        table.AddRow("Location", asset.Location?.Name ?? "-");
        
        if (asset.PurchaseDate != null)
            table.AddRow("Purchase Date", asset.PurchaseDate.Formatted ?? "-");
        if (!string.IsNullOrEmpty(asset.PurchaseCost))
            table.AddRow("Purchase Cost", asset.PurchaseCost);
        if (asset.WarrantyMonths.HasValue)
            table.AddRow("Warranty (months)", asset.WarrantyMonths.Value.ToString());
        if (!string.IsNullOrEmpty(asset.Eol))
            table.AddRow("EOL", asset.Eol);
        
        if (!string.IsNullOrEmpty(asset.OrderNumber))
            table.AddRow("Order Number", asset.OrderNumber);
        if (!string.IsNullOrEmpty(asset.Notes))
            table.AddRow("Notes", asset.Notes);
        
        table.AddRow("Created", asset.CreatedAt?.Formatted ?? "-");
        table.AddRow("Updated", asset.UpdatedAt?.Formatted ?? "-");
        
        AnsiConsole.Write(table);
        
        // Show custom fields if any
        if (asset.CustomFields?.Count > 0)
        {
            Console.WriteLine();
            AnsiConsole.Write(new Rule("[cyan]Custom Fields[/]").LeftJustified());
            
            var cfTable = new Table();
            cfTable.Border = TableBorder.Simple;
            cfTable.AddColumn("Field");
            cfTable.AddColumn("Value");
            
            foreach (var (name, field) in asset.CustomFields)
            {
                cfTable.AddRow(name, field.Value ?? "[dim]-[/]");
            }
            
            AnsiConsole.Write(cfTable);
        }
        
        // Show available actions
        if (asset.AvailableActions != null)
        {
            Console.WriteLine();
            var actions = new List<string>();
            if (asset.AvailableActions.Checkout) actions.Add("[green]checkout[/]");
            if (asset.AvailableActions.Checkin) actions.Add("[yellow]checkin[/]");
            if (asset.AvailableActions.Update) actions.Add("[blue]update[/]");
            if (asset.AvailableActions.Delete) actions.Add("[red]delete[/]");
            if (asset.AvailableActions.Clone) actions.Add("[cyan]clone[/]");
            if (asset.AvailableActions.Restore) actions.Add("[magenta]restore[/]");
            
            if (actions.Count > 0)
            {
                AnsiConsole.MarkupLine($"[dim]Available actions:[/] {string.Join(", ", actions)}");
            }
        }
    }
    
    #endregion
    
    #region Users Commands
    
    private static Command CreateUsersCommand(SnipeService? snipe)
    {
        var command = new Command("users", "List users");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 50,
            description: "Number of results to display");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(limitOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, limit, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var users = await snipe.GetUsersAsync(search: search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(users.Take(limit),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (users.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No users found[/]");
                return;
            }
            
            var displayUsers = users.Take(limit).ToList();
            AnsiConsole.MarkupLine($"[dim]Showing {displayUsers.Count} of {users.Count} users[/]");
            Console.WriteLine();
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Username");
            table.AddColumn("Name");
            table.AddColumn("Email");
            table.AddColumn("Department");
            table.AddColumn("Assets");
            
            foreach (var user in displayUsers)
            {
                table.AddRow(
                    user.Id.ToString(),
                    user.Username ?? "",
                    user.Name ?? "",
                    user.Email ?? "[dim]-[/]",
                    user.Department?.Name ?? "[dim]-[/]",
                    user.AssetsCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, limitOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateUserCommand(SnipeService? snipe)
    {
        var command = new Command("user", "Get details for a specific user");
        
        var idArg = new Argument<int>(
            name: "id",
            description: "User ID");
        
        var assetsOption = new Option<bool>(
            aliases: ["--assets", "-a"],
            description: "Show user's assigned assets");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddArgument(idArg);
        command.AddOption(assetsOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (id, showAssets, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var user = await snipe.GetUserAsync(id);
            
            if (user == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No user found with ID:[/] {id}");
                return;
            }
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(user,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("Property");
            table.AddColumn("Value");
            
            table.AddRow("ID", user.Id.ToString());
            table.AddRow("Username", user.Username ?? "-");
            table.AddRow("Name", user.Name ?? "-");
            table.AddRow("First Name", user.FirstName ?? "-");
            table.AddRow("Last Name", user.LastName ?? "-");
            table.AddRow("Email", user.Email ?? "-");
            table.AddRow("Employee #", user.EmployeeNum ?? "-");
            table.AddRow("Department", user.Department?.Name ?? "-");
            table.AddRow("Manager", user.Manager?.Name ?? "-");
            table.AddRow("Location", user.Location?.Name ?? "-");
            table.AddRow("Job Title", user.JobTitle ?? "-");
            table.AddRow("Phone", user.Phone ?? "-");
            table.AddRow("Assets", user.AssetsCount.ToString());
            table.AddRow("Licenses", user.LicensesCount.ToString());
            table.AddRow("Accessories", user.AccessoriesCount.ToString());
            table.AddRow("Activated", user.Activated ? "[green]Yes[/]" : "[red]No[/]");
            
            AnsiConsole.Write(table);
            
            if (showAssets)
            {
                Console.WriteLine();
                var assets = await snipe.GetUserAssetsAsync(id);
                
                if (assets.Count == 0)
                {
                    AnsiConsole.MarkupLine("[dim]No assets assigned to this user[/]");
                    return;
                }
                
                AnsiConsole.Write(new Rule("[cyan]Assigned Assets[/]").LeftJustified());
                
                var assetTable = new Table();
                assetTable.Border = TableBorder.Simple;
                assetTable.AddColumn("ID");
                assetTable.AddColumn("Asset Tag");
                assetTable.AddColumn("Name");
                assetTable.AddColumn("Model");
                
                foreach (var asset in assets)
                {
                    assetTable.AddRow(
                        asset.Id.ToString(),
                        asset.AssetTag ?? "",
                        asset.Name ?? "",
                        asset.Model?.Name ?? "-"
                    );
                }
                
                AnsiConsole.Write(assetTable);
            }
        }, idArg, assetsOption, jsonOption);
        
        return command;
    }
    
    #endregion
    
    #region List Commands
    
    private static Command CreateLocationsCommand(SnipeService? snipe)
    {
        var command = new Command("locations", "List locations");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var locations = await snipe.GetLocationsAsync(search: search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(locations,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (locations.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No locations found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("City");
            table.AddColumn("State");
            table.AddColumn("Country");
            table.AddColumn("Assets");
            
            foreach (var loc in locations)
            {
                table.AddRow(
                    loc.Id.ToString(),
                    loc.Name ?? "",
                    loc.City ?? "[dim]-[/]",
                    loc.State ?? "[dim]-[/]",
                    loc.Country ?? "[dim]-[/]",
                    loc.AssetsCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateModelsCommand(SnipeService? snipe)
    {
        var command = new Command("models", "List asset models");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var models = await snipe.GetModelsAsync(search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(models,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (models.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No models found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Model #");
            table.AddColumn("Manufacturer");
            table.AddColumn("Category");
            table.AddColumn("Assets");
            
            foreach (var model in models)
            {
                table.AddRow(
                    model.Id.ToString(),
                    model.Name ?? "",
                    model.ModelNumber ?? "[dim]-[/]",
                    model.Manufacturer?.Name ?? "[dim]-[/]",
                    model.Category?.Name ?? "[dim]-[/]",
                    model.AssetsCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateCategoriesCommand(SnipeService? snipe)
    {
        var command = new Command("categories", "List categories");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(jsonOption);
        
        command.SetHandler(async (json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var categories = await snipe.GetCategoriesAsync();
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(categories,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (categories.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No categories found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Items");
            
            foreach (var cat in categories)
            {
                table.AddRow(
                    cat.Id.ToString(),
                    cat.Name ?? "",
                    cat.CategoryType ?? "[dim]-[/]",
                    cat.ItemCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, jsonOption);
        
        return command;
    }
    
    private static Command CreateLicensesCommand(SnipeService? snipe)
    {
        var command = new Command("licenses", "List software licenses");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var licenses = await snipe.GetLicensesAsync(search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(licenses,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (licenses.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No licenses found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Product Key");
            table.AddColumn("Seats");
            table.AddColumn("Free");
            table.AddColumn("Expires");
            
            foreach (var lic in licenses)
            {
                var freeSeats = lic.FreeSeatsCount.ToString();
                var freeColor = lic.FreeSeatsCount > 0 ? "green" : "red";
                
                table.AddRow(
                    lic.Id.ToString(),
                    lic.Name ?? "",
                    lic.ProductKey != null ? "[dim]****[/]" : "[dim]-[/]",
                    lic.Seats.ToString(),
                    $"[{freeColor}]{freeSeats}[/]",
                    lic.ExpirationDate?.Formatted ?? "[dim]-[/]"
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateManufacturersCommand(SnipeService? snipe)
    {
        var command = new Command("manufacturers", "List manufacturers");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(jsonOption);
        
        command.SetHandler(async (json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var manufacturers = await snipe.GetManufacturersAsync();
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(manufacturers,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (manufacturers.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No manufacturers found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Assets");
            
            foreach (var mfg in manufacturers)
            {
                table.AddRow(
                    mfg.Id.ToString(),
                    mfg.Name ?? "",
                    mfg.AssetsCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, jsonOption);
        
        return command;
    }
    
    private static Command CreateStatusLabelsCommand(SnipeService? snipe)
    {
        var command = new Command("statuses", "List status labels");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(jsonOption);
        
        command.SetHandler(async (json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var statuses = await snipe.GetStatusLabelsAsync();
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(statuses,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (statuses.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No status labels found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Type");
            table.AddColumn("Assets");
            
            foreach (var status in statuses)
            {
                var typeName = status.Type switch
                {
                    "deployable" => "[green]Deployable[/]",
                    "pending" => "[yellow]Pending[/]",
                    "archived" => "[dim]Archived[/]",
                    "undeployable" => "[red]Undeployable[/]",
                    _ => status.Type ?? "[dim]-[/]"
                };
                
                table.AddRow(
                    status.Id.ToString(),
                    status.Name ?? "",
                    typeName,
                    status.AssetsCount.ToString()
                );
            }
            
            AnsiConsole.Write(table);
        }, jsonOption);
        
        return command;
    }
    
    private static Command CreateAccessoriesCommand(SnipeService? snipe)
    {
        var command = new Command("accessories", "List accessories");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var accessories = await snipe.GetAccessoriesAsync(search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(accessories,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (accessories.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No accessories found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Category");
            table.AddColumn("Qty");
            table.AddColumn("Remaining");
            
            foreach (var acc in accessories)
            {
                var remaining = acc.RemainingQty.ToString();
                var remainingColor = acc.RemainingQty > 0 ? "green" : "red";
                
                table.AddRow(
                    acc.Id.ToString(),
                    acc.Name ?? "",
                    acc.Category?.Name ?? "[dim]-[/]",
                    acc.Qty.ToString(),
                    $"[{remainingColor}]{remaining}[/]"
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateConsumablesCommand(SnipeService? snipe)
    {
        var command = new Command("consumables", "List consumables");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var consumables = await snipe.GetConsumablesAsync(search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(consumables,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (consumables.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No consumables found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Category");
            table.AddColumn("Qty");
            table.AddColumn("Remaining");
            
            foreach (var con in consumables)
            {
                var remaining = con.Remaining.ToString();
                var remainingColor = con.Remaining > 0 ? "green" : "red";
                
                table.AddRow(
                    con.Id.ToString(),
                    con.Name ?? "",
                    con.Category?.Name ?? "[dim]-[/]",
                    con.Qty.ToString(),
                    $"[{remainingColor}]{remaining}[/]"
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    private static Command CreateComponentsCommand(SnipeService? snipe)
    {
        var command = new Command("components", "List components");
        
        var searchOption = new Option<string?>(
            aliases: ["--search", "-s"],
            description: "Search term");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(searchOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (search, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var components = await snipe.GetComponentsAsync(search);
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(components,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (components.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No components found[/]");
                return;
            }
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("ID");
            table.AddColumn("Name");
            table.AddColumn("Category");
            table.AddColumn("Qty");
            table.AddColumn("Remaining");
            
            foreach (var comp in components)
            {
                var remaining = comp.Remaining.ToString();
                var remainingColor = comp.Remaining > 0 ? "green" : "red";
                
                table.AddRow(
                    comp.Id.ToString(),
                    comp.Name ?? "",
                    comp.Category?.Name ?? "[dim]-[/]",
                    comp.Qty.ToString(),
                    $"[{remainingColor}]{remaining}[/]"
                );
            }
            
            AnsiConsole.Write(table);
        }, searchOption, jsonOption);
        
        return command;
    }
    
    #endregion
    
    #region Activity Command
    
    private static Command CreateActivityCommand(SnipeService? snipe)
    {
        var command = new Command("activity", "Show recent activity/audit log");
        
        var limitOption = new Option<int>(
            aliases: ["--limit", "-n"],
            getDefaultValue: () => 25,
            description: "Number of results to display");
        
        var jsonOption = new Option<bool>(
            aliases: ["--json", "-j"],
            description: "Output as JSON");
        
        command.AddOption(limitOption);
        command.AddOption(jsonOption);
        
        command.SetHandler(async (limit, json) =>
        {
            if (!EnsureConfigured(snipe)) return;
            var activities = await snipe.GetActivityAsync();
            
            if (json)
            {
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(activities.Take(limit),
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                return;
            }
            
            if (activities.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No activity found[/]");
                return;
            }
            
            var displayActivities = activities.Take(limit).ToList();
            AnsiConsole.MarkupLine($"[dim]Showing {displayActivities.Count} of {activities.Count} activities[/]");
            Console.WriteLine();
            
            var table = new Table();
            table.Border = TableBorder.Rounded;
            table.AddColumn("When");
            table.AddColumn("Action");
            table.AddColumn("Item");
            table.AddColumn("Target");
            table.AddColumn("User");
            
            foreach (var activity in displayActivities)
            {
                var when = activity.CreatedAt?.Formatted ?? "-";
                var action = activity.ActionType ?? "-";
                var item = activity.Item?.Name ?? "-";
                var target = activity.Target?.Name ?? "-";
                var user = activity.Admin?.Name ?? "-";
                
                var actionMarkup = action switch
                {
                    "checkout" => "[green]checkout[/]",
                    "checkin from" or "checkin" => "[yellow]checkin[/]",
                    "create" => "[cyan]create[/]",
                    "update" => "[blue]update[/]",
                    "delete" => "[red]delete[/]",
                    _ => action
                };
                
                table.AddRow(when, actionMarkup, item, target, user);
            }
            
            AnsiConsole.Write(table);
        }, limitOption, jsonOption);
        
        return command;
    }
    
    #endregion
    
    #region Action Commands
    
    private static Command CreateCheckoutCommand(SnipeService? snipe)
    {
        var command = new Command("checkout", "Checkout an asset to a user or location");
        
        var assetArg = new Argument<string>(
            name: "asset",
            description: "Asset ID, asset tag, or serial number");
        
        var userOption = new Option<int?>(
            aliases: ["--user", "-u"],
            description: "User ID to checkout to");
        
        var locationOption = new Option<int?>(
            aliases: ["--location", "-l"],
            description: "Location ID to checkout to");
        
        var noteOption = new Option<string?>(
            aliases: ["--note", "-n"],
            description: "Checkout note");
        
        command.AddArgument(assetArg);
        command.AddOption(userOption);
        command.AddOption(locationOption);
        command.AddOption(noteOption);
        
        command.SetHandler(async (assetQuery, userId, locationId, note) =>
        {
            if (!EnsureConfigured(snipe)) return;
            if (!userId.HasValue && !locationId.HasValue)
            {
                AnsiConsole.MarkupLine("[red]Must specify either --user or --location[/]");
                return;
            }
            
            // Find asset
            SnipeAsset? asset = null;
            if (int.TryParse(assetQuery, out var assetId))
            {
                asset = await snipe.GetAssetAsync(assetId);
            }
            asset ??= await snipe.GetAssetByTagAsync(assetQuery);
            asset ??= await snipe.GetAssetBySerialAsync(assetQuery);
            
            if (asset == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No asset found for:[/] {assetQuery}");
                return;
            }
            
            var request = new SnipeCheckoutRequest
            {
                Note = note
            };
            
            if (userId.HasValue)
            {
                request.CheckoutToType = "user";
                request.AssignedUser = userId.Value;
            }
            else if (locationId.HasValue)
            {
                request.CheckoutToType = "location";
                request.AssignedLocation = locationId.Value;
            }
            
            var response = await snipe.CheckoutAssetAsync(asset.Id, request);
            
            if (response?.IsSuccess == true)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Asset [cyan]{asset.AssetTag}[/] checked out successfully");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to checkout asset [cyan]{asset.AssetTag}[/]");
                if (!string.IsNullOrEmpty(response?.Messages))
                {
                    AnsiConsole.MarkupLine($"[red]{response.Messages}[/]");
                }
            }
        }, assetArg, userOption, locationOption, noteOption);
        
        return command;
    }
    
    private static Command CreateCheckinCommand(SnipeService? snipe)
    {
        var command = new Command("checkin", "Checkin an asset");
        
        var assetArg = new Argument<string>(
            name: "asset",
            description: "Asset ID, asset tag, or serial number");
        
        var noteOption = new Option<string?>(
            aliases: ["--note", "-n"],
            description: "Checkin note");
        
        var statusOption = new Option<int?>(
            aliases: ["--status", "-s"],
            description: "Status label ID to set");
        
        command.AddArgument(assetArg);
        command.AddOption(noteOption);
        command.AddOption(statusOption);
        
        command.SetHandler(async (assetQuery, note, statusId) =>
        {
            if (!EnsureConfigured(snipe)) return;
            // Find asset
            SnipeAsset? asset = null;
            if (int.TryParse(assetQuery, out var assetId))
            {
                asset = await snipe.GetAssetAsync(assetId);
            }
            asset ??= await snipe.GetAssetByTagAsync(assetQuery);
            asset ??= await snipe.GetAssetBySerialAsync(assetQuery);
            
            if (asset == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No asset found for:[/] {assetQuery}");
                return;
            }
            
            var request = new SnipeCheckinRequest
            {
                Note = note,
                StatusId = statusId
            };
            
            var response = await snipe.CheckinAssetAsync(asset.Id, request);
            
            if (response?.IsSuccess == true)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Asset [cyan]{asset.AssetTag}[/] checked in successfully");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to checkin asset [cyan]{asset.AssetTag}[/]");
                if (!string.IsNullOrEmpty(response?.Messages))
                {
                    AnsiConsole.MarkupLine($"[red]{response.Messages}[/]");
                }
            }
        }, assetArg, noteOption, statusOption);
        
        return command;
    }
    
    private static Command CreateAuditCommand(SnipeService? snipe)
    {
        var command = new Command("audit", "Audit an asset (confirm location/existence)");
        
        var assetArg = new Argument<string>(
            name: "asset",
            description: "Asset ID, asset tag, or serial number");
        
        var locationOption = new Option<int?>(
            aliases: ["--location", "-l"],
            description: "Location ID where asset was found");
        
        var noteOption = new Option<string?>(
            aliases: ["--note", "-n"],
            description: "Audit note");
        
        command.AddArgument(assetArg);
        command.AddOption(locationOption);
        command.AddOption(noteOption);
        
        command.SetHandler(async (assetQuery, locationId, note) =>
        {
            if (!EnsureConfigured(snipe)) return;
            // Find asset
            SnipeAsset? asset = null;
            if (int.TryParse(assetQuery, out var assetId))
            {
                asset = await snipe.GetAssetAsync(assetId);
            }
            asset ??= await snipe.GetAssetByTagAsync(assetQuery);
            asset ??= await snipe.GetAssetBySerialAsync(assetQuery);
            
            if (asset == null)
            {
                AnsiConsole.MarkupLine($"[yellow]No asset found for:[/] {assetQuery}");
                return;
            }
            
            var request = new SnipeAuditRequest
            {
                Note = note,
                LocationId = locationId,
                UpdateLocation = locationId.HasValue
            };
            
            var response = await snipe.AuditAssetAsync(asset.Id, request);
            
            if (response?.IsSuccess == true)
            {
                AnsiConsole.MarkupLine($"[green]✓[/] Asset [cyan]{asset.AssetTag}[/] audited successfully");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]✗[/] Failed to audit asset [cyan]{asset.AssetTag}[/]");
                if (!string.IsNullOrEmpty(response?.Messages))
                {
                    AnsiConsole.MarkupLine($"[red]{response.Messages}[/]");
                }
            }
        }, assetArg, locationOption, noteOption);
        
        return command;
    }
    
    #endregion
    
    #region Helpers
    
    private static string GetStatusMarkup(SnipeStatusLabel? status)
    {
        if (status == null) return "[dim]-[/]";
        
        var color = status.StatusMeta switch
        {
            "deployable" => "green",
            "deployed" => "blue",
            "pending" => "yellow",
            "archived" => "dim",
            "undeployable" => "red",
            _ => "white"
        };
        
        return $"[{color}]{status.Name}[/]";
    }
    
    #endregion
}
