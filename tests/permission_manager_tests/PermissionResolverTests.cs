using PermissionManagerApi;
using Xunit;

namespace permission_manager.tests;

/// <summary>
/// Resolution order: default group, then user groups, then direct nodes, last match wins.
/// </summary>
public class PermissionResolverTests
{
    private static PermissionResolution Resolve(
        string node,
        string[]? defaultGroup = null,
        string[]? groups = null,
        string[]? direct = null)
    {
        return PermissionResolver.Resolve(node, defaultGroup, groups, direct);
    }

    [Fact]
    public void No_layers_means_no_decision()
    {
        PermissionResolution resolution = Resolve("fov.use");

        Assert.Equal(PermissionDecision.Unset, resolution.Decision);
        Assert.False(resolution.Explicit);
        Assert.False(resolution.Allowed);
    }

    [Fact]
    public void Blank_node_is_never_resolved()
    {
        Assert.Equal(PermissionDecision.Unset, Resolve("  ", ["*"]).Decision);
    }

    [Fact]
    public void Default_group_alone_allows()
    {
        PermissionResolution resolution = Resolve("fov.use", ["fov.use"]);

        Assert.Equal(PermissionDecision.Allow, resolution.Decision);
        Assert.Equal("default", resolution.Source);
        Assert.Equal("fov.use", resolution.Pattern);
    }

    [Fact]
    public void Group_beats_default_group()
    {
        // default says allow, the group says deny, so the group wins.
        PermissionResolution resolution = Resolve("fov.use", ["fov.use"], ["-fov.use"]);

        Assert.Equal(PermissionDecision.Deny, resolution.Decision);
        Assert.Equal("group", resolution.Source);
    }

    [Fact]
    public void Direct_node_beats_group()
    {
        PermissionResolution resolution = Resolve("fov.use", null, ["-fov.use"], ["fov.use"]);

        Assert.Equal(PermissionDecision.Allow, resolution.Decision);
        Assert.Equal("user", resolution.Source);
    }

    [Fact]
    public void Direct_node_can_also_deny_over_a_group_allow()
    {
        PermissionResolution resolution = Resolve("fov.use", null, ["fov.use"], ["-fov.use"]);

        Assert.Equal(PermissionDecision.Deny, resolution.Decision);
        Assert.Equal("user", resolution.Source);
    }

    [Fact]
    public void Last_pattern_inside_one_layer_wins()
    {
        Assert.Equal(PermissionDecision.Deny, Resolve("fov.use", ["fov.use", "-fov.use"]).Decision);
        Assert.Equal(PermissionDecision.Allow, Resolve("fov.use", ["-fov.use", "fov.use"]).Decision);
    }

    [Fact]
    public void A_group_with_star_overrides_a_narrower_default_allow()
    {
        // The spec's classic case: `default: ["fov.use"]`, group `vip: ["fov.*"]`.
        PermissionResolution resolution = Resolve("fov.setvalue", ["fov.use"], ["fov.*"]);

        Assert.Equal(PermissionDecision.Allow, resolution.Decision);
        Assert.Equal("fov.*", resolution.Pattern);
    }

    [Fact]
    public void Unmatched_layers_do_not_disturb_an_earlier_decision()
    {
        // The direct layer exists but does not mention this node.
        PermissionResolution resolution = Resolve("shop.use", ["*"], ["-fov.use"], ["fov.setvalue"]);

        Assert.Equal(PermissionDecision.Allow, resolution.Decision);
        Assert.Equal("default", resolution.Source);
    }

    [Fact]
    public void Denying_node_wins_over_a_broader_star_in_an_earlier_layer()
    {
        // admin = ["*", "-perm.admin"]: the star allows everything, the deny carves out one node.
        Assert.Equal(PermissionDecision.Deny, Resolve("perm.admin", ["*", "-perm.admin"]).Decision);
        Assert.Equal(PermissionDecision.Allow, Resolve("fov.use", ["*", "-perm.admin"]).Decision);
    }

