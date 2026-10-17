# permission_manager_api

The contract between the `permission_manager` plugin and every other plugin on the server.

This assembly is **shared** rather than bundled into the plugin on purpose. CounterStrikeSharp's plugin capability
registry lives on `PluginCapability<T>`, a generic type from `CounterStrikeSharp.API`. Two plugins only see the
same provider list if `T` is the *same type*, which only holds when both reference the same DLL. Putting
`IPermissionService` in a shared assembly is what makes `PermissionApi.Get()` work across plugin boundaries.

## Contents

| File | What it holds |
|---|---|
| `PermissionNodes.cs` | well known nodes (`perm.admin`, `*`, `@css/root`) and the `-` / `@` prefixes |
| `IPermissionService.cs` | the service interface, `PermissionDeniedEventArgs`, `PermissionsChangedEventArgs` |
| `PermissionApi.cs` | static locator + capability accessor |
| `PermissionCheck.cs` | `PermissionDecision`, `PermissionLayer`, `PermissionResolution`, `PermissionCheckResult` |
| `NodeMatcher.cs` | wildcard matching (`*`, `?`), normalisation, negation splitting |
| `PermissionResolver.cs` | resolution order and precedence |
| `GroupResolver.cs` | `@parent` group inheritance, flattening, cycle detection |

The last three are pure C# with no game dependency, which is what makes them unit testable without a server.

## Consumer usage

```csharp
using PermissionManagerApi;

public override void Load(bool hotReload)
{
    PermissionApi.Get()?.RegisterGatedCommand("css_fov", "fov.use");
}

// ...later, anywhere:
if (PermissionApi.Get()?.HasPermission(player, "fov.use") == true) { /* ... */ }
```

`Get()` returns `null` when the plugin is not loaded rather than throwing, so late-loading consumers can do:

```csharp
PermissionApi.Available += service => service.RegisterGatedCommand("css_fov", "fov.use");
```

The capability route is available too if you prefer to avoid the static:

```csharp
IPermissionService? perms = PermissionApi.TryGetCapability();
```

## Build

```bash
make        # build + copy the DLL to this folder
```

The plugin's `makefile` runs this automatically. Drop the built `permission_manager_api.dll` into
`addons/counterstrikesharp/shared/permission_manager_api/` on the server.