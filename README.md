# Permission Manager

A Counter-Strike 2 plugin for [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) that
centralises permissions for **every** plugin on the server, including third party ones. Think LuckPerms: groups,
users, wildcard nodes, and one place that decides who may run what.

Other plugins ask this plugin questions and register their commands with it. They do not need to change their
own permission logic, and they never reference this plugin's assembly.

## Requirements

- Counter-Strike 2 dedicated server
- [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) on the net10.0 runtime (the version
  shipping the current `CounterStrikeSharp.API.dll`)

## Installation

1. Download a release (or build it yourself, see below).
2. Extract the archive into `game/csgo/`, so that:
   - plugin files land in `addons/counterstrikesharp/plugins/permission_manager/`
   - the shared API lands in `addons/counterstrikesharp/shared/permission_manager_api/`
   - the data files land in `addons/counterstrikesharp/configs/plugins/permission_manager/`
3. Restart the server or run `css_plugins reload permission_manager`.

On first launch the plugin creates `permission_manager.json` (config), `permissions.json` (data, if missing) and
`commands.json` (command map).

## Quick start

```
css_perm_group_create admin perm.admin *        # a group that can administer and do anything
css_perm_group_add @all admin                  # everyone is an admin (for a test server)
css_perm_gate css_fov fov.use                   # gate a command that belongs to another plugin
css_perm_check @me fov.use                     # see which layer decided
```

Out of the box `DefaultDeny` is **false**, so nothing is locked until you gate it. The server owner always keeps
access through CounterStrikeSharp's `@css/root`.

## Concepts

### Nodes

Dot notation strings: `fov.use`, `fov.fovvalue`, `perm.admin`. Two wildcards are supported:

| Pattern | Meaning |
|---|---|
| `*` | matches everything |
| `fov.*` | matches `fov.use`, `fov.setvalue`, `fov.a.b` — but not `fovuse` |
| `f?v` | matches exactly one character (rarely useful) |

Matching is case insensitive. Prefixing a node with `-` makes it an explicit **deny**.

### Groups

Named collections of nodes. A group may inherit another group by prefixing its name with `@`:

```json
{
  "groups": {
    "default": [],
    "vip":       ["fov.use", "fov.fovvalue"],
    "moderator": ["@vip", "shop.use"],
    "admin":     ["@moderator", "*", "-perm.admin"]
  }
}
```

`admin` above resolves to `fov.use, fov.fovvalue, shop.use, *, -perm.admin`: everything except permission
administration. Inherited nodes come first, so a child group can override what it inherits.

Inheritance cycles (`a -> b -> a`) are detected, the offending edge is dropped, and the plugin logs a warning
instead of recursing forever.

### Resolution order

For a given node, layers are evaluated **least specific to most specific**, and the **last matching pattern wins**:

1. the `default` group
2. each group the user belongs to, in file order
3. the user's own direct nodes

Within one layer, patterns are evaluated in order too. Concretely, for a user in `admin` with `-fov.use` as a
direct node:

| Node | Decided by | Result |
|---|---|---|
| `shop.use` | `*` on `admin` | allow |
| `fov.use` | `-fov.use` on the user | deny |
| `perm.admin` | `*` then `-perm.admin` on `admin` | deny |
| `unmapped.node` | nothing matched | falls back to `DefaultDeny` |

`css_perm_check` prints exactly this: which layer and which pattern won.

### File format — `permissions.json`

```json
{
  "version": 1,
  "groups": {
    "default": [],
    "vip":       ["fov.use", "fov.fovvalue"],
    "moderator": ["@vip", "shop.use"],
    "admin":     ["@moderator", "*", "-perm.admin"]
  },
  "users": {
    "76561198000000000": { "groups": ["vip"], "nodes": ["fov.setvalue"] }
  }
}
```

Keys are lower cased and trimmed on load, so hand editing is safe. Writes are debounced (`SaveDebounceSeconds`)
and atomic.

## Gating commands

There are two ways, and both are opt-in per command.

