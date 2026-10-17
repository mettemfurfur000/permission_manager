using System;
using System.Collections.Generic;
using System.IO;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using permission_manager.src.gating;
using permission_manager.src.storage;
using permission_manager.src.utils;
using PermissionManagerApi;
using Timer = CounterStrikeSharp.API.Modules.Timers.Timer;

namespace permission_manager.src;

/// <summary>
/// Centralised permission manager: one JSON backed store of groups, users and nodes, plus a
/// command gate that blocks gated commands before their handlers run.
///
/// <para>Consumers reach it through <see cref="PermissionApi"/> or the
/// <c>tem_perm:service</c> plugin capability. Nothing in here needs to be touched by the
/// plugins being gated.</para>
/// </summary>
public partial class PermissionManagerPlugin : BasePlugin, IPluginConfig<PermissionManagerConfig>, IPermissionService
{
    private PermissionStore _store = null!;
    private CommandGate _gate = null!;
    private Timer? _saveTimer;

    /// <summary>Plugin capability other plugins can resolve, mirrors <see cref="PermissionApi"/>.</summary>
    public static PluginCapability<IPermissionService> Capability { get; } = new(PermissionApi.CapabilityName);

    public override string ModuleName => "permission_manager";
    public override string ModuleVersion => "0.1.0";
    public override string ModuleAuthor => "tem";

    public PermissionManagerConfig Config { get; set; } = new();

    /// <summary>Folder holding <c>permissions.json</c> and <c>commands.json</c>.</summary>
    public string DataDirectory { get; private set; } = string.Empty;

    /// <summary>The command gate. Public so tests and commands can inspect it.</summary>
    public CommandGate Gate => _gate;

    public bool IsReady { get; private set; }

    // -------------------------------------------------------------- lifecycle

    public void OnConfigParsed(PermissionManagerConfig config)
    {
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        // A previous instance may still be registered when the framework reloads in place.
        PermissionApi.Unregister(this);
        PermissionsChanged = null;
        PermissionDenied = null;

        DataDirectory = ResolveDataDirectory(Config.DataDirectory);

        // The store creates the directory itself and degrades to defaults if that fails, so
        // there is no half-initialised state to guard every later call against.
        _store = new PermissionStore(
            Path.Combine(DataDirectory, "permissions.json"),
            Config.DefaultGroup,
            LogWarn,
            LogError);

        _gate = new CommandGate(this) { FilePath = Path.Combine(DataDirectory, "commands.json") };
        _gate.Denied += OnGateDenied;
        _gate.GateChanged += OnGateChanged;

        _store.Changed += (_, args) => PermissionsChanged?.Invoke(this, args);

        if (!_store.Load())
            LogWarn("running on default (empty) permission data, see the log above for the reason.");

        _gate.Install(Config.EnableCommandMap);
        _gate.LoadMap();

        _saveTimer = AddTimer(
            Math.Max(0.5f, Config.SaveDebounceSeconds),
            FlushIfDirty,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Capabilities.RegisterPluginCapability(Capability, () => this);
        PermissionApi.Register(this);

        RegisterListener<Listeners.OnTick>(() => _gate.Tick());

        // Warms the snapshot so the first command of a session is already resolved.
        RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
        {
            ulong? steamId = TargetResolver.SteamIdOf(@event.Userid);

            if (steamId is not null)
                _store.GetSnapshot(steamId.Value);

            return HookResult.Continue;
        });

        IsReady = true;

        LogInfo(
            $"v{ModuleVersion} ready. Data: '{DataDirectory}'. Command map {MapState()}. "
            + $"{_store.GetGroupNames().Count} group(s), {_store.GetAssignedUserCount()} user(s), {_gate.Gates.Count} gated command(s).");
    }

    public override void Unload(bool hotReload)
    {
        IsReady = false;

        if (_gate is not null)
        {
            // Persist anything admins changed through css_perm_* before the store dies.
            _store?.Save();
            _gate.SaveMap();

            _gate.Denied -= OnGateDenied;
            _gate.GateChanged -= OnGateChanged;
            _gate.Reset();
        }

        if (_saveTimer is not null)
        {
            _saveTimer.Kill();
            _saveTimer = null;
        }

        PermissionApi.Unregister(this);
    }

