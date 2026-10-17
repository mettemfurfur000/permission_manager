using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using PermissionManagerApi;
using permission_manager.src.utils;

namespace permission_manager.src.gating;

/// <summary>On disk shape of <c>commands.json</c>.</summary>
public sealed class CommandMapData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Command name (lower case, usually <c>css_*</c>) to the node required to run it.</summary>
    [JsonPropertyName("commands")]
    public Dictionary<string, string> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Blocks gated commands before they reach their handler.
///
/// <para>Two interception strategies, both installed in <c>Load</c>:</para>
/// <list type="number">
/// <item>
/// <b>Explicit registration.</b> Another plugin calls
/// <see cref="PermissionManagerPlugin.RegisterGatedCommand"/>. A dedicated
/// <see cref="BasePlugin.AddCommandListener"/> is installed for that command name.
/// </item>
/// <item>
/// <b>Command map (opt in).</b> With <c>EnableCommandMap</c> the whole of
/// <c>commands.json</c> is gated through a single global listener plus one listener per
/// mapped command. The global listener is a dictionary lookup and returns immediately for
/// commands nobody cares about, so the cost on unmapped commands is negligible.
/// </item>
/// </list>
///
/// <para>Denials return <see cref="HookResult.Handled"/>, which stops CounterStrikeSharp
/// before the command callback runs. Permitted commands return
/// <see cref="HookResult.Continue"/> and are left completely alone.</para>
///
/// <para>Everything is tracked by command name and torn down in <c>Unload</c>, so hot
/// reloading never leaves a stale listener or a duplicate gate behind.</para>
/// </summary>
public sealed class CommandGate
{
    private readonly PermissionManagerPlugin _plugin;
    private readonly Dictionary<string, string> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CommandInfo.CommandListenerCallback> _listeners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Commands that came from <c>commands.json</c>. Only these are pruned when a line is
    /// removed from the map file, so a gate that arrived some other way is only ever removed
    /// by an explicit <see cref="Unregister"/>.
    ///
    /// <para><b>Note.</b> <c>commands.json</c> is the full persisted gate table: unloading
    /// the plugin writes every gate, including ones plugins registered in code, so they
    /// survive a reload. The practical consequence is that deleting a line from the file
    /// un-gates that command until its owning plugin registers it again (on its next load).</para>
    /// </summary>
    private readonly HashSet<string> _fromMap = new(StringComparer.OrdinalIgnoreCase);

    private CommandInfo.CommandListenerCallback? _globalListener;
    private bool _mapEnabled;

    // Both the global listener and the per command listener can see the same dispatch when
    // the native layer does not stop after Handled. Remembering the last denial for the
    // current tick keeps that from producing two chat messages for one command.
    private long _tick;
    private nint _lastDeniedHandle;
    private string _lastDeniedCommand = string.Empty;
    private long _lastDeniedTick = -1;

    public CommandGate(PermissionManagerPlugin plugin)
    {
        _plugin = plugin;
    }

    public string FilePath { get; set; } = string.Empty;

    /// <summary>True while the global listener is installed.</summary>
    public bool MapEnabled => _mapEnabled;

    /// <summary>Raised after the gate table changed.</summary>
    public event EventHandler<PermissionsChangedEventArgs>? GateChanged;

    /// <summary>Raised when a gated command was blocked. The plugin re-raises it publicly.</summary>
    public event EventHandler<PermissionDeniedEventArgs>? Denied;