**From another plugin's code** — one line in its `Load`:

```csharp
public override void Load(bool hotReload)
{
    var perms = PermissionApi.Get();
    perms?.RegisterGatedCommand("css_fov", "fov.use");
}
```

**From a config file** — `commands.json`, no plugin changes at all:

```json
{
  "version": 1,
  "commands": {
    "css_fov": "fov.use",
    "css_fovvalue": "fov.fovvalue"
  }
}
```

That path needs `"EnableCommandMap": true` in `permission_manager.json`. With it off, `commands.json` is left
alone entirely and only gates registered through `RegisterGatedCommand` (or `css_perm_gate`) are enforced.
Turning it on installs one global command listener; for commands that are not in the map it does a single
dictionary lookup and returns immediately.

`commands.json` is also the plugin's **persisted gate table**: it is rewritten on unload, so gates a plugin
registered in code survive a reload. The flip side is that deleting a line from the file un-gates that command
until its owning plugin registers it again.

Or at runtime, without touching either: `css_perm_gate css_fov fov.use`.

Denied commands return `HookResult.Handled` from a `HookMode.Pre` listener, so the target command's handler
**never runs**. Permitted commands are left completely alone.

## Commands

All of these require the `perm.admin` node. Console is allowed unless `AllowConsoleAdmin` is off.

| Command | Description |
|---|---|
| `css_perm_grant <player> <node>` | Give a player a node directly |
| `css_perm_revoke <player> <node>` | Take a direct node away |
| `css_perm_group_add <player> <group>` | Add a player to a group |
| `css_perm_group_remove <player> <group>` | Remove a player from a group |
| `css_perm_group_create <group> [node ...]` | Create a group, optionally seeded with nodes |
| `css_perm_group_delete <group>` | Delete a group and drop it from everyone |
| `css_perm_group_node <add\|remove> <group> <node>` | Edit one group's node list |
| `css_perm_check <player> <node>` | Explain how a node resolves |
| `css_perm_list [player]` | List groups, or one user's groups and nodes |
| `css_perm_gate <command> <node>` | Gate a command behind a node |
| `css_perm_ungate <command>` | Stop gating a command |
| `css_perm_gates` | List every gated command |
| `css_perm_reload` | Re-read both JSON files |
| `css_perm_save` | Write pending changes now |

`<player>` accepts CounterStrikeSharp's selectors (`@all`, `@me`, `#ct`, `#t`, player names) **and** a bare
SteamID64. A SteamID64 is resolved straight out of the file, so you can grant to somebody who is offline.

## Configuration — `permission_manager.json`

| Key | Default | Description |
|---|---|---|
| `DataDirectory` | `""` | Where the JSON files live. Empty means the plugin's own config folder. |
| `DefaultGroup` | `default` | Group applied to everyone, including users with no entry. |
| `SaveDebounceSeconds` | `5` | Writes are batched: at most one `permissions.json` write per this interval. |
| `AllowConsole` | `true` | Console / RCON passes every node check. |
| `AllowBots` | `true` | Bots pass every node check. |
| `DefaultDeny` | `false` | What a node nobody granted answers. `false` keeps the server safe to enable. |
| `AllowConsoleAdmin` | `true` | Let the console run `css_perm_*` even when `AllowConsole` is off. |
| `UseCounterStrikeSharpAdmins` | `true` | Honour CSS's own flags (e.g. `@css/root`) as an override in strict mode. |
| `EnableCommandMap` | `false` | Apply `commands.json`. Off means only `RegisterGatedCommand` gates are enforced. |
| `DenialMessage` | see file | Message for blocked commands. See placeholders below. |
| `DenyToChat` | `true` | Send the denial to the player's chat. |
| `DenyToConsole` | `true` | Print the denial to the server console. |
| `LogDenials` | `false` | Also log every denial to the plugin's own log line. |

