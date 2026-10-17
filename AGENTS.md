# AGENTS.md — permission_manager

Counter-Strike 2 plugin for the CounterStrikeSharp framework. Centralised permission manager: one JSON store of
groups/users/nodes plus a command gate that blocks gated commands before their handlers run.

## Build & dev

```bash
make                 # rebuild shared API, build, copy binaries to project root
make test            # rebuild shared API, then dotnet test (xUnit, no game required)
make release_full    # package plugin + shared API + data files into addons/, then zip
```

- `watch.sh` — polls `src/` and `../../shared/permission_manager_api/` for changes, runs `make` on each.
- Must be built **inside the CS2 plugin folder** (`game/csgo/addons/counterstrikesharp/plugins/permission_manager/`)
  so DotNet resolves `../../api/CounterStrikeSharp.API.dll`.
- Target: `net10.0`, `AllowUnsafeBlocks=true`.

## Two projects (plus tests)

| Project | Location | Build separately? |
|---|---|---|
| Main plugin | `permission_manager.csproj` (this dir) | `make` (also builds the API first) |
| Shared API | `../../shared/permission_manager_api/` | Its own `makefile`; `make` here runs it |
| Tests | `tests/permission_manager_tests/` | `make test`, or `dotnet test` (132 tests) |

The plugin references the shared API as a compiled `.dll` via `HintPath`. If you change the API interface, run
`make` or `make test` (both build the API first) or `make -C ../../shared/permission_manager_api`.

**Do not replace the `<Reference HintPath>` with a `ProjectReference`.** The API has to exist as a standalone DLL
in `shared/` at runtime; that is the whole point of the split (see below). `make test` building the API first
matters: `dotnet test` on its own would compile the tests against a **stale** API DLL and quietly pass.

## Project structure

- `src/main.cs` — plugin entrypoint (`BasePlugin`, `IPluginConfig`, and `IPermissionService` itself), plus
  `LogInfo`/`LogWarn`/`LogError` helpers.
- `src/config/config.cs` — config POCO. CSS generates `permission_manager.json` from it on first launch.
- `src/storage/PermissionStore.cs` — `permissions.json` load/save, normalisation, group closure cache,
  per-user snapshots, all mutations.
- `src/gating/CommandGate.cs` — `commands.json`, `AddCommandListener` registration, denial handling.
- `src/commands/PermissionCommands.cs` — every `css_perm_*` command (a `partial class` of the plugin).
- `src/utils/ChatFormat.cs` — `{lightred}`-style colour tags and `{command}`/`{node}` substitution.
- `src/utils/TargetResolver.cs` — target argument to SteamID64s; supports offline grants.

The **pure logic lives in the shared API**, not here: `NodeMatcher` (wildcards), `PermissionResolver`
(resolution order), `GroupResolver` (`@parent` inheritance + cycle detection). That is what the unit tests
exercise, and it is why the test project does not need a running game.

## Resolution order (the core rule)

Layers are evaluated **least specific to most specific**, and the **last matching pattern wins**:

1. `default` group
2. each group the user belongs to, in file order
3. the user's own direct nodes

Within one layer, patterns are evaluated in order too. A pattern prefixed with `-` is an explicit deny.

```
default: ["*"]          -> everything allowed
vip:     ["fov.*"]      -> fov.* refines nothing, still allowed
user:    ["-fov.use"]  -> fov.use denied for this one user
```

So a direct node overrides a group, a group overrides the default group, and negations are how you carve an
exception out of a broad grant (`admin = ["*", "-perm.admin"]`).

## Gating a command

Two paths, both live in `CommandGate`:

- **Explicit** — `RegisterGatedCommand("css_fov", "fov.use")`. Installs one `AddCommandListener(name, cb, HookMode.Pre)`
  per command. Idempotent, safe from another plugin's `Load`.
- **Command map** — opt in with `EnableCommandMap`. With it on, everything in `commands.json` is gated through a
  single global `AddCommandListener(null, cb, HookMode.Pre)` *and* one listener per mapped command. The global
  hook is a dictionary lookup and returns `Continue` for anything unmapped. With it off, `commands.json` is
  ignored entirely and any previously applied map gates are dropped, so the switch really means off.

Both funnel into `Evaluate()`. A `(CommandInfo.Handle, command, tick)` guard stops the two paths from printing
two denial messages if the native layer ever runs both listeners for one dispatch.

`commands.json` is also the **persisted** gate table: `Unload` rewrites it with everything, including gates other
plugins registered in code, so those survive a reload. `_fromMap` tracks which entries came from the file so a
line deleted from it un-gates that command until its owner registers again.

On denial: `HookResult.Handled` (command never reaches its handler), optional chat + console output, and the
public `PermissionDenied` event. On success: `HookResult.Continue`, untouched.

## Default policy

`DefaultDeny` is the answer for a node no pattern matched. `false` (shipped default) means allow, which is why
enabling this plugin can never lock the server out. `true` means deny, and only then does
`UseCounterStrikeSharpAdmins` let `@css/root` through as an override. Keep the polarity consistent: use
`Effective(resolution)` rather than re-deriving it.

## Commands

All `css_perm_*` commands are guarded by `perm.admin`, checked **in the handler** rather than with
`[RequiresPermissions]` — that attribute is answered by CSS's own admin system and would create a competing
permission system. Console is allowed by `AllowConsoleAdmin`.

- `css_perm_grant <player> <node>` / `css_perm_revoke <player> <node>`
- `css_perm_group_add|remove <player> <group>`
- `css_perm_group_create <group> [node ...]` / `css_perm_group_delete <group>`
- `css_perm_group_node <add|remove> <group> <node>`
- `css_perm_check <player> <node>` — prints which layer and pattern decided
- `css_perm_list [player]`
- `css_perm_gate <command> <node>` / `css_perm_ungate <command>` / `css_perm_gates`
- `css_perm_reload` / `css_perm_save`

Player selectors: CounterStrikeSharp's own (`@all`, `#ct`, names) plus a bare SteamID64, which is resolved
straight from the file so you can grant to someone who is offline.

## Caveats

- **Config is regenerated by CSS** from `PermissionManagerConfig` on first launch. The data files
  (`permissions.json`, `commands.json`) are *not* touched by the plugin's config layer and are yours to edit.
- **Saving is batched.** A `SaveDebounceSeconds` repeating timer flushes if anything changed, so a burst of
  grants costs one write. `Unload` and `css_perm_save` flush immediately.
- **The store is game-thread affine and takes no locks.** It is owned by one plugin and every caller (commands,
  the save timer, `Unload`) runs on the CounterStrikeSharp main thread. `Changed` is raised straight from the
  mutating call, so a handler must not block on another thread that calls back into the service.
- **`permissions.json` writes are atomic** (temp file + move). A malformed file is reported and replaced with
  defaults rather than left half-loaded.
- **Snapshots carry a revision counter.** Any mutation bumps it, so the next check rebuilds. There is no
  explicit invalidate call to forget.
- **Never hand out a shared default instance.** A literal `null` user entry in the JSON used to alias one object
  across every such user; `Normalize` allocates per user and `GroupResolver` memoises per call.
- **`Logger` is deliberately unused** — `ILogger` lives in an assembly the other plugins never reference, so the
  plugin uses `LogInfo`/`LogWarn`/`LogError` (`Server.PrintToConsole`) like the rest of the ecosystem.
- `.gitignore` blocks `.dll`, `.pdb`, `.sln`, `.deps.json`, `addons/`, `bin/`, `obj/`, `*.zip`.
- No linter/formatter config. No CI.