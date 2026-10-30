using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Commands;

namespace permission_manager.src.utils;

/// <summary>
/// Reflectively enumerates the commands every loaded plugin registered with
/// CounterStrikeSharp. There is no public API for this, so we reach into the
/// (private) global <c>CommandManager</c> via reflection.
/// </summary>
public sealed record DiscoveredCommand(
    string Command,
    string Description,
    string ExecutableBy,
    string Owner);

public sealed class DiscoveredCommandFile
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O");

    [JsonPropertyName("commands")]
    public Dictionary<string, string> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class CommandDiscovery
{
    private static readonly Type? DecoratorType =
        Type.GetType("CounterStrikeSharp.API.Core.Commands.PluginCommandManagerDecorator, CounterStrikeSharp.API");

    private static readonly Type? GlobalManagerType =
        Type.GetType("CounterStrikeSharp.API.Core.Commands.CommandManager, CounterStrikeSharp.API");

    private static readonly FieldInfo? InnerField =
        DecoratorType?.GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? DefinitionsField =
        GlobalManagerType?.GetField("_commandDefinitions", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Checks that the CounterStrikeSharp internals we depend on still exist.
    /// Call this before a scan so a CSS version drift produces a clear message
    /// instead of a silent empty result.
    /// </summary>
    public static bool Preflight(out string? error)
    {
        if (DecoratorType is null)
        {
            error = "CounterStrikeSharp internals changed: PluginCommandManagerDecorator type not found.";
            return false;
        }

        if (GlobalManagerType is null)
        {
            error = "CounterStrikeSharp internals changed: CommandManager type not found.";
            return false;
        }

        if (InnerField is null)
        {
            error = "CounterStrikeSharp internals changed: PluginCommandManagerDecorator._inner field not found.";
            return false;
        }

        if (DefinitionsField is null)
        {
            error = "CounterStrikeSharp internals changed: CommandManager._commandDefinitions field not found.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryScan(ICommandManager commandManager, out List<DiscoveredCommand> commands, out string? error)
    {
        commands = new List<DiscoveredCommand>();

        if (!Preflight(out error))
            return false;

        object? global = commandManager;
        if (global is not null && global.GetType() != GlobalManagerType)
        {
            global = InnerField!.GetValue(global);
        }

        if (global is null || global.GetType() != GlobalManagerType)
        {
            error = "Could not reach the global CommandManager instance (unexpected wrapper type).";
            return false;
        }

        object? raw = DefinitionsField!.GetValue(global);
        if (raw is not IDictionary dictionary)
        {
            error = "CounterStrikeSharp internals changed: _commandDefinitions is not a dictionary.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in dictionary)
        {
            string command = Convert.ToString(entry.Key) ?? string.Empty;
            if (command.Length == 0 || !seen.Add(command))
                continue;

            if (entry.Value is not IEnumerable list)
                continue;

            foreach (object? item in list)
            {
                if (item is not CommandDefinition definition)
                    continue;

                string owner = definition.Callback?.Target?.GetType().FullName ?? string.Empty;
                string executableBy = definition.ExecutableBy.ToString();

                commands.Add(new DiscoveredCommand(
                    definition.Name.Length == 0 ? command : definition.Name,
                    definition.Description ?? string.Empty,
                    executableBy,
                    owner));
            }
        }

        commands.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Command, b.Command));

        if (commands.Count == 0)
        {
            error = "Scan completed but found 0 commands. Plugins may not have registered yet, or CSS internals changed.";
            return false;
        }

        error = null;
        return true;
    }
}