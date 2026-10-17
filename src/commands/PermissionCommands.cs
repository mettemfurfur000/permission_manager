using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using permission_manager.src.utils;
using PermissionManagerApi;

namespace permission_manager.src;

/// <summary>
/// Administration commands. Split into its own file purely for readability; they are
/// instance methods of the plugin because that is where the config, store and gate live.
///
/// <para>None of these use <see cref="RequiresPermissions"/> on purpose. That attribute is
/// answered by CounterStrikeSharp's own admin system, which would put a second, competing
/// permission system in front of ours. Guarding in the handler keeps
/// <see cref="PermissionNodes.Admin"/> as the single source of truth while still letting
/// the console through.</para>
/// </summary>
public partial class PermissionManagerPlugin
{
    private const string Usage =
        " | target: name, @all, #ct, #t, or a SteamID64 (offline players allowed)";

    // -------------------------------------------------------------- node grants

    [ConsoleCommand("css_perm_grant", "Give a player a permission node")]
    [CommandHelper(minArgs: 2, usage: "(player) (node)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGrantNode(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);
        string node = info.GetArg(2);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        int changed = 0;

        foreach (ulong steamId in targets)
            changed += GrantNode(steamId, node) ? 1 : 0;

        ReportMutation(info, online, targets.Count, changed, $"node '{node}'");
    }

    [ConsoleCommand("css_perm_revoke", "Take a permission node away from a player")]
    [CommandHelper(minArgs: 2, usage: "(player) (node)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnRevokeNode(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);
        string node = info.GetArg(2);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        int changed = 0;

        foreach (ulong steamId in targets)
            changed += RevokeNode(steamId, node) ? 1 : 0;

        ReportMutation(info, online, targets.Count, changed, $"node '{node}'");
    }

    // ---------------------------------------------------------- group commands

    [ConsoleCommand("css_perm_group_add", "Add a player to a group")]
    [CommandHelper(minArgs: 2, usage: "(player) (group)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGroupAdd(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);
        string group = info.GetArg(2);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        EnsureGroup(group);

        int changed = 0;

        foreach (ulong steamId in targets)
            changed += GrantGroup(steamId, group) ? 1 : 0;

        ReportMutation(info, online, targets.Count, changed, $"group '{group}'");
    }

    [ConsoleCommand("css_perm_group_remove", "Remove a player from a group")]
    [CommandHelper(minArgs: 2, usage: "(player) (group)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGroupRemove(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);
        string group = info.GetArg(2);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        int changed = 0;

        foreach (ulong steamId in targets)
            changed += RevokeGroup(steamId, group) ? 1 : 0;

        ReportMutation(info, online, targets.Count, changed, $"group '{group}'");
    }

    [ConsoleCommand("css_perm_group_create", "Create a group, optionally with initial nodes")]
    [CommandHelper(minArgs: 1, usage: "(group) [node ...]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGroupCreate(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        string group = info.GetArg(1);
        bool created = EnsureGroup(group);

        int nodes = 0;

        for (int i = 2; i < info.ArgCount; i++)
            nodes += GrantGroupNode(group, info.GetArg(i)) ? 1 : 0;

        Reply(info, created
            ? Colored(ChatColors.Green, $"Created group '{group}'") + (nodes > 0 ? $" with {nodes} node(s)." : ".")
            : Colored(ChatColors.Yellow, $"Group '{group}' already exists") + (nodes > 0 ? $", added {nodes} node(s)." : "."));
    }

    [ConsoleCommand("css_perm_group_delete", "Delete a group and drop it from every user")]
    [CommandHelper(minArgs: 1, usage: "(group)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGroupDelete(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        string group = NodeMatcher.Normalize(info.GetArg(1));

        if (group == NodeMatcher.Normalize(Config.DefaultGroup))
        {
            Reply(info, Colored(ChatColors.Red, $"'{info.GetArg(1)}' is the default group and cannot be deleted."));
            return;
        }

        Reply(info, _store.DeleteGroup(group)
            ? Colored(ChatColors.Green, $"Deleted group '{group}'.")
            : Colored(ChatColors.Yellow, $"No such group '{group}'."));
    }

    [ConsoleCommand("css_perm_group_node", "Add or remove a node on a group")]
    [CommandHelper(minArgs: 3, usage: "(add|remove) (group) (node)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGroupNode(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        string action = info.GetArg(1);
        string group = info.GetArg(2);
        string node = info.GetArg(3);

        bool isAdd = action.Equals("add", StringComparison.OrdinalIgnoreCase);
        bool isRemove = action.Equals("remove", StringComparison.OrdinalIgnoreCase);

        if (!isAdd && !isRemove)
        {
            Reply(info, Colored(ChatColors.Red, $"'{action}' is not add or remove."));
            return;
        }

        // Only create the group when we are adding to it, so a typo in a remove cannot
        // silently materialise an empty group.
        if (isAdd)
            EnsureGroup(group);

        bool changed = isAdd ? GrantGroupNode(group, node) : RevokeGroupNode(group, node);

        Reply(info, changed
            ? Colored(ChatColors.Green, $"{(isAdd ? "added" : "removed")} '{node}' on group '{group}'.")
            : Colored(ChatColors.Yellow, isAdd
                ? $"'{node}' is already on group '{group}'."
                : $"'{node}' was not on group '{group}'."));
    }

    // ------------------------------------------------------------------ debug

    [ConsoleCommand("css_perm_check", "Explain how a node resolves for a player")]
    [CommandHelper(minArgs: 2, usage: "(player) (node)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnCheck(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        string node = info.GetArg(2);

        foreach (ulong steamId in targets)
        {
            PermissionResolution resolution = Resolve(steamId, node);
            bool allowed = Effective(resolution);
            string? name = online.FirstOrDefault(p => SteamIdOf(p) == steamId)?.PlayerName;
            string label = name is null ? steamId.ToString() : $"{name} ({steamId})";
            string policy = Config.DefaultDeny ? "deny" : "allow";

            Reply(info, Colored(allowed ? ChatColors.Green : ChatColors.Red,
                $"{label}: {(allowed ? "ALLOW" : "DENY")} '{node}' -> {resolution}")
                + $" [default policy: {policy}]");
        }
    }

    [ConsoleCommand("css_perm_list", "List groups, or one user's groups and nodes")]
    [CommandHelper(minArgs: 0, usage: "[player]", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnList(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        if (info.ArgCount < 2)
        {
            Reply(info, Colored(ChatColors.LightBlue, $"Groups ({GetGroupNames().Count}):")
                + string.Join(" ", GetGroupNames().OrderBy(g => g, StringComparer.OrdinalIgnoreCase).Select(g => $" {g}")));
            return;
        }

        List<ulong> targets = TargetResolver.Resolve(info, 1, out List<CCSPlayerController> online);

        if (targets.Count == 0)
        {
            Reply(info, Colored(ChatColors.Red, $"No player matched '{info.GetArg(1)}'.{Usage}"));
            return;
        }

        foreach (ulong steamId in targets)
        {
            IReadOnlyList<string> groups = GetUserGroups(steamId);
            IReadOnlyList<string> nodes = GetUserNodes(steamId);

            Reply(info, Colored(ChatColors.LightBlue, $"{steamId}") + $" groups[{groups.Count}]: " + string.Join(" ", groups)
                + $"  nodes[{nodes.Count}]: " + string.Join(" ", nodes));
        }
    }

    // ------------------------------------------------------------ command gate

    [ConsoleCommand("css_perm_gate", "Gate a command behind a node without touching its plugin")]
    [CommandHelper(minArgs: 2, usage: "(command) (node)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnGateCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        string command = info.GetArg(1);
        string node = info.GetArg(2);

        Reply(info, RegisterGatedCommand(command, node)
            ? Colored(ChatColors.Green, $"'{command}' now requires '{node}'.")
            : Colored(ChatColors.Red, $"Could not gate '{command}'."));

        _gate.SaveMap();
    }

    [ConsoleCommand("css_perm_ungate", "Stop gating a command")]
    [CommandHelper(minArgs: 1, usage: "(command)", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnUngateCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        string command = info.GetArg(1);

        Reply(info, UnregisterGatedCommand(command)
            ? Colored(ChatColors.Green, $"'{command}' is no longer gated.")
            : Colored(ChatColors.Yellow, $"'{command}' was not gated."));

        _gate.SaveMap();
    }

    [ConsoleCommand("css_perm_gates", "List every gated command")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnListGates(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        IReadOnlyDictionary<string, string> gates = GatedCommands;

        if (gates.Count == 0)
        {
            Reply(info, Colored(ChatColors.Yellow, "No commands are gated."));
            return;
        }

        Reply(info, Colored(ChatColors.LightBlue, $"Gated commands ({gates.Count}):")
            + string.Join(" ", gates.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => $"{kv.Key}->{kv.Value}")));

        Reply(info, Colored(ChatColors.Grey,
            _gate.MapEnabled
                ? "Global command listener: enabled."
                : "Global command listener: disabled (EnableCommandMap in permission_manager.json)."));
    }

    // --------------------------------------------------------------- lifecycle

    [ConsoleCommand("css_perm_reload", "Re-read permissions.json and commands.json")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnReload(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        _saveTimer?.Kill();

        bool ok = Reload();

        _saveTimer = AddTimer(
            Math.Max(0.5f, Config.SaveDebounceSeconds),
            FlushIfDirty,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);

        Reply(info, ok
            ? Colored(ChatColors.Green, $"Reloaded {DataDirectory}.")
            : Colored(ChatColors.Red, "Reload failed, check the CounterStrikeSharp log for details."));
    }

    [ConsoleCommand("css_perm_save", "Write pending changes to disk now")]
    [CommandHelper(minArgs: 0, usage: "", whoCanExecute: CommandUsage.CLIENT_AND_SERVER)]
    public void OnSave(CCSPlayerController? player, CommandInfo info)
    {
        if (!RequireAdmin(player, info))
            return;

        Save();

        Reply(info, Colored(ChatColors.Green, "Saved."));
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// Gate for every <c>css_perm_*</c> command. Console is governed by
    /// <c>AllowConsoleAdmin</c>; players need <see cref="PermissionNodes.Admin"/>.
    /// </summary>
    private bool RequireAdmin(CCSPlayerController? player, CommandInfo info)
    {
        if (CanAdminister(player))
            return true;

        if (player is null)
        {
            Reply(info, Colored(ChatColors.Red, "Console administration is disabled (AllowConsoleAdmin in permission_manager.json)."));
        }
        else
        {
            ReplyToPlayer(player, Config.DenialMessage, info.GetArg(0), PermissionNodes.Admin, "admin");
        }

        return false;
    }

    private void ReportMutation(CommandInfo info, List<CCSPlayerController> online, int targets, int changed, string what)
    {
        string noun = targets == 1 ? "player" : "players";

        string summary = changed > 0
            ? Colored(ChatColors.Green, $"Updated {changed} of {targets} {noun} ({what}).")
            : Colored(ChatColors.Yellow, $"No change, all {targets} {noun} already had {what}.");

        string status = online.Count >= targets
            ? string.Empty
            : $" {online.Count}/{targets} offline, will apply on next join.";

        Reply(info, summary + status);
    }
}