using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FleetMate.Commands.Shared;

/// <summary>
/// The whole command tree as JSON, for `fleetmate --experimental-dump-help`:
/// every command, positional, option and flag, in the same shape the Mac
/// CLI's argument parser prints, so the agent brief FleetMate generates from
/// it reads the same on both platforms. Hidden commands and options are kept
/// but marked, and the brief leaves them out.
/// </summary>
public static class HelpDump
{
    public const string Flag = "--experimental-dump-help";

    public static string Serialize(Command root) =>
        new JsonObject
        {
            ["serializationVersion"] = 0,
            ["command"] = Describe(root),
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static JsonObject Describe(Command command)
    {
        var node = new JsonObject
        {
            ["commandName"] = command.Name,
            ["shouldDisplay"] = !command.IsHidden,
        };
        if (!string.IsNullOrWhiteSpace(command.Description)) node["abstract"] = command.Description;

        var arguments = new JsonArray();
        foreach (var argument in command.Arguments) arguments.Add(Describe(argument));
        foreach (var option in command.Options) arguments.Add(Describe(option));
        if (arguments.Count > 0) node["arguments"] = arguments;

        if (command.Subcommands.Count > 0)
        {
            var subcommands = new JsonArray();
            foreach (var sub in command.Subcommands) subcommands.Add(Describe(sub));
            node["subcommands"] = subcommands;
        }
        return node;
    }

    private static JsonObject Describe(Argument argument)
    {
        var node = new JsonObject
        {
            ["kind"] = "positional",
            ["shouldDisplay"] = !argument.IsHidden,
            ["isOptional"] = argument.Arity.MinimumNumberOfValues == 0,
            ["isRepeating"] = argument.Arity.MaximumNumberOfValues > 1,
            ["valueName"] = string.IsNullOrWhiteSpace(argument.HelpName) ? argument.Name : argument.HelpName,
        };
        if (!string.IsNullOrWhiteSpace(argument.Description)) node["abstract"] = argument.Description;
        AddValues(node, argument, argument.ValueType, argument.HasDefaultValue ? argument.GetDefaultValue() : null);
        return node;
    }

    private static JsonObject Describe(Option option)
    {
        var isFlag = option.ValueType == typeof(bool) && option.Arity.MinimumNumberOfValues == 0;
        var names = new JsonArray();
        foreach (var alias in option.Aliases.OrderBy(a => a.Length)) names.Add(Name(alias));
        var node = new JsonObject
        {
            ["kind"] = isFlag ? "flag" : "option",
            ["shouldDisplay"] = !option.IsHidden,
            ["isOptional"] = !option.IsRequired,
            ["isRepeating"] = option.Arity.MaximumNumberOfValues > 1,
            ["names"] = names,
            ["preferredName"] = Name(option.Aliases.OrderByDescending(a => a.Length).First()),
            ["valueName"] = string.IsNullOrWhiteSpace(option.ArgumentHelpName) ? option.Name : option.ArgumentHelpName,
        };
        if (!string.IsNullOrWhiteSpace(option.Description)) node["abstract"] = option.Description;
        // Option implements the value descriptor explicitly in this System.CommandLine.
        var descriptor = (System.CommandLine.Binding.IValueDescriptor)option;
        if (!isFlag) AddValues(node, option, option.ValueType, descriptor.HasDefaultValue ? descriptor.GetDefaultValue() : null);
        return node;
    }

    /// <summary>The allowed values (a fixed list or an enum) and the default, where there are any.</summary>
    private static void AddValues(JsonObject node, Symbol symbol, Type valueType, object? defaultValue)
    {
        if (valueType != typeof(bool))
        {
            var values = new JsonArray();
            foreach (var item in symbol.GetCompletions()) values.Add(item.Label);
            if (values.Count > 0) node["allValues"] = values;
        }
        var text = defaultValue switch
        {
            null => null,
            string s => s,
            bool b => b ? "true" : "false",
            System.Collections.IEnumerable e => string.Join(" ", e.Cast<object>()),
            _ => defaultValue.ToString(),
        };
        if (!string.IsNullOrEmpty(text)) node["defaultValue"] = text;
    }

    /// <summary>"--json" as {kind: long, name: json}; "-j" as short; "-json" as longWithSingleDash.</summary>
    private static JsonObject Name(string alias)
    {
        if (alias.StartsWith("--")) return new JsonObject { ["kind"] = "long", ["name"] = alias[2..] };
        if (alias.StartsWith('-') && alias.Length == 2) return new JsonObject { ["kind"] = "short", ["name"] = alias[1..] };
        if (alias.StartsWith('-')) return new JsonObject { ["kind"] = "longWithSingleDash", ["name"] = alias[1..] };
        return new JsonObject { ["kind"] = "long", ["name"] = alias };
    }
}
