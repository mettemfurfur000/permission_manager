using System;
using System.Collections.Generic;

namespace PermissionManagerApi;

/// <summary>
/// A group's nodes with every <c>@parent</c> reference inlined, plus the set of group
/// names the inheritance pulled in (including the group itself).
/// </summary>
public sealed class GroupClosure
{
    public GroupClosure(List<string> nodes, HashSet<string> groupNames)
    {
        Nodes = nodes;
        GroupNames = groupNames;
    }

    public static GroupClosure Empty { get; } = new(new List<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Flattened node patterns, inherited ones first.</summary>
    public List<string> Nodes { get; }

    /// <summary>Every group name that contributed, i.e. the group plus all of its ancestors.</summary>
    public HashSet<string> GroupNames { get; }
}

/// <summary>
/// Flattens group definitions into ordered node lists, resolving <c>@parent</c>
/// inheritance entries first.
///
/// <para>Groups may reference other groups by prefixing their name with
/// <see cref="PermissionNodes.GroupParentPrefix"/>:</para>
/// <code>
/// "vip":       ["fov.use", "fov.fovvalue"],
/// "moderator": ["@vip", "shop.use"],
/// "admin":     ["@moderator", "*"]
/// </code>
///
/// <para>The closure for <c>admin</c> is <c>["fov.use", "fov.fovvalue", "shop.use", "*"]</c>:
/// parent patterns come first so a child group can override what it inherits, and the
/// trailing <c>"*"</c> shows how an explicit deny placed after an inheritance would work.</para>
///
/// <para>Inheritance cycles (<c>a -&gt; b -&gt; a</c>) are detected rather than recursed
/// forever: the back edge is dropped and <c>onCycle</c> is invoked once per cycle with the
/// offending group name so the caller can warn about it.</para>
/// </summary>
public static class GroupResolver
{
    /// <summary>
    /// Returns the group's nodes with every <c>@parent</c> reference replaced by the parent's
    /// own closure, together with all group names involved. Unknown groups give
    /// <see cref="GroupClosure.Empty"/>.
    ///
    /// <para>Nodes are de-duplicated in first-seen order and each group's contribution is
    /// memoised for the duration of the call, so a diamond inheritance chain costs linear
    /// rather than exponential work.</para>
    /// </summary>
    public static GroupClosure FlattenDetailed(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groups,
        string groupName,
        Action<string>? onCycle = null)
    {
        if (groups is null || string.IsNullOrWhiteSpace(groupName) || !groups.ContainsKey(groupName))
            return GroupClosure.Empty;

        List<string> nodes = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> stack = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> ownNodes = new(StringComparer.OrdinalIgnoreCase);

        Visit(groupName);

        return new GroupClosure(nodes, names);

        void Absorb(IEnumerable<string> patterns)
        {
            foreach (string pattern in patterns)
            {
                if (seen.Add(pattern))
                    nodes.Add(pattern);
            }
        }

        void Visit(string name)
        {
            if (!groups.TryGetValue(name, out IReadOnlyList<string>? entries) || entries is null)
                return;

            names.Add(name);

            if (!stack.Add(name))
            {
                if (reported.Add(name))
                    onCycle?.Invoke(name);

                return;
            }

            if (ownNodes.TryGetValue(name, out List<string>? memoised))
            {
                Absorb(memoised);
                stack.Remove(name);
                return;
            }

            // Parents first so a group's own patterns override inherited ones.
            for (int i = 0; i < entries.Count; i++)
            {
                string? parent = PermissionNodes.GroupReferenceOf(entries[i]);

                if (parent is not null)
                    Visit(parent);
            }

            List<string> own = new();

            for (int i = 0; i < entries.Count; i++)
            {
                string entry = entries[i];

                if (PermissionNodes.IsGroupReference(entry) || string.IsNullOrWhiteSpace(entry))
                    continue;

                own.Add(entry.Trim());
            }

            ownNodes[name] = own;

            Absorb(own);

            stack.Remove(name);
        }
    }

    /// <summary>Flattened node patterns of a single group. Unknown groups give an empty list.</summary>
    public static List<string> Flatten(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groups,
        string groupName,
        Action<string>? onCycle = null)
    {
        return FlattenDetailed(groups, groupName, onCycle).Nodes;
    }

    /// <summary>
    /// Flattens several groups in order into one list, dropping duplicates so a diamond
    /// inheritance (<c>a -&gt; b, a -&gt; c, b -&gt; d, c -&gt; d</c>) does not repeat patterns.
    /// Order follows <paramref name="groupNames"/> and, within a group, parent first.
    /// </summary>
    public static List<string> FlattenAll(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groups,
        IReadOnlyList<string> groupNames,
        Action<string>? onCycle = null)
    {
        List<string> combined = new();

        if (groups is null || groupNames is null)
            return combined;

        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int i = 0; i < groupNames.Count; i++)
        {
            foreach (string node in FlattenDetailed(groups, groupNames[i], onCycle).Nodes)
            {
                if (seen.Add(node))
                    combined.Add(node);
            }
        }

        return combined;
    }

    /// <summary>Every group name reachable from <paramref name="groupName"/> through <c>@parent</c> entries.</summary>
    public static HashSet<string> ClosureNames(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? groups,
        string groupName,
        Action<string>? onCycle = null)
    {
        return FlattenDetailed(groups, groupName, onCycle).GroupNames;
    }
}