`DenialMessage` supports `{command}`, `{node}`, `{group}`, `{player}` and colour tags `{red}` (`{lightred}` is an
alias for it), `{green}`, `{lime}`, `{olive}`, `{yellow}`, `{lightyellow}`, `{lightblue}`, `{purple}`,
`{lightpurple}`, `{darkred}`, `{grey}`/`{gray}`, `{silver}`, `{gold}`, `{white}`, `{default}`. Unknown tags are
printed literally rather than swallowed.

Turning on `DefaultDeny` switches the whole server to strict mode: a node nobody was granted is refused. In that
mode `UseCounterStrikeSharpAdmins` lets `@css/root` still work, so the owner cannot lock themselves out.

## Integrating another plugin

Reference the shared API and ask it questions:

```csharp
using PermissionManagerApi;

// Wire once, re-wired automatically every time permission_manager (re)loads.
PermissionApi.Available += perms => perms.RegisterGatedCommand("css_fov", "fov.use");
PermissionApi.Gone += _ => { /* the plugin is going away, drop cached state */ };
```

Then anywhere:

```csharp
if (PermissionApi.Get()?.HasPermission(player, "fov.use") == true)
    PlayerSettings.SetFov(player, 90);
```

`PermissionApi.Get()` returns `null` instead of throwing when the plugin is not loaded, so consumers do not need
null-guards everywhere. The service is published twice — through the static locator and through the
CounterStrikeSharp plugin capability `tem_perm:service` — and `PermissionApi.Get()` checks both.

Useful members of `IPermissionService`:

| Member | Purpose |
|---|---|
| `HasPermission(player \| steamId, node)` | the check you want 99% of the time |
| `Resolve(steamId, node)` | full result including which layer and pattern decided |
| `InGroup(player \| steamId, group)` | direct or inherited membership |
| `GrantGroup` / `RevokeGroup` | e.g. an existing VIP plugin granting `vip` when status changes |
| `GrantNode` / `RevokeNode` | direct node grants |
| `RegisterGatedCommand` / `UnregisterGatedCommand` | declare a command as gated |
| `PermissionDenied` event | react to blocked commands (logging, metrics, ...) |

`PermissionDenied` and `PermissionsChanged` both fire on the game thread.

### Event lifetime

They belong to the plugin instance, so reloading `permission_manager` replaces them. Wire event handlers from
`PermissionApi.Available` (which fires on every load) and drop them from `PermissionApi.Gone`; the snippet above
does exactly that. A handler attached directly to `IPermissionService.PermissionDenied` and left there will go
quiet after a reload of this plugin.

### Migrating an existing VIP plugin

Nothing has to change in the VIP plugin. Either let it grant the group itself:

```csharp
perms.GrantGroup(steamId, "vip");   // on VIP status change
perms.RevokeGroup(steamId, "vip");  // when it lapses
```

or drop the manager's `vip` group into the existing membership flow. `GetUserGroups` and `GetGroupNames` exist
for reporting.

## Building from source

Requirements: .NET SDK (net10.0), make + bash (msys2 on Windows works fine).

Place the repo inside `game/csgo/addons/counterstrikesharp/plugins/permission_manager/` so that
`../../api/CounterStrikeSharp.API.dll` resolves, and keep `../../shared/permission_manager_api/` next to it,
then:

```bash
make                 # build shared API + plugin, copy binaries next to the sources
make test            # run the unit tests (no game needed)
make release_full    # package a release zip
```

`watch.sh` rebuilds whenever `src/` changes.

## Tests

`make test` runs 132 xUnit tests over the pure logic: wildcard matching, resolution order and precedence,
negation, group inheritance, cycle detection and diamond dedup, message templating, target parsing, plus JSON
load/normalise/save round trips. They need no running game because the matching and resolution code lives in the
shared API assembly.

`CommandGate` itself is not unit tested — it needs a live `BasePlugin` to register listeners with. Its
dependencies are, though: matching, resolution, closure flattening, templating and target parsing are all
covered above.

## Non-goals (v1)

Database backend, web UI, time-based grants, per-map permissions. All of them would slot in behind
`IPermissionService`, which is why the interface is deliberately small.