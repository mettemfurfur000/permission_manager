using System;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core;

namespace PermissionManagerApi;

/// <summary>
/// The single permission gateway every plugin on the server should talk to.
///
/// Obtain an instance through <see cref="PermissionApi"/> (locator) or
/// <see cref="PermissionApi.TryGetCapability"/> (CounterStrikeSharp plugin capability).
/// The interface is intentionally free of permission manager internals so consumers
/// never have to reference the plugin assembly itself.
///
/// All members are safe to call before the plugin finished loading and after it
/// unloaded: <see cref="IsReady"/> reports availability and every query answers
/// conservatively (see <c>DefaultDeny</c>) when the store is not loaded yet.
/// </summary>
public interface IPermissionService
{
    /// <summary>True once permission data has been loaded and the command gate is live.</summary>
    bool IsReady { get; }

    // ---------------------------------------------------------------- queries

    /// <summary>
    /// Checks a node for a player. <paramref name="player"/> may be <c>null</c> (server console),
    /// in which case the configured console policy decides.
    /// </summary>
    bool HasPermission(CCSPlayerController? player, string node);

    /// <summary>Checks a node for a SteamID64. Works for offline users too.</summary>
    bool HasPermission(ulong steamId, string node);

    /// <summary>Full resolution result including which layer decided. Intended for debugging.</summary>
    PermissionResolution Resolve(ulong steamId, string node);

    /// <summary>True when the player belongs to the named group (directly or via inheritance).</summary>
    bool InGroup(CCSPlayerController? player, string group);

    /// <summary>True when the SteamID64 belongs to the named group (directly or via inheritance).</summary>
    bool InGroup(ulong steamId, string group);

    /// <summary>Groups directly assigned to the user, in file order. Does not include the default group.</summary>
    IReadOnlyList<string> GetUserGroups(ulong steamId);

    /// <summary>Nodes assigned directly to the user, in file order.</summary>
    IReadOnlyList<string> GetUserNodes(ulong steamId);

    /// <summary>Every group name currently defined.</summary>
    IReadOnlyCollection<string> GetGroupNames();

    /// <summary>Raw node patterns of a group, including unresolved <c>@parent</c> references.</summary>
    IReadOnlyList<string> GetGroupEntries(string group);

    // -------------------------------------------------------------- mutations

    /// <summary>Adds a direct node to a user. Returns false when the node was already present.</summary>
    bool GrantNode(ulong steamId, string node);

    /// <summary>Removes a direct node from a user. Returns false when the node was not present.</summary>
    bool RevokeNode(ulong steamId, string node);

    /// <summary>Adds a user to a group. Returns false when the membership already existed.</summary>
    bool GrantGroup(ulong steamId, string group);

    /// <summary>Removes a user from a group. Returns false when the membership did not exist.</summary>
    bool RevokeGroup(ulong steamId, string group);

    /// <summary>Creates a group when it does not exist yet.</summary>
    bool EnsureGroup(string group);

    /// <summary>Adds a node pattern to a group. Returns false when it was already present.</summary>
    bool GrantGroupNode(string group, string node);

    /// <summary>Removes a node pattern from a group. Returns false when it was not present.</summary>
    bool RevokeGroupNode(string group, string node);

    // ---------------------------------------------------------- command gating

    /// <summary>
    /// Gates a server command behind a permission node. Safe to call from another plugin's
    /// <c>Load</c> and idempotent: re-registering the same command only refreshes the node.
    /// </summary>
    /// <returns>False when the command name or node was blank.</returns>
    bool RegisterGatedCommand(string command, string node);

    /// <summary>Stops gating a command. Returns false when the command was not gated.</summary>
    bool UnregisterGatedCommand(string command);

    /// <summary>Current <c>command -> node</c> gate table, lower cased.</summary>
    IReadOnlyDictionary<string, string> GatedCommands { get; }

    // ------------------------------------------------------------- lifecycle

    /// <summary>Re-reads <c>permissions.json</c> and <c>commands.json</c> from disk. Returns false on failure.</summary>
    bool Reload();

    /// <summary>Flushes pending changes to disk immediately.</summary>
    void Save();

    // ---------------------------------------------------------------- events

    /// <summary>Raised after a gated command was blocked. Fires on the game thread.</summary>
    event EventHandler<PermissionDeniedEventArgs>? PermissionDenied;

    /// <summary>Raised after any mutation of groups, users or the gate table.</summary>
    event EventHandler<PermissionsChangedEventArgs>? PermissionsChanged;
}

/// <summary>Raised when a gated command is blocked.</summary>
public sealed class PermissionDeniedEventArgs : EventArgs
{
    public PermissionDeniedEventArgs(CCSPlayerController? player, ulong steamId, string command, string node)
    {
        Player = player;
        SteamId = steamId;
        Command = command;
        Node = node;
    }

    /// <summary>The blocked player, or <c>null</c> for console/server originated commands.</summary>
    public CCSPlayerController? Player { get; }

    /// <summary>SteamID64 of <see cref="Player"/>, or <c>0</c> when unknown (console, bots).</summary>
    public ulong SteamId { get; }

    /// <summary>The command that was blocked, lower cased (for example <c>css_fov</c>).</summary>
    public string Command { get; }

    /// <summary>The node the command is gated behind (for example <c>fov.use</c>).</summary>
    public string Node { get; }
}

/// <summary>Raised after permission data or the gate table changed.</summary>
public sealed class PermissionsChangedEventArgs : EventArgs
{
    public PermissionsChangedEventArgs(PermissionsChangeKind kind, string? detail = null)
    {
        Kind = kind;
        Detail = detail;
    }

    public PermissionsChangeKind Kind { get; }

    /// <summary>Human readable description of what changed, when there is one.</summary>
    public string? Detail { get; }
}

/// <summary>Coarse classification of a permission data change.</summary>
public enum PermissionsChangeKind
{
    /// <summary>Data was reloaded from disk or freshly created.</summary>
    Reloaded,
    /// <summary>A user's direct nodes changed.</summary>
    UserNodes,
    /// <summary>A user's group membership changed.</summary>
    UserGroups,
    /// <summary>A group's definition changed.</summary>
    GroupNodes,
    /// <summary>A command was gated or ungated.</summary>
    CommandGate,
}