    [Fact]
    public void Named_layers_report_which_group_decided()
    {
        PermissionLayer[] layers =
        [
            PermissionLayer.Default([]),
            PermissionLayer.Group("vip", ["fov.*"]),
            PermissionLayer.User(["-fov.use"]),
        ];

        PermissionResolution resolution = PermissionResolver.Resolve("fov.use", layers);

        Assert.Equal(PermissionDecision.Deny, resolution.Decision);
        Assert.Equal("user", resolution.Source);

        PermissionResolution allow = PermissionResolver.Resolve("fov.other", layers);

        Assert.Equal(PermissionDecision.Allow, allow.Decision);
        Assert.Equal("vip", allow.Source);
    }

    [Fact]
    public void Groups_are_evaluated_in_order_so_the_later_one_wins()
    {
        PermissionLayer[] layers =
        [
            PermissionLayer.Group("a", ["-fov.use"]),
            PermissionLayer.Group("b", ["fov.use"]),
        ];

        Assert.Equal(PermissionDecision.Allow, PermissionResolver.Resolve("fov.use", layers).Decision);

        PermissionLayer[] reversed =
        [
            PermissionLayer.Group("b", ["fov.use"]),
            PermissionLayer.Group("a", ["-fov.use"]),
        ];

        Assert.Equal(PermissionDecision.Deny, PermissionResolver.Resolve("fov.use", reversed).Decision);
    }

    [Fact]
    public void Bare_negation_still_denies_everything()
    {
        Assert.Equal(PermissionDecision.Deny, Resolve("anything", ["-*"]).Decision);
        Assert.Equal(PermissionDecision.Deny, Resolve("anything", [], ["-*"]).Decision);
        Assert.Equal(PermissionDecision.Deny, Resolve("anything", [], [], ["-*"]).Decision);
    }

    [Fact]
    public void ToString_describes_the_outcome()
    {
        PermissionResolution deny = Resolve("fov.use", null, null, ["-fov.*"]);

        Assert.Equal("deny by '-fov.*' (layer: user)", deny.ToString());
        Assert.Equal("unset", PermissionResolution.None.ToString());
        Assert.True(deny.Denied);
        Assert.True(deny.Explicit);
    }
}

/// <summary>
/// <see cref="PermissionResolver.BuildLayers"/>: ordering and default group de-duplication.
/// </summary>
public class PermissionLayerBuilderTests
{
    [Fact]
    public void Empty_layers_are_dropped_so_the_list_stays_short()
    {
        List<PermissionLayer> layers = PermissionResolver.BuildLayers(
            "default",
            [],
            ["vip"],
            _ => [],
            []);

        Assert.Empty(layers);
    }

    [Fact]
    public void Default_group_is_not_repeated_when_the_user_is_explicitly_in_it()
    {
        List<PermissionLayer> layers = PermissionResolver.BuildLayers(
            "default",
            ["fov.use"],
            ["default", "vip"],
            name => name == "vip" ? ["vip.node"] : ["should.not.appear.twice"],
            []);

        Assert.Equal(["default", "vip"], layers.Select(l => l.Name).ToArray());
    }

    [Fact]
    public void Layers_come_out_least_specific_first()
    {
        IReadOnlyList<string> one = ["one"];
        IReadOnlyList<string> two = ["two"];

        List<PermissionLayer> layers = PermissionResolver.BuildLayers(
            "default",
            ["d"],
            ["g1", "g2"],
            name => name == "g1" ? one : two,
            ["u"]);

        Assert.Equal(["default", "g1", "g2", "user"], layers.Select(l => l.Name).ToArray());

        PermissionResolution resolution = PermissionResolver.Resolve("one", layers);

        Assert.Equal("g1", resolution.Source);
    }

    [Fact]
    public void Built_layers_resolve_end_to_end()
    {
        List<PermissionLayer> layers = PermissionResolver.BuildLayers(
            "default",
            ["shop.use"],
            ["vip"],
            _ => ["fov.*"],
            ["-fov.setvalue"]);

        Assert.Equal(PermissionDecision.Allow, PermissionResolver.Resolve("fov.use", layers).Decision);
        Assert.Equal(PermissionDecision.Deny, PermissionResolver.Resolve("fov.setvalue", layers).Decision);
        Assert.Equal(PermissionDecision.Allow, PermissionResolver.Resolve("shop.use", layers).Decision);
        Assert.Equal(PermissionDecision.Unset, PermissionResolver.Resolve("other.node", layers).Decision);
    }
}