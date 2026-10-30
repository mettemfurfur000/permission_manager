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
    string Owner,
    string Author,
    string Plugin,
    string SuggestedNode);

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

                var (author, pluginName) = ResolvePluginIdentity(definition.Callback?.Target, definition.Callback?.Method.DeclaringType);
                string resolvedCommand = definition.Name.Length == 0 ? command : definition.Name;
                string suggestedNode = BuildSuggestedNode(author, pluginName, resolvedCommand);

                commands.Add(new DiscoveredCommand(
                    resolvedCommand,
                    definition.Description ?? string.Empty,
                    executableBy,
                    owner,
                    author,
                    pluginName,
                    suggestedNode));
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

    private static (string Author, string Plugin) ResolvePluginIdentity(object? target, Type? declaringType)
    {
        BasePlugin? plugin = target as BasePlugin;

        if (plugin is null && target is not null)
        {
            foreach (FieldInfo field in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.GetValue(target) is BasePlugin candidate)
                {
                    plugin = candidate;
                    break;
                }
            }
        }

        if (plugin is not null)
        {
            string author = NormalizeSegment(plugin.ModuleAuthor);
            string name = NormalizeSegment(plugin.ModuleName);

            if (author.Length == 0)
                author = "unknown";
            if (name.Length == 0)
                name = NormalizeSegment(plugin.GetType().Name);

            return (author, name);
        }

        Type type = declaringType ?? target?.GetType() ?? typeof(object);
        string fallbackAuthor = NormalizeSegment(type.Namespace);
        if (fallbackAuthor.Length == 0)
            fallbackAuthor = NormalizeSegment(type.Assembly.GetName().Name);
        if (fallbackAuthor.Length == 0)
            fallbackAuthor = "unknown";

        string fallbackPlugin = NormalizeSegment(type.Name);
        if (fallbackPlugin.Length == 0)
            fallbackPlugin = "plugin";

        // Trim a trailing "Plugin" suffix noise is intentionally kept: the type name is
        // the most stable identifier available without an instance.

        return (fallbackAuthor, fallbackPlugin);
    }

    private static string BuildSuggestedNode(string author, string plugin, string command)
    {
        string a = NormalizeSegment(author);
        string p = NormalizeSegment(plugin);
        string c = NormalizeSegment(command);

        if (a.Length == 0)
            a = "unknown";
        if (p.Length == 0)
            p = "plugin";
        if (c.Length == 0)
            c = "command";

        return $"{a}.{p}.{c}";
    }

    private static string NormalizeSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        string trimmed = value.Trim().ToLowerInvariant();
        var chars = new System.Text.StringBuilder(trimmed.Length);

        foreach (char ch in trimmed)
        {
            if (char.IsWhiteSpace(ch))
                chars.Append('_');
            else
                chars.Append(ch);
        }

        return chars.ToString();
    }
}