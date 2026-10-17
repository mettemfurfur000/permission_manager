using PermissionManagerApi;
using Xunit;

namespace permission_manager.tests;

/// <summary>
/// Group inheritance: <c>@parent</c> flattening, ordering and cycle handling.
/// </summary>
public class GroupResolverTests
{
    private static Dictionary<string, IReadOnlyList<string>> Groups(params (string Name, string[] Nodes)[] groups)
    {
        Dictionary<string, IReadOnlyList<string>> table = new(StringComparer.OrdinalIgnoreCase);

        foreach ((string name, string[] nodes) in groups)
            table[name] = nodes;

        return table;
    }

    [Fact]
    public void Leaf_group_flattens_to_its_own_nodes()
    {
        GroupClosure closure = GroupResolver.FlattenDetailed(
            Groups(("vip", ["fov.use", "fov.fovvalue"])),
            "vip");

        Assert.Equal(["fov.use", "fov.fovvalue"], closure.Nodes);
        Assert.Equal(["vip"], closure.GroupNames);
    }

    [Fact]
    public void Unknown_group_is_empty()
    {
        Assert.Empty(GroupResolver.Flatten(Groups(("vip", ["*"])), "nope").ToList());

        Assert.Same(GroupClosure.Empty, GroupResolver.FlattenDetailed(null, "vip"));
        Assert.Same(GroupClosure.Empty, GroupResolver.FlattenDetailed(Groups(("vip", ["*"])), " "));
    }

    [Fact]
    public void Inherited_nodes_come_first_so_children_can_override_them()
    {
        Dictionary<string, IReadOnlyList<string>> table = Groups(
            ("vip", ["fov.use"]),
            ("moderator", ["@vip", "shop.use"]),
            ("admin", ["@moderator", "*"]));

        Assert.Equal(["fov.use", "shop.use", "*"], GroupResolver.Flatten(table, "admin"));
    }

    [Fact]
    public void Inherited_deny_can_be_overridden_by_the_child_group()
    {
        Dictionary<string, IReadOnlyList<string>> table = Groups(
            ("vip", ["*", "-perm.admin"]),
            ("admin", ["@vip", "perm.admin"]));

        List<PermissionLayer> layers =
        [
            PermissionLayer.Group("admin", GroupResolver.Flatten(table, "admin")),
        ];

        Assert.Equal(PermissionDecision.Allow, PermissionResolver.Resolve("perm.admin", layers).Decision);
        Assert.Equal(PermissionDecision.Allow, PermissionResolver.Resolve("shop.use", layers).Decision);
    }

    [Fact]
    public void Group_names_include_every_ancestor()
    {
        HashSet<string> names = GroupResolver.ClosureNames(Groups(
            ("vip", ["a"]),
            ("moderator", ["@vip", "b"]),
            ("admin", ["@moderator", "c"])),
            "admin");

        Assert.Contains("vip", names);
        Assert.Contains("moderator", names);
        Assert.Contains("admin", names);
        Assert.Equal(3, names.Count);
    }

    [Fact]
    public void Direct_cycles_terminate_and_are_reported_once()
    {
        List<string> cycles = new();

        List<string> nodes = GroupResolver.Flatten(
            Groups(("a", ["@b", "a.node"]), ("b", ["@a", "b.node"])),
            "a",
            cycles.Add);

        Assert.Equal(["b.node", "a.node"], nodes);
        Assert.Equal(["a"], cycles);
    }

    [Fact]
    public void Self_inheritance_is_a_cycle()
    {
        List<string> cycles = new();

        List<string> nodes = GroupResolver.Flatten(Groups(("a", ["@a", "a.node"])), "a", cycles.Add);

        Assert.Equal(["a.node"], nodes);
        Assert.Equal(["a"], cycles);
    }

    [Fact]
    public void Longer_cycles_terminate()
    {
        List<string> cycles = new();

        List<string> nodes = GroupResolver.Flatten(
            Groups(("a", ["@b"]), ("b", ["@c"]), ("c", ["@a", "c.node"])),
            "a",
            cycles.Add);

        Assert.Equal(["c.node"], nodes);
        Assert.Equal(["a"], cycles);
    }

    [Fact]
    public void Dangling_parent_is_skipped_without_error()
    {
        List<string> nodes = GroupResolver.Flatten(Groups(("a", ["@ghost", "a.node"])), "a");

        Assert.Equal(["a.node"], nodes);
    }

    [Fact]
    public void Blank_entries_are_dropped()
    {
        Assert.Equal(["keep"], GroupResolver.Flatten(Groups(("a", ["", "   ", "keep"])), "a"));
    }

    [Fact]
    public void FlattenAll_drops_duplicates_from_diamond_inheritance()
    {
        Dictionary<string, IReadOnlyList<string>> table = Groups(
            ("base", ["shared", "base.only"]),
            ("left", ["@base", "left.only"]),
            ("right", ["@base", "right.only"]),
            ("top", ["@left", "@right"]));

        Assert.Equal(
            ["shared", "base.only", "left.only", "right.only"],
            GroupResolver.FlattenAll(table, ["top"]));

        Assert.Equal(
            ["shared", "base.only", "left.only", "right.only"],
            GroupResolver.FlattenAll(table, ["left", "right", "base"]));
    }

    [Fact]
    public void FlattenAll_tolerates_empty_inputs()
    {
        Assert.Empty(GroupResolver.FlattenAll(null, ["a"]));
        Assert.Empty(GroupResolver.FlattenAll(Groups(("a", ["x"])), []));
    }

    [Fact]
    public void Parent_entries_are_not_treated_as_nodes()
    {
        GroupClosure closure = GroupResolver.FlattenDetailed(
            Groups(("base", ["base.node"]), ("child", ["@base", "child.node"])),
            "child");

        Assert.DoesNotContain("@base", closure.Nodes);
        Assert.Contains("base.node", closure.Nodes);
        Assert.Contains("child.node", closure.Nodes);
    }
}