    /// <summary>Snapshot of the gate table.</summary>
    public IReadOnlyDictionary<string, string> Gates => new Dictionary<string, string>(_gates, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the command came from <c>commands.json</c> rather than plugin code.</summary>
    public bool IsFromMap(string command) => _fromMap.Contains(NodeMatcher.Normalize(command));

    // ------------------------------------------------------------- lifecycle

    /// <summary>
    /// Installs the global listener when enabled and re-registers a listener for every
    /// command that is currently mapped. Safe to call again after <see cref="Reset"/>.
    /// </summary>
    public void Install(bool commandMapEnabled)
    {
        _mapEnabled = commandMapEnabled;

        if (_mapEnabled && _globalListener is null)
        {
            CommandInfo.CommandListenerCallback global = OnGlobalCommand;
            _globalListener = global;

            // A null command name installs the catch-all Pre hook in front of every command.
            _plugin.AddCommandListener(null, global, HookMode.Pre);
        }
        else if (!_mapEnabled && _globalListener is not null)
        {
            _plugin.RemoveCommandListener(null!, _globalListener, HookMode.Pre);
            _globalListener = null;
        }

        foreach (string command in _gates.Keys)
            EnsureListener(command);
    }

    /// <summary>Removes every listener this gate installed and clears the table.</summary>
    public void Reset()
    {
        foreach ((string command, CommandInfo.CommandListenerCallback listener) in _listeners)
            _plugin.RemoveCommandListener(command, listener, HookMode.Pre);

        _listeners.Clear();
        _fromMap.Clear();

        if (_globalListener is not null)
        {
            _plugin.RemoveCommandListener(null!, _globalListener, HookMode.Pre);
            _globalListener = null;
        }

        _gates.Clear();
        _mapEnabled = false;
        _lastDeniedTick = -1;
    }

    /// <summary>Called once per tick so the duplicate denial guard can expire.</summary>
    public void Tick() => _tick++;

    // ---------------------------------------------------------------- gating

    /// <summary>
    /// Adds or refreshes a gate. Re-registering the same command with the same node is a
    /// no-op, which is what makes it safe for a plugin to call this from its own <c>Load</c>
    /// on every hot reload.
    /// </summary>
    public bool Register(string command, string node)
    {
        string name = NodeMatcher.Normalize(command);
        string value = NodeMatcher.NormalizePattern(node);

        if (name.Length == 0 || value.Length == 0)
            return false;

        if (_gates.TryGetValue(name, out string? existing) && string.Equals(existing, value, StringComparison.Ordinal))
            return true;

        _gates[name] = value;
        EnsureListener(name);

        GateChanged?.Invoke(this, new PermissionsChangedEventArgs(PermissionsChangeKind.CommandGate, $"{name} -> {value}"));

        return true;
    }

    public bool Unregister(string command)
    {
        string name = NodeMatcher.Normalize(command);

        if (name.Length == 0 || !_gates.Remove(name))
            return false;

        _fromMap.Remove(name);

        if (_listeners.Remove(name, out CommandInfo.CommandListenerCallback? listener))
            _plugin.RemoveCommandListener(name, listener, HookMode.Pre);

        GateChanged?.Invoke(this, new PermissionsChangedEventArgs(PermissionsChangeKind.CommandGate, name));

        return true;
    }

    /// <summary>The node a command is gated behind, or <c>null</c>.</summary>
    public string? NodeFor(string command)
    {
        return _gates.TryGetValue(NodeMatcher.Normalize(command), out string? node) ? node : null;
    }

    public bool IsGated(string command) => _gates.ContainsKey(NodeMatcher.Normalize(command));

    private void EnsureListener(string command)
    {
        if (_listeners.ContainsKey(command))
            return;

        // One closure per command name so CounterStrikeSharp can key and unkey it.
        CommandInfo.CommandListenerCallback listener = (player, info) => OnGatedCommand(command, player, info);

        _listeners[command] = listener;
        _plugin.AddCommandListener(command, listener, HookMode.Pre);
    }

    // -------------------------------------------------------------- handlers

    private HookResult OnGatedCommand(string command, CCSPlayerController? player, CommandInfo info)
    {
        if (!_gates.TryGetValue(command, out string? node))
            return HookResult.Continue;

        return Evaluate(command, node, player, info);
    }

    private HookResult OnGlobalCommand(CCSPlayerController? player, CommandInfo info)
    {
        // Cheap path first: one dictionary lookup keyed by the raw command word.
        string command = NodeMatcher.Normalize(info.GetArg(0));

        if (command.Length == 0 || !_gates.TryGetValue(command, out string? node))
            return HookResult.Continue;

        return Evaluate(command, node, player, info);
    }

    private HookResult Evaluate(string command, string node, CCSPlayerController? player, CommandInfo info)
    {
        if (AlreadyReported(info, command))
            return HookResult.Handled;

        if (_plugin.HasPermission(player, node))
            return HookResult.Continue;

        ReportDenied(command, node, player);
        MarkReported(info, command);

        return HookResult.Handled;
    }

    private bool AlreadyReported(CommandInfo info, string command)
    {
        return _lastDeniedTick == _tick
            && _lastDeniedHandle == info.Handle
            && string.Equals(_lastDeniedCommand, command, StringComparison.OrdinalIgnoreCase);
    }

    private void MarkReported(CommandInfo info, string command)
    {
        _lastDeniedTick = _tick;
        _lastDeniedHandle = info.Handle;
        _lastDeniedCommand = command;
    }

    private void ReportDenied(string command, string node, CCSPlayerController? player)
    {
        ulong steamId = _plugin.SteamIdOf(player);

        if (_plugin.Config.DenyToChat && player is not null)
        {
            _plugin.ReplyToPlayer(player, _plugin.Config.DenialMessage, command, node);
        }

        if (_plugin.Config.DenyToConsole)
        {
            Server.PrintToConsole(
                $"[permission_manager] denied '{command}' for {TargetResolver.NameOf(player)} ({steamId}), requires '{node}'");
        }

        if (_plugin.Config.LogDenials)
        {
            _plugin.LogWarn($"denied '{command}' for {TargetResolver.NameOf(player)} ({steamId}), requires '{node}'");
        }

        Denied?.Invoke(this, new PermissionDeniedEventArgs(player, steamId, command, node));
    }

    // -------------------------------------------------------------------- io

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Reads <c>commands.json</c> and gates everything in it. Missing file means an empty map.
    /// </summary>
    public bool LoadMap()
    {
        if (FilePath.Length == 0)
            return false;

        try
        {
            string? directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(FilePath))
            {
                SaveMap();
                return true;
            }

            string json = File.ReadAllText(FilePath);

            CommandMapData data = string.IsNullOrWhiteSpace(json)
                ? new CommandMapData()
                : JsonSerializer.Deserialize<CommandMapData>(json, ReadOptions) ?? new CommandMapData();

            // Hand edited files can legitimately contain "commands": null.
            data.Commands ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!_mapEnabled)
            {
                // The map is opt in. Gates a plugin registered in code keep working, but
                // nothing from commands.json is enforced and previously applied entries are
                // dropped so turning the switch off really does mean off.
                List<string> stale = _fromMap.ToList();

                foreach (string command in stale)
                    Unregister(command);

                _plugin.LogInfo(
                    stale.Count > 0
                        ? $"command map disabled, stopped gating {stale.Count} command(s) from '{FilePath}'"
                        : $"command map disabled, '{FilePath}' ignored");

                return true;
            }

            int applied = 0;
            int pruned = 0;

            // Drop gates that came from the map and are no longer in it, but leave anything a
            // plugin registered in code alone.
            List<string> removed = _fromMap
                .Where(command => !data.Commands.ContainsKey(command))
                .ToList();

            foreach (string command in removed)
            {
                if (Unregister(command))
                    pruned++;
            }

            foreach ((string key, string? value) in data.Commands)
            {
                if (Register(key, value ?? string.Empty))
                {
                    _fromMap.Add(NodeMatcher.Normalize(key));
                    applied++;
                }
            }

            if (pruned > 0)
                _plugin.LogInfo($"removed {pruned} gate(s) that were deleted from the command map");

            _plugin.LogInfo($"loaded {applied} gated command(s) from '{FilePath}'");

            return true;
        }
        catch (Exception ex)
        {
            _plugin.LogError($"failed to read '{FilePath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>Writes the current gate table back to <c>commands.json</c>.</summary>
    public bool SaveMap()
    {
        if (FilePath.Length == 0)
            return false;

        try
        {
            string? directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Sorted output keeps diffs of the file readable.
            string json = JsonSerializer.Serialize(
                new SortedDictionary<string, string>(_gates, StringComparer.OrdinalIgnoreCase),
                WriteOptions);

            File.WriteAllText(FilePath,
                "{\n  \"version\": 1,\n  \"commands\": " + json + "\n}\n");

            return true;
        }
        catch (Exception ex)
        {
            _plugin.LogError($"failed to write '{FilePath}': {ex.Message}");
            return false;
        }
    }
}