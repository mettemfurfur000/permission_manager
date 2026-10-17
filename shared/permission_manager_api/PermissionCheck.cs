using System.Collections.Generic;

namespace PermissionManagerApi;

/// <summary>Outcome of resolving a single node against a user's layered permissions.</summary>
public enum PermissionDecision
{
    /// <summary>No pattern matched. Falls back to the server's default deny policy.</summary>
    Unset = 0,
    /// <summary>A positive pattern matched.</summary>
    Allow = 1,
    /// <summary>A negated pattern matched and won.</summary>
    Deny = 2,
}

/// <summary>
/// One ordered source of permission patterns, for example the default group, a single
/// user group, or the user's own direct nodes.
/// </summary>
public readonly record struct PermissionLayer(string Name, IReadOnlyList<string> Patterns)
{
    /// <summary>The fallback group applied to everybody.</summary>
    public static PermissionLayer Default(IReadOnlyList<string> patterns) => new("default", patterns);

    /// <summary>A group the user belongs to.</summary>
    public static PermissionLayer Group(string name, IReadOnlyList<string> patterns) => new(name, patterns);

    /// <summary>The user's own direct nodes.</summary>
    public static PermissionLayer User(IReadOnlyList<string> patterns) => new("user", patterns);
}

/// <summary>
/// Result of a node resolution, including which layer and which pattern decided.
/// </summary>
public readonly record struct PermissionResolution(
    PermissionDecision Decision,
    string Source,
    string? Pattern)
{
    /// <summary>An empty resolution: nothing matched anywhere.</summary>
    public static PermissionResolution None => new(PermissionDecision.Unset, "-", null);

    public bool Allowed => Decision == PermissionDecision.Allow;

    public bool Denied => Decision == PermissionDecision.Deny;

    /// <summary>True when a pattern actually matched, as opposed to falling through to the default policy.</summary>
    public bool Explicit => Decision != PermissionDecision.Unset;

    public override string ToString()
    {
        if (!Explicit)
            return "unset";

        return Pattern is null
            ? $"{Decision.ToString().ToLowerInvariant()} (layer: {Source})"
            : $"{Decision.ToString().ToLowerInvariant()} by '{Pattern}' (layer: {Source})";
    }
}