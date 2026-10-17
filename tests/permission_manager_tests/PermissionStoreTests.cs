using permission_manager.src.storage;
using PermissionManagerApi;
using Xunit;

namespace permission_manager.tests;

/// <summary>
/// <see cref="PermissionStore"/>: JSON round trips, normalisation, snapshot caching and
/// the refresh-on-mutation guarantee. Uses a throwaway folder per test.
/// </summary>
public sealed class PermissionStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly List<string> _warnings = [];
    private readonly List<string> _errors = [];

    public PermissionStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "permission_manager_tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // Windows sometimes keeps the handle briefly. Not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    private PermissionStore NewStore(string defaultGroup = "default") =>
        new(Path.Combine(_directory, "permissions.json"), defaultGroup, _warnings.Add, _errors.Add);

    private void WriteFile(string json) =>
        File.WriteAllText(Path.Combine(_directory, "permissions.json"), json);

    // ------------------------------------------------------------------ loading

    [Fact]
    public void Missing_file_creates_defaults_including_the_default_group()
    {
        PermissionStore store = NewStore();

        Assert.True(store.Load());
        Assert.Contains("default", store.GetGroupNames());
        Assert.NotEmpty(_warnings);
        Assert.Empty(_errors);
        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Spec_shape_loads_and_resolves()
    {
        WriteFile("""
        {
          "groups": { "vip": ["fov.use"], "admin": ["*"] },
          "users": { "76561198000000000": { "groups": ["vip"], "nodes": [] } }
        }
        """);

        PermissionStore store = NewStore();
        Assert.True(store.Load());

        const ulong vip = 76561198000000000;

        Assert.True(store.IsInGroup(vip, "vip"));
        Assert.Equal(PermissionDecision.Allow, store.Resolve(vip, "fov.use").Decision);

        // Not in admin, so the '*' grant does not apply.
        Assert.Equal(PermissionDecision.Unset, store.Resolve(vip, "perm.admin").Decision);
    }

    [Fact]
    public void Malformed_json_falls_back_to_defaults_and_reports_the_error()
    {
        WriteFile("{ this is not json");

        PermissionStore store = NewStore();

        Assert.False(store.Load());
        Assert.NotEmpty(_errors);
        Assert.Contains("default", store.GetGroupNames());
    }

    [Fact]
    public void Null_collections_in_the_file_do_not_crash_the_load()
    {
        WriteFile("""{ "groups": null, "users": null }""");

        PermissionStore store = NewStore();

        Assert.True(store.Load());
        Assert.Contains("default", store.GetGroupNames());
    }

    [Fact]
    public void Pascal_case_keys_are_accepted()
    {
        WriteFile("""{ "Groups": { "VIP": ["fov.use"] }, "Users": { "76561198000000001": { "Groups": ["vip"] } } }""");

        PermissionStore store = NewStore();

        Assert.True(store.Load());
        Assert.True(store.IsInGroup(76561198000000001UL, "vip"));
    }

    [Fact]
    public void Names_are_trimmed_and_lowercased()
    {
        WriteFile("""{ "groups": { "  VIP  ": ["  FOV.USE  "] }, "users": { "76561198000000002": { "groups": [" VIP "], "nodes": [" Foo.Bar "] } } }""");

        PermissionStore store = NewStore();
        Assert.True(store.Load());

        Assert.Equal(["fov.use"], store.GetGroupEntries("vip"));
        Assert.Equal(["foo.bar"], store.GetAssignedNodes(76561198000000002UL));
    }

    [Fact]
    public void User_keys_that_are_not_steamid64_are_dropped()
    {
        WriteFile("""{ "groups": {}, "users": { "bob": { "groups": [] }, "76561198000000003": { "groups": [] } } }""");

        PermissionStore store = NewStore();

        Assert.True(store.Load());
        Assert.Equal(1, store.GetAssignedUserCount());
    }

    [Fact]
    public void Cycles_and_dangling_references_are_reported_once_on_load()
    {
        WriteFile("""
        {
          "groups": { "a": ["@b"], "b": ["@a"], "c": ["@ghost"] },
          "users": { "76561198000000004": { "groups": ["nowhere"] } }
        }
        """);

        PermissionStore store = NewStore();
        Assert.True(store.Load());

        Assert.Contains(_warnings, w => w.Contains("inheritance cycle"));
        Assert.Contains(_warnings, w => w.Contains("unknown group 'ghost'"));
        Assert.Contains(_warnings, w => w.Contains("unknown group 'nowhere'"));
    }

    // ---------------------------------------------------------------- mutations

    [Fact]
    public void Granting_the_same_node_twice_reports_no_change()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000010UL;

        Assert.True(store.GrantNode(id, "fov.use"));
        Assert.False(store.GrantNode(id, "FOV.USE"));
        Assert.Equal(["fov.use"], store.GetAssignedNodes(id));
    }

    [Fact]
    public void Revoking_a_missing_node_reports_no_change()
    {
        PermissionStore store = NewStore();
        store.Load();

        Assert.False(store.RevokeNode(76561198000000011UL, "nope"));
        Assert.True(store.GrantNode(76561198000000011UL, "nope"));
        Assert.True(store.RevokeNode(76561198000000011UL, "nope"));
    }

    [Fact]
    public void Granting_a_group_to_an_unknown_user_creates_the_entry()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000012UL;

        Assert.False(store.IsInGroup(id, "vip"));
        Assert.True(store.GrantGroup(id, "vip"));
        Assert.True(store.IsInGroup(id, "vip"));
        Assert.False(store.GrantGroup(id, "vip"));
        Assert.True(store.RevokeGroup(id, "vip"));
        Assert.False(store.RevokeGroup(id, "vip"));
    }

    [Fact]
    public void Assigned_group_counts_even_when_it_was_never_defined()
    {
        // Regression: membership must not depend on the group having a node list.
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000023UL;

        Assert.True(store.GrantGroup(id, "undefined-group"));

        Assert.True(store.IsInGroup(id, "undefined-group"));
        Assert.Equal(PermissionDecision.Unset, store.Resolve(id, "any.node").Decision);

        // Granting nodes to the group later must be picked up by the existing membership.
        store.GrantGroupNode("undefined-group", "any.node");

        Assert.Equal(PermissionDecision.Allow, store.Resolve(id, "any.node").Decision);
    }

    [Fact]
    public void Granting_a_node_to_a_group_creates_the_group()
    {
        PermissionStore store = NewStore();
        store.Load();

        Assert.True(store.GrantGroupNode("brandnew", "a.node"));
        Assert.Contains("brandnew", store.GetGroupNames());
        Assert.False(store.GrantGroupNode("brandnew", "a.node"));
        Assert.True(store.RevokeGroupNode("brandnew", "a.node"));
    }

    [Fact]
    public void Group_nodes_accept_parent_references()
    {
        PermissionStore store = NewStore();
        store.Load();

        store.GrantGroupNode("vip", "fov.use");
        store.GrantGroupNode("admin", "@VIP");

        Assert.Equal(["@vip"], store.GetGroupEntries("admin"));
        Assert.Equal(["fov.use"], store.GetGroupClosure("admin"));
    }

    [Fact]
    public void Deleting_a_group_drops_it_from_every_user()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000013UL;

        store.EnsureGroup("vip");
        store.GrantGroup(id, "vip");

        Assert.True(store.DeleteGroup("vip"));
        Assert.DoesNotContain("vip", store.GetGroupNames());
        Assert.False(store.IsInGroup(id, "vip"));
        Assert.False(store.DeleteGroup("vip"));
    }

    [Fact]
    public void The_default_group_cannot_be_deleted()
    {
        PermissionStore store = NewStore();
        store.Load();

        Assert.False(store.DeleteGroup("default"));
    }

    // ----------------------------------------------------------------- snapshots

    [Fact]
    public void Every_user_resolves_against_the_default_group()
    {
        PermissionStore store = NewStore();
        store.Load();

        store.GrantGroupNode("default", "shop.use");

        Assert.Equal(PermissionDecision.Allow, store.Resolve(999999UL, "shop.use").Decision);

        // GetAssignedGroups reports what is written in the file; the default group is
        // applied implicitly and never stored on the user.
        Assert.Empty(store.GetAssignedGroups(999999UL));
        Assert.Contains("default", store.GetSnapshot(999999UL).AssignedGroups);
    }

    [Fact]
    public void Snapshots_refresh_immediately_after_a_mutation()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000014UL;
        const string node = "fov.use";

        // Warm the cache with a denial first.
        Assert.Equal(PermissionDecision.Unset, store.Resolve(id, node).Decision);
        int before = store.Revision;

        Assert.True(store.GrantNode(id, node));
        Assert.True(store.Revision > before);
        Assert.Equal(PermissionDecision.Allow, store.Resolve(id, node).Decision);

        Assert.True(store.RevokeNode(id, node));
        Assert.Equal(PermissionDecision.Unset, store.Resolve(id, node).Decision);
    }

    [Fact]
    public void A_group_edit_is_visible_to_members_of_that_group()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000015UL;

        store.EnsureGroup("vip");
        store.GrantGroup(id, "vip");

        Assert.Equal(PermissionDecision.Unset, store.Resolve(id, "fov.use").Decision);

        store.GrantGroupNode("vip", "fov.*");

        Assert.Equal(PermissionDecision.Allow, store.Resolve(id, "fov.use").Decision);

        store.GrantGroupNode("vip", "-fov.use");

        Assert.Equal(PermissionDecision.Deny, store.Resolve(id, "fov.use").Decision);
    }

    [Fact]
    public void Layer_order_is_default_then_groups_then_user()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000016UL;

        store.GrantGroupNode("default", "node.default");
        store.EnsureGroup("vip");
        store.GrantGroupNode("vip", "node.vip");
        store.GrantGroup(id, "vip");
        store.GrantNode(id, "node.user");

        IReadOnlyList<string> layers = store.GetUserLayerNames(id);

        Assert.Equal(["default", "vip", "user"], layers);
    }

    [Fact]
    public void A_direct_node_overrides_the_group_it_came_from()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000017UL;

        store.EnsureGroup("vip");
        store.GrantGroupNode("vip", "fov.use");
        store.GrantGroup(id, "vip");
        store.GrantNode(id, "-fov.use");

        PermissionResolution resolution = store.Resolve(id, "fov.use");

        Assert.Equal(PermissionDecision.Deny, resolution.Decision);
        Assert.Equal("user", resolution.Source);
    }

    [Fact]
    public void Inherited_group_membership_is_reported()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000018UL;

        store.EnsureGroup("vip");
        store.GrantGroupNode("moderator", "@vip");
        store.GrantGroup(id, "moderator");

        Assert.True(store.IsInGroup(id, "moderator"));
        Assert.True(store.IsInGroup(id, "vip"));
        Assert.False(store.IsInGroup(id, " "));
    }

    [Fact]
    public void Null_user_entries_never_share_state()
    {
        // Regression: a literal `null` in the users map used to alias one shared instance,
        // so granting a node to one such user silently granted it to all of them.
        WriteFile("""
        { "groups": {}, "users": { "76561198000000030": null, "76561198000000031": null } }
        """);

        PermissionStore store = NewStore();
        Assert.True(store.Load());

        const ulong first = 76561198000000030UL;
        const ulong second = 76561198000000031UL;

        Assert.True(store.GrantNode(first, "secret.node"));

        Assert.Equal(PermissionDecision.Allow, store.Resolve(first, "secret.node").Decision);
        Assert.Equal(PermissionDecision.Unset, store.Resolve(second, "secret.node").Decision);
        Assert.Empty(store.GetAssignedNodes(second));
    }

    [Fact]
    public void Read_only_accessors_hand_back_copies()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000032UL;

        store.EnsureGroup("vip");
        store.GrantGroup(id, "vip");
        store.GrantNode(id, "fov.use");

        // Whatever the store handed out must not be a window into the data that gets saved.
        TryMutate(store.GetAssignedGroups(id));
        TryMutate(store.GetAssignedNodes(id));
        TryMutate(store.GetGroupEntries("vip"));

        store.Save();
        store.Reload();

        Assert.Equal(["vip"], store.GetAssignedGroups(id));
        Assert.Equal(["fov.use"], store.GetAssignedNodes(id));
        Assert.Empty(store.GetGroupEntries("vip"));
    }

    private static void TryMutate(IReadOnlyList<string> list)
    {
        if (list is not IList<string> mutable)
            return;

        try
        {
            mutable.Add("injected");
        }
        catch (NotSupportedException)
        {
            // A fixed size copy is equally fine; the point is the store stays unaffected.
        }
    }

    [Fact]
    public void Diamond_inheritance_does_not_duplicate_nodes_in_live_layers()
    {
        PermissionStore store = NewStore();
        store.Load();

        store.GrantGroupNode("base", "base.node");
        store.GrantGroupNode("left", "@base");
        store.GrantGroupNode("left", "left.only");
        store.GrantGroupNode("right", "@base");
        store.GrantGroupNode("right", "right.only");
        store.GrantGroupNode("top", "@left");
        store.GrantGroupNode("top", "@right");

        Assert.Equal(["base.node", "left.only", "right.only"], store.GetGroupClosure("top"));
    }

    [Fact]
    public void Deep_diamond_chains_stay_linear()
    {
        // Regression: without per-call memoisation this closure took 100k+ nodes and ~100ms.
        PermissionStore store = NewStore();
        store.Load();

        store.GrantGroupNode("g0", "node.0");

        for (int i = 1; i <= 40; i++)
        {
            store.GrantGroupNode($"g{i}", $"@g{i - 1}");
            store.GrantGroupNode($"g{i}", $"@g{i - 2}");
            store.GrantGroupNode($"g{i}", $"node.{i}");
        }

        IReadOnlyList<string> closure = store.GetGroupClosure("g40");

        Assert.Equal(41, closure.Count);
        Assert.Equal("node.0", closure[0]);
        Assert.Equal("node.40", closure[^1]);
    }

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void Saving_then_reloading_preserves_everything()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000019UL;

        store.EnsureGroup("vip");
        store.GrantGroupNode("vip", "fov.*");
        store.EnsureGroup("admin");
        store.GrantGroupNode("admin", "@vip");
        store.GrantGroupNode("admin", "perm.admin");
        store.GrantGroup(id, "admin");
        store.GrantNode(id, "-fov.setvalue");

        store.Save();

        PermissionStore reloaded = NewStore();
        Assert.True(reloaded.Load());

        Assert.Equal(PermissionDecision.Allow, reloaded.Resolve(id, "fov.use").Decision);
        Assert.Equal(PermissionDecision.Deny, reloaded.Resolve(id, "fov.setvalue").Decision);
        Assert.Equal(["@vip", "perm.admin"], reloaded.GetGroupEntries("admin"));
        Assert.Empty(_errors);
    }

    [Fact]
    public void Saving_writes_readable_json()
    {
        PermissionStore store = NewStore();
        store.Load();

        store.GrantNode(76561198000000020UL, "fov.use");
        store.Save();

        string json = File.ReadAllText(store.FilePath);

        Assert.Contains("\n", json);
        Assert.Contains("\"groups\"", json);
        Assert.Contains("\"users\"", json);
        Assert.Contains("76561198000000020", json);
    }

    [Fact]
    public void Reload_discards_unsaved_edits_and_picks_up_file_edits()
    {
        PermissionStore store = NewStore();
        store.Load();

        const ulong id = 76561198000000021UL;

        store.GrantNode(id, "will.be.lost");
        Assert.Equal(PermissionDecision.Allow, store.Resolve(id, "will.be.lost").Decision);

        WriteFile("""{ "groups": { "default": ["from.file"] } }""");

        Assert.True(store.Reload());

        Assert.Equal(PermissionDecision.Unset, store.Resolve(id, "will.be.lost").Decision);
        Assert.Equal(PermissionDecision.Allow, store.Resolve(id, "from.file").Decision);
    }

    [Fact]
    public void Changes_are_announced()
    {
        PermissionStore store = NewStore();
        store.Load();

        List<PermissionsChangeKind> kinds = [];
        store.Changed += (_, args) => kinds.Add(args.Kind);

        const ulong id = 76561198000000022UL;

        store.GrantNode(id, "a");
        store.RevokeNode(id, "a");
        store.GrantGroup(id, "g");
        store.RevokeGroup(id, "g");
        store.GrantGroupNode("g", "n");

        Assert.Equal(
            [
                PermissionsChangeKind.UserNodes,
                PermissionsChangeKind.UserNodes,
                PermissionsChangeKind.UserGroups,
                PermissionsChangeKind.UserGroups,
                PermissionsChangeKind.GroupNodes,
            ],
            kinds);
    }
}