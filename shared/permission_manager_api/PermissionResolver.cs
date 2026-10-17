using System;
using System.Collections.Generic;

namespace PermissionManagerApi;

/// <summary>
/// Resolves a node against the ordered layers that make up a user's effective permissions.
///
/// <para><b>Resolution order</b></para>
///
/// Layers are evaluated from least specific to most specific and the
/// <i>last</i> matching pattern wins:
///
/// <list type="number">
/// <item>the default group,</item>
/// <item>each group the user belongs to, in the order they are stored,</item>
/// <item>the user's own direct nodes.</item>
/// </list>
///
/// Within one layer patterns are evaluated in order too, so a later pattern overrides
/// an earlier one. That makes explicit denials useful: <c>admin = ["*", "-perm.admin"]</c>
/// grants everything except administration, and <c>user nodes = ["perm.admin"]</c> on top
/// of that group wins again.
///
/// A pattern prefixed with <c>-</c> is an explicit deny. See <see cref="PermissionNodes"/>.
/// </para>
/// </summary>
public static class PermissionResolver
{
    /// <summary>Convenience overload for the common three source case.</summary>
    public static PermissionResolution Resolve(
        string node,
        IReadOnlyList<string>? defaultGroupNodes,
        IReadOnlyList<string>? groupNodes,
        IReadOnlyList<string>? directNodes)
    {
        return Resolve(node, new[]
        {
            PermissionLayer.Default(defaultGroupNodes ?? Array.Empty<string>()),
            PermissionLayer.Group("group", groupNodes ?? Array.Empty<string>()),
            PermissionLayer.User(directNodes ?? Array.Empty<string>()),
        });
    }

    /// <summary>
    /// Walks <paramref name="layers"/> in order and returns the last decision that was set.
    /// Returns <see cref="PermissionResolution.None"/> when nothing matched.
    /// </summary>
    public static PermissionResolution Resolve(string node, IReadOnlyList<PermissionLayer> layers)
    {
        if (string.IsNullOrWhiteSpace(node) || layers is null)
            return PermissionResolution.None;

        PermissionResolution best = PermissionResolution.None;

        foreach (PermissionLayer layer in layers)
        {
            IReadOnlyList<string> patterns = layer.Patterns;
            if (patterns is null)
                continue;

            for (int i = 0; i < patterns.Count; i++)
            {
                PermissionNodes.TrySplitNegation(patterns[i], out bool negated, out string bare);

                if (bare.Length == 0 || !NodeMatcher.Matches(bare, node))
                    continue;

                best = new PermissionResolution(
                    negated ? PermissionDecision.Deny : PermissionDecision.Allow,
                    layer.Name,
                    patterns[i]);
            }
        }

        return best;
    }

    /// <summary>Layers describing a user, least specific first.</summary>
    public static List<PermissionLayer> BuildLayers(
        string defaultGroupName,
        IReadOnlyList<string> defaultGroupNodes,
        IReadOnlyList<string> groupNames,
        Func<string, IReadOnlyList<string>> groupNodeLookup,
        IReadOnlyList<string> directNodes)
    {
        List<PermissionLayer> layers = new();

        if (defaultGroupNodes.Count > 0)
            layers.Add(PermissionLayer.Default(defaultGroupNodes));

        for (int i = 0; i < groupNames.Count; i++)
        {
            string name = groupNames[i];

            // The default group is already represented by its own layer.
            if (string.Equals(name, defaultGroupName, StringComparison.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<string> nodes = groupNodeLookup(name);
            if (nodes.Count > 0)
                layers.Add(PermissionLayer.Group(name, nodes));
        }

        if (directNodes.Count > 0)
            layers.Add(PermissionLayer.User(directNodes));

        return layers;
    }
}