    /// <summary>
    /// Default data folder sits next to the plugin config:
    /// <c>addons/counterstrikesharp/configs/plugins/permission_manager</c>.
    /// </summary>
    private string ResolveDataDirectory(string configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.IsPathRooted(configured)
                ? configured
                : Path.GetFullPath(Path.Combine(Server.GameDirectory, configured));
        }

        // ModuleDirectory is <game>/addons/counterstrikesharp/plugins/permission_manager
        string? moduleDirectory = ModuleDirectory;

        if (!string.IsNullOrEmpty(moduleDirectory))
        {
            string? counterStrikeSharp = Directory.GetParent(moduleDirectory)?.Parent?.FullName;

            if (!string.IsNullOrEmpty(counterStrikeSharp))
                return Path.Combine(counterStrikeSharp, "configs", "plugins", ModuleName);
        }

        return Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "configs", "plugins", ModuleName);
    }

    private void FlushIfDirty()
    {
        if (_store is not null && _store.IsDirty)
            _store.Save();
    }

    private void OnGateDenied(object? sender, PermissionDeniedEventArgs args)
    {
        PermissionDenied?.Invoke(this, args);
    }

    private void OnGateChanged(object? sender, PermissionsChangedEventArgs args)
    {
        PermissionsChanged?.Invoke(this, args);
    }

    // ------------------------------------------------------------------ events

    public event EventHandler<PermissionDeniedEventArgs>? PermissionDenied;

    public event EventHandler<PermissionsChangedEventArgs>? PermissionsChanged;

    // ------------------------------------------------------------------ checks

    public bool HasPermission(CCSPlayerController? player, string node)
    {
        if (string.IsNullOrWhiteSpace(node))
            return true;

        if (player is null || !player.IsValid)
            return Config.AllowConsole;

        if (player.IsBot || player.IsHLTV)
            return Config.AllowBots;

        ulong? steamId = TargetResolver.SteamIdOf(player);

        // Not authorised yet (still connecting, or a private Steam profile). Falling back to
        // the default policy keeps such players from being silently locked out.
        if (steamId is null)
            return !Config.DefaultDeny;

        if (HasPermission(steamId.Value, node))
            return true;

        // Strict mode only: let CounterStrikeSharp's own admin flags act as an override so
        // the server owner does not have to be mirrored into permissions.json.
        return Config.DefaultDeny
            && Config.UseCounterStrikeSharpAdmins
            && AdminManager.PlayerHasPermissions(player, node, PermissionNodes.CssRoot);
    }

    public bool HasPermission(ulong steamId, string node)
    {
        if (string.IsNullOrWhiteSpace(node))
            return true;

        if (steamId == TargetResolver.ConsoleSteamId)
            return Config.AllowConsole;

        PermissionResolution resolution = _store.Resolve(steamId, node);

        return Effective(resolution);
    }

    /// <summary>
    /// Turns a raw resolution into the answer the gate acts on. A node that matched nothing
    /// falls through to the configured default, which is *allow* unless
    /// <see cref="PermissionManagerConfig.DefaultDeny"/> is on.
    /// </summary>
    public bool Effective(PermissionResolution resolution)
    {
        return resolution.Explicit ? resolution.Allowed : !Config.DefaultDeny;
    }

    public PermissionResolution Resolve(ulong steamId, string node)
    {
        return _store.Resolve(steamId, node);
    }

    public bool InGroup(CCSPlayerController? player, string group)
    {
        if (string.IsNullOrWhiteSpace(group))
            return false;

        if (player is null)
            return false;

        ulong? steamId = TargetResolver.SteamIdOf(player);

        return steamId is not null && _store.IsInGroup(steamId.Value, group);
    }

    public bool InGroup(ulong steamId, string group) => _store.IsInGroup(steamId, group);

    public IReadOnlyList<string> GetUserGroups(ulong steamId) => _store.GetAssignedGroups(steamId);

    public IReadOnlyList<string> GetUserNodes(ulong steamId) => _store.GetAssignedNodes(steamId);

    public IReadOnlyCollection<string> GetGroupNames() => _store.GetGroupNames();

    public IReadOnlyList<string> GetGroupEntries(string group) => _store.GetGroupEntries(group);

    // --------------------------------------------------------------- mutations

    public bool GrantNode(ulong steamId, string node) => _store.GrantNode(steamId, node);

    public bool RevokeNode(ulong steamId, string node) => _store.RevokeNode(steamId, node);

    public bool GrantGroup(ulong steamId, string group) => _store.GrantGroup(steamId, group);

    public bool RevokeGroup(ulong steamId, string group) => _store.RevokeGroup(steamId, group);

    public bool EnsureGroup(string group) => _store.EnsureGroup(group);

    public bool GrantGroupNode(string group, string node) => _store.GrantGroupNode(group, node);

    public bool RevokeGroupNode(string group, string node) => _store.RevokeGroupNode(group, node);

    // ---------------------------------------------------------- command gating

    public bool RegisterGatedCommand(string command, string node) => _gate.Register(command, node);

    public bool UnregisterGatedCommand(string command) => _gate.Unregister(command);

    public IReadOnlyDictionary<string, string> GatedCommands => _gate.Gates;

    // ---------------------------------------------------------------- lifecycle

    public bool Reload()
    {
        // Pick up a DefaultGroup change too, so css_perm_reload covers every config value
        // that affects resolution.
        string defaultGroup = NodeMatcher.Normalize(Config.DefaultGroup);
        _store.DefaultGroup = defaultGroup.Length == 0 ? "default" : defaultGroup;

        bool permissions = _store.Reload();
        bool map = _gate.LoadMap();

        _gate.Install(Config.EnableCommandMap);

        PermissionsChanged?.Invoke(this, new PermissionsChangedEventArgs(PermissionsChangeKind.Reloaded));

        return permissions && map;
    }

    public void Save()
    {
        _store.Save();
        _gate.SaveMap();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>SteamID64 of a player, or <see cref="TargetResolver.ConsoleSteamId"/> for console.</summary>
    public ulong SteamIdOf(CCSPlayerController? player) => TargetResolver.SteamIdOf(player) ?? TargetResolver.ConsoleSteamId;

    /// <summary>Sends a rendered template to a player.</summary>
    public void ReplyToPlayer(CCSPlayerController player, string template, string command = "", string node = "", string group = "")
    {
        string message = ChatFormat.Render(template, command, node, group, player);

        if (message.Length == 0)
            return;

        try
        {
            player.PrintToChat(message);
        }
        catch
        {
            // Player left between the check and the print. Nothing to do.
        }
    }

    /// <summary>Coloured helper for command output that does not come from a template.</summary>
    public void Reply(CommandInfo info, string message) => info.ReplyToCommand(message);

    /// <summary>Startup and status output.</summary>
    public void LogInfo(string message) => Server.PrintToConsole($"permission_manager: {message}");

    /// <summary>Console output for something an admin should look at.</summary>
    public void LogWarn(string message) => Server.PrintToConsole($"permission_manager: WARN {message}");

    /// <summary>Console output for a failure the plugin worked around.</summary>
    public void LogError(string message) => Server.PrintToConsole($"permission_manager: ERROR {message}");

    /// <summary>Human readable state of the global command listener, for startup and status output.</summary>
    public string MapState() => Config.EnableCommandMap ? "enabled" : "disabled";

    /// <summary>True when the caller may run the <c>css_perm_*</c> administration commands.</summary>
    public bool CanAdminister(CCSPlayerController? caller)
    {
        if (caller is null || !caller.IsValid)
            return Config.AllowConsoleAdmin;

        return HasPermission(caller, PermissionNodes.Admin);
    }

    /// <summary>Coloured prefix for administration command replies.</summary>
    public static string Colored(char color, string message) => $" {color}{message}";
}