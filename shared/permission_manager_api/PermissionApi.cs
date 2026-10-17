using System;
using System.Collections.Generic;
using CounterStrikeSharp.API.Core.Capabilities;

namespace PermissionManagerApi;

/// <summary>
/// Locator for the running <see cref="IPermissionService"/>.
///
/// The permission manager publishes itself twice:
/// <list type="number">
/// <item>through <see cref="Register"/> on this static locator, and</item>
/// <item>through a CounterStrikeSharp <see cref="PluginCapability{T}"/> named
/// <see cref="CapabilityName"/>.</item>
/// </list>
///
/// Both routes share the same generic instantiation only when consumers reference this
/// very assembly, which is exactly why the contract lives in <c>shared/</c> instead of
/// inside the plugin. Use <see cref="Get"/> and you cannot care which route won.
///
/// Typical consumer code:
/// <code>
/// var perms = PermissionApi.Get();
/// if (perms is not null &amp;&amp; perms.HasPermission(player, "fov.use"))
///     perms.RegisterGatedCommand("css_fov", "fov.use");
/// </code>
/// </summary>
public static class PermissionApi
{
    /// <summary>Capability name used with CounterStrikeSharp's capability registry.</summary>
    public const string CapabilityName = "tem_perm:service";

    private static readonly PluginCapability<IPermissionService> Capability = new(CapabilityName);

    private static IPermissionService? _service;

    /// <summary>Raised when a service becomes available, so consumers can wire themselves up late.</summary>
    public static event Action<IPermissionService>? Available;

    /// <summary>Raised when the service unloads. Handlers must not assume the service stays alive.</summary>
    public static event Action? Gone;

    /// <summary>The active service, or <c>null</c> when the plugin is not loaded.</summary>
    public static IPermissionService? Current => _service ?? TryGetCapability();

    /// <summary>True when a service is reachable through either route.</summary>
    public static bool IsAvailable => Current is not null;

    /// <summary>Called by the plugin on load. Repeated calls with the same instance are ignored.</summary>
    public static void Register(IPermissionService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (ReferenceEquals(_service, service))
            return;

        _service = service;
        Available?.Invoke(service);
    }

    /// <summary>Called by the plugin on unload. Only clears the locator if it still owns the instance.</summary>
    public static void Unregister(IPermissionService service)
    {
        ArgumentNullException.ThrowIfNull(service);

        if (!ReferenceEquals(_service, service))
            return;

        _service = null;
        Gone?.Invoke();
    }

    /// <summary>Non throwing lookup. Prefer this over touching <see cref="Current"/> directly.</summary>
    public static IPermissionService? Get() => Current;

    /// <summary>
    /// Reads the service straight from the CounterStrikeSharp capability registry.
    /// Returns <c>null</c> instead of throwing when nothing registered the capability yet.
    /// </summary>
    public static IPermissionService? TryGetCapability()
    {
        try
        {
            return Capability.Get();
        }
        catch (KeyNotFoundException)
        {
            // PluginCapability<T>.Get() indexes a dictionary without checking first.
            return null;
        }
    }
}