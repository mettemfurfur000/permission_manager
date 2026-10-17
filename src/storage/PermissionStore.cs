using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using PermissionManagerApi;

namespace permission_manager.src.storage;

/// <summary>On disk shape of <c>permissions.json</c>.</summary>
public sealed class PermissionData
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Group name to its raw node patterns. An entry prefixed with <c>@</c> inherits another group.</summary>
    [JsonPropertyName("groups")]
    public Dictionary<string, List<string>> Groups { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>SteamID64 to that user's groups and direct nodes.</summary>
    [JsonPropertyName("users")]
    public Dictionary<string, PermissionUserEntry> Users { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per user slice of <c>permissions.json</c>.</summary>
public sealed class PermissionUserEntry
{
    [JsonPropertyName("groups")]
    public List<string> Groups { get; set; } = [];

    [JsonPropertyName("nodes")]
    public List<string> Nodes { get; set; } = [];
}

/// <summary>
/// Owns <c>permissions.json</c>: loading, atomic saving, mutations, group closure caching
/// and per user snapshots.
///
/// <para>Every mutation bumps a revision counter. Snapshots and group closures carry the
/// revision they were built at and are rebuilt lazily, so a grant or revoke is visible to
/// the very next permission check without any explicit invalidation call.</para>
///
/// <para><b>Threading.</b> The store is game-thread affine: it is owned by one plugin and
/// every caller (commands, the save timer, <c>Unload</c>) runs on the CounterStrikeSharp
/// main thread. It therefore takes no locks. <see cref="Changed"/> is raised straight from
/// the mutating call, still on that thread, so a handler must not block on another thread
/// that calls back into the service.</para>
/// </summary>
public sealed class PermissionStore
{
    private readonly Action<string> _warn;
    private readonly Action<string> _error;

    private PermissionData _data = new();
    private Dictionary<string, IReadOnlyList<string>> _groupView = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, GroupClosure> _groupClosures = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<ulong, UserSnapshot> _snapshots = new();

    private int _revision;
    private bool _dirty;

    /// <param name="filePath">Absolute path of <c>permissions.json</c>.</param>
    /// <param name="defaultGroup">Group applied to everybody. Created when missing.</param>
    /// <param name="warn">Sink for recoverable problems (cycles, dangling references).</param>
    /// <param name="error">Sink for hard failures such as unreadable files.</param>
    public PermissionStore(string filePath, string defaultGroup, Action<string> warn, Action<string> error)
    {
        FilePath = filePath;
        DefaultGroup = NormalizeGroupName(defaultGroup);
        _warn = warn;
        _error = error;
    }

    public string FilePath { get; }

    /// <summary>
    /// Group applied to everybody. Assign before <see cref="Load"/> or <see cref="Reload"/>;
    /// both rebuild the group closure cache, so changing it takes effect immediately.
    /// </summary>
    public string DefaultGroup { get; set; }

    /// <summary>True when there are unsaved mutations.</summary>
    public bool IsDirty => _dirty;

    /// <summary>Counter bumped on every mutation. Snapshots compare against it.</summary>
    public int Revision => _revision;

    /// <summary>Raised after any mutation, on the calling (game) thread.</summary>
    public event EventHandler<PermissionsChangedEventArgs>? Changed;

    // ------------------------------------------------------------------- io

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Reads <c>permissions.json</c>, creating it with defaults when it is missing.
    /// Returns false when an existing file could not be parsed; the in memory data is
    /// then reset to defaults rather than left half loaded.
    /// </summary>
    public bool Load()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (!File.Exists(FilePath))
            {
                _data = CreateDefaultData();
                Normalize(_data);
                Invalidate();

                _dirty = true;
                SaveUnlocked();

                _warn($"permissions.json did not exist, created a default one at '{FilePath}'.");
                return true;
            }

            string json = File.ReadAllText(FilePath);

            if (string.IsNullOrWhiteSpace(json))
            {
                _data = CreateDefaultData();
                Normalize(_data);
                Invalidate();
                _dirty = true;
                SaveUnlocked();
                return true;
            }

            PermissionData? parsed = JsonSerializer.Deserialize<PermissionData>(json, ReadOptions);

            if (parsed is null)
            {
                _error($"'{FilePath}' deserialized to null, falling back to defaults.");
                parsed = CreateDefaultData();
            }

            Normalize(parsed);
            _data = parsed;
            Invalidate();

            _dirty = false;

            Validate();
            return true;
        }
        catch (Exception ex)
        {
            _error($"Failed to open or read '{FilePath}': {ex.Message}. Falling back to defaults.");

            _data = CreateDefaultData();
            Normalize(_data);
            Invalidate();
            _dirty = false;

            return false;
        }
    }

    /// <summary>Writes pending changes out. Safe to call when nothing changed.</summary>
    public void Save()
    {
        if (!_dirty)
            return;

        SaveUnlocked();
    }

    /// <summary>Drops unsaved changes and re-reads the file.</summary>
    public bool Reload() => Load();

    private void SaveUnlocked()
    {
        try
        {
            string? directory = Path.GetDirectoryName(FilePath);

            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Write next to the target then swap, so a crash mid write cannot truncate the file.
            string temp = FilePath + ".tmp";

            File.WriteAllText(temp, JsonSerializer.Serialize(_data, WriteOptions));
            File.Move(temp, FilePath, overwrite: true);

            _dirty = false;
        }
        catch (Exception ex)
        {
            // _dirty stays set so the next flush retries.
            _error($"Failed to write '{FilePath}': {ex.Message}");
        }
    }

    private PermissionData CreateDefaultData()
    {
        PermissionData data = new();

        // The spec's starting point: nothing but an empty default group. Anything else has
        // to be granted on purpose, so a fresh install never silently hands out access.
        data.Groups[DefaultGroup] = [];

        return data;
    }

    /// <summary>
    /// Repairs whatever deserialization handed us: case insensitive dictionaries, trimmed
    /// lower cased keys, no null collections, SteamID64 only user keys, default group present.
    /// </summary>
    private void Normalize(PermissionData data)
    {
        data.Version = data.Version <= 0 ? 1 : data.Version;

        Dictionary<string, List<string>> groups = new(StringComparer.OrdinalIgnoreCase);

        if (data.Groups is not null)
        {
            foreach ((string? key, List<string>? entries) in data.Groups)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                groups[key.Trim().ToLowerInvariant()] = NormalizeEntries(entries);
            }
        }

        if (!groups.ContainsKey(DefaultGroup))
            groups[DefaultGroup] = [];

        data.Groups = groups;

        Dictionary<string, PermissionUserEntry> users = new(StringComparer.OrdinalIgnoreCase);

        if (data.Users is not null)
        {
            foreach ((string? key, PermissionUserEntry? entry) in data.Users)
            {
                if (key is null || !ulong.TryParse(key.Trim(), out ulong steamId))
                    continue;

                // A fresh instance per user. Never hand out a shared default: a "users" entry
                // that is literally `null` in the file would otherwise alias every other one.
                PermissionUserEntry source = entry ?? new PermissionUserEntry();

                source.Groups = Dedupe(source.Groups, static value => value.Trim().ToLowerInvariant());
                source.Nodes = Dedupe(source.Nodes, NodeMatcher.NormalizePattern);

                users[steamId.ToString()] = source;
            }
        }

        data.Users = users;
    }

    private static List<string> NormalizeEntries(List<string>? entries)
    {
        List<string> patterns = new();

        if (entries is null)
            return patterns;

        foreach (string? entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            string trimmed = entry.Trim();
            string? parent = PermissionNodes.GroupReferenceOf(trimmed);

            patterns.Add(parent is not null
                ? PermissionNodes.GroupParentPrefix + parent.ToLowerInvariant()
                : NodeMatcher.NormalizePattern(trimmed));
        }

        return patterns;
    }

    private static List<string> Dedupe(List<string>? values, Func<string, string> normalize)
    {
        List<string> result = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        if (values is null)
            return result;

        foreach (string? value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string normalized = normalize(value);

            if (seen.Add(normalized))
                result.Add(normalized);
        }

        return result;
    }

    /// <summary>Reports cycles and dangling references once per load.</summary>
    private void Validate()
    {
        HashSet<string> cycles = new(StringComparer.OrdinalIgnoreCase);

        foreach (string group in _groupView.Keys)
            GroupResolver.FlattenDetailed(_groupView, group, cycle => cycles.Add(cycle));

        foreach (string group in cycles)
            _warn($"Group '{group}' takes part in an inheritance cycle. The offending entry was ignored.");

        foreach ((string group, List<string> entries) in _data.Groups)
        {
            foreach (string entry in entries)
            {
                string? parent = PermissionNodes.GroupReferenceOf(entry);

                if (parent is not null && !_data.Groups.ContainsKey(parent))
                    _warn($"Group '{group}' inherits unknown group '{parent}'.");
            }
        }

        foreach ((string steamId, PermissionUserEntry user) in _data.Users)
        {
            foreach (string group in user.Groups)
            {
                if (!_data.Groups.ContainsKey(group))
                    _warn($"User {steamId} is in unknown group '{group}'. Use css_perm_group_create to add it.");
            }
        }
    }

    // -------------------------------------------------------------- internals

    private static string NormalizeGroupName(string? group)
    {
        string name = NodeMatcher.Normalize(group);

        return name.Length == 0 ? "default" : name;
    }

    private void Invalidate()
    {
        _revision++;

        // Dictionary<string, List<string>> is not covariant into IReadOnlyDictionary<string,
        // IReadOnlyList<string>>, so rebuild the read only view the resolvers consume. The
        // List instances are shared, not copied.
        _groupView = new Dictionary<string, IReadOnlyList<string>>(_data.Groups.Count, StringComparer.OrdinalIgnoreCase);

        foreach ((string name, List<string> entries) in _data.Groups)
            _groupView[name] = entries;

        _groupClosures.Clear();
        _snapshots.Clear();
    }

    private void MarkDirty(PermissionsChangeKind kind, string? detail)
    {
        _dirty = true;
        Invalidate();
        Changed?.Invoke(this, new PermissionsChangedEventArgs(kind, detail));
    }

    private GroupClosure ClosureOf(string group)
    {
        if (_groupClosures.TryGetValue(group, out GroupClosure? cached))
            return cached;

        GroupClosure closure = GroupResolver.FlattenDetailed(_groupView, group, cycle =>
            _warn($"Group '{cycle}' takes part in an inheritance cycle, ignoring the repeated edge."));

        _groupClosures[group] = closure;

        return closure;
    }

    private PermissionUserEntry UserOrCreate(ulong steamId)
    {
        if (_data.Users.TryGetValue(steamId.ToString(), out PermissionUserEntry? existing))
            return existing;

        PermissionUserEntry created = new();
        _data.Users[steamId.ToString()] = created;

        return created;
    }

    // ---------------------------------------------------------------- queries

    /// <summary>Everything a single user resolves against, cached until the data changes.</summary>
    public UserSnapshot GetSnapshot(ulong steamId)
    {
        if (_snapshots.TryGetValue(steamId, out UserSnapshot? cached) && cached.Revision == _revision)
            return cached;

        PermissionUserEntry entry = _data.Users.TryGetValue(steamId.ToString(), out PermissionUserEntry? found)
            ? found
            : new PermissionUserEntry();

        List<string> groups = new(entry.Groups);

        if (!groups.Contains(DefaultGroup, StringComparer.OrdinalIgnoreCase))
            groups.Add(DefaultGroup);

        HashSet<string> groupNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (string group in groups)
        {
            // The assigned name counts even when the group is not defined yet, otherwise
            // InGroup would report false for a group nobody bothered to create.
            groupNames.Add(group);

            foreach (string inherited in ClosureOf(group).GroupNames)
                groupNames.Add(inherited);
        }

        List<PermissionLayer> layers = PermissionResolver.BuildLayers(
            DefaultGroup,
            ClosureOf(DefaultGroup).Nodes,
            groups,
            group => ClosureOf(group).Nodes,
            entry.Nodes.ToArray());

        UserSnapshot snapshot = new(
            _revision,
            layers,
            groupNames,
            groups,
            entry.Nodes.ToArray());

        _snapshots[steamId] = snapshot;

        return snapshot;
    }

    /// <summary>Resolves a node for a SteamID64, reporting which layer decided.</summary>
    public PermissionResolution Resolve(ulong steamId, string node)
    {
        return PermissionResolver.Resolve(node, GetSnapshot(steamId).Layers);
    }

    /// <summary>True when the user belongs to the group, directly or through inheritance.</summary>
    public bool IsInGroup(ulong steamId, string group)
    {
        if (string.IsNullOrWhiteSpace(group))
            return false;

        return GetSnapshot(steamId).GroupNames.Contains(group.Trim());
    }

    /// <summary>Groups directly assigned to the user, excluding the implicit default group.</summary>
    public IReadOnlyList<string> GetAssignedGroups(ulong steamId) => Copy(_data, steamId, static entry => entry.Groups);

    /// <summary>Nodes assigned directly to the user.</summary>
    public IReadOnlyList<string> GetAssignedNodes(ulong steamId) => Copy(_data, steamId, static entry => entry.Nodes);

    private static string[] Copy(PermissionData data, ulong steamId, Func<PermissionUserEntry, List<string>> selector)
    {
        return data.Users.TryGetValue(steamId.ToString(), out PermissionUserEntry? entry)
            ? selector(entry).ToArray()
            : [];
    }

    /// <summary>How many users have an entry in the file.</summary>
    public int GetAssignedUserCount() => _data.Users.Count;

    public IReadOnlyCollection<string> GetGroupNames() => _data.Groups.Keys.ToArray();

    /// <summary>Raw node patterns of a group, including unresolved <c>@parent</c> references.</summary>
    public IReadOnlyList<string> GetGroupEntries(string group)
    {
        return _data.Groups.TryGetValue(group, out List<string>? entries) ? entries.ToArray() : [];
    }

    /// <summary>Flattened nodes of a group, with inheritance resolved and duplicates dropped.</summary>
    public IReadOnlyList<string> GetGroupClosure(string group) => ClosureOf(group).Nodes.ToArray();

    /// <summary>Names of the resolution layers a user resolves against, least specific first.</summary>
    public IReadOnlyList<string> GetUserLayerNames(ulong steamId) => GetSnapshot(steamId).Layers.Select(static layer => layer.Name).ToArray();

    // -------------------------------------------------------------- mutations

    public bool EnsureGroup(string group)
    {
        string name = NodeMatcher.Normalize(group);

        if (name.Length == 0 || _data.Groups.ContainsKey(name))
            return false;

        _data.Groups[name] = [];
        MarkDirty(PermissionsChangeKind.GroupNodes, name);

        return true;
    }

    public bool GrantGroupNode(string group, string node)
    {
        string name = NodeMatcher.Normalize(group);
        string pattern = NormalizeEntry(node);

        if (name.Length == 0 || pattern.Length == 0)
            return false;

        if (!_data.Groups.TryGetValue(name, out List<string>? entries))
        {
            entries = [];
            _data.Groups[name] = entries;
        }

        if (entries.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            return false;

        entries.Add(pattern);
        MarkDirty(PermissionsChangeKind.GroupNodes, $"{name}: {pattern}");

        return true;
    }

    public bool RevokeGroupNode(string group, string node)
    {
        string name = NodeMatcher.Normalize(group);
        string pattern = NormalizeEntry(node);

        if (name.Length == 0)
            return false;

        if (!_data.Groups.TryGetValue(name, out List<string>? entries))
            return false;

        int index = entries.FindIndex(e => string.Equals(e, pattern, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        entries.RemoveAt(index);
        MarkDirty(PermissionsChangeKind.GroupNodes, $"{name}: {pattern}");

        return true;
    }

    public bool GrantNode(ulong steamId, string node)
    {
        string pattern = NodeMatcher.NormalizePattern(node);

        if (pattern.Length == 0)
            return false;

        PermissionUserEntry entry = UserOrCreate(steamId);

        if (entry.Nodes.Contains(pattern, StringComparer.OrdinalIgnoreCase))
            return false;

        entry.Nodes.Add(pattern);
        MarkDirty(PermissionsChangeKind.UserNodes, $"{steamId}: {pattern}");

        return true;
    }

    public bool RevokeNode(ulong steamId, string node)
    {
        string pattern = NodeMatcher.NormalizePattern(node);

        if (pattern.Length == 0)
            return false;

        if (!_data.Users.TryGetValue(steamId.ToString(), out PermissionUserEntry? entry))
            return false;

        int index = entry.Nodes.FindIndex(n => string.Equals(n, pattern, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        entry.Nodes.RemoveAt(index);
        MarkDirty(PermissionsChangeKind.UserNodes, $"{steamId}: {pattern}");

        return true;
    }

    public bool GrantGroup(ulong steamId, string group)
    {
        string name = NodeMatcher.Normalize(group);

        if (name.Length == 0)
            return false;

        PermissionUserEntry entry = UserOrCreate(steamId);

        if (entry.Groups.Contains(name, StringComparer.OrdinalIgnoreCase))
            return false;

        entry.Groups.Add(name);
        MarkDirty(PermissionsChangeKind.UserGroups, $"{steamId}: {name}");

        return true;
    }

    public bool RevokeGroup(ulong steamId, string group)
    {
        string name = NodeMatcher.Normalize(group);

        if (name.Length == 0)
            return false;

        if (!_data.Users.TryGetValue(steamId.ToString(), out PermissionUserEntry? entry))
            return false;

        int index = entry.Groups.FindIndex(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
            return false;

        entry.Groups.RemoveAt(index);
        MarkDirty(PermissionsChangeKind.UserGroups, $"{steamId}: {name}");

        return true;
    }

    public bool DeleteGroup(string group)
    {
        string name = NodeMatcher.Normalize(group);

        if (name.Length == 0 || name == DefaultGroup || !_data.Groups.Remove(name))
            return false;

        foreach (PermissionUserEntry entry in _data.Users.Values)
            entry.Groups.RemoveAll(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        MarkDirty(PermissionsChangeKind.GroupNodes, name);

        return true;
    }

    private static string NormalizeEntry(string entry)
    {
        string? parent = PermissionNodes.GroupReferenceOf(entry);

        return parent is not null
            ? PermissionNodes.GroupParentPrefix + parent.ToLowerInvariant()
            : NodeMatcher.NormalizePattern(entry);
    }
}

/// <summary>Immutable per user view of the permission data at one revision.</summary>
public sealed class UserSnapshot
{
    public UserSnapshot(
        int revision,
        List<PermissionLayer> layers,
        HashSet<string> groupNames,
        List<string> assignedGroups,
        string[] directNodes)
    {
        Revision = revision;
        Layers = layers;
        GroupNames = groupNames;
        AssignedGroups = assignedGroups;
        DirectNodes = directNodes;
    }

    /// <summary>Data revision this snapshot was built from.</summary>
    public int Revision { get; }

    /// <summary>Ordered resolution layers, least specific first. Treat as read only.</summary>
    public List<PermissionLayer> Layers { get; }

    /// <summary>Group names the user belongs to, including everything inherited.</summary>
    public HashSet<string> GroupNames { get; }

    /// <summary>Groups assigned to the user plus the implicit default group.</summary>
    public List<string> AssignedGroups { get; }

    /// <summary>Nodes assigned directly to the user.</summary>
    public IReadOnlyList<string> DirectNodes { get; }
}