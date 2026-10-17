using PermissionManagerApi;
using Xunit;

namespace permission_manager.tests;

/// <summary>
/// Wildcard semantics for permission node patterns.
/// </summary>
public class NodeMatcherTests
{
    [Theory]
    [InlineData("fov.use", "fov.use", true)]
    [InlineData("fov.*", "fov.use", true)]
    [InlineData("fov.*", "fov.setvalue", true)]
    [InlineData("fov.*", "fov.use.deep", true)]
    [InlineData("fov.*", "fovuse", false)]
    [InlineData("fov.*", "shop.use", false)]
    [InlineData("*", "anything.at.all", true)]
    [InlineData("*", "", true)]
    [InlineData("*", "fov", true)]
    [InlineData("f?v", "fov", true)]
    [InlineData("f?v", "fo", false)]
    [InlineData("f? v", "fov", false)]
    [InlineData("", "", true)]
    [InlineData("", "fov", false)]
    [InlineData("vip", "vip", true)]
    public void Matches_expected(string pattern, string node, bool expected)
    {
        Assert.Equal(expected, NodeMatcher.Matches(pattern, node));
    }

    [Theory]
    [InlineData("FOV.USE", "fov.use", true)]
    [InlineData("FOV.*", "FoV.Use", true)]
    [InlineData("fov.use", "FOV.USE", true)]
    public void Matching_is_case_insensitive(string pattern, string node, bool expected)
    {
        Assert.Equal(expected, NodeMatcher.Matches(pattern, node));
    }

    [Fact]
    public void Backtracks_over_multiple_stars()
    {
        // A naive matcher without a backtrack point gets this wrong.
        Assert.True(NodeMatcher.Matches("*a*b*c*", "xxaxxbxxcxx"));
        Assert.False(NodeMatcher.Matches("*a*b*c*", "xxaxxcxxbxx"));
    }

    [Fact]
    public void Trailing_star_matches_empty_remainder()
    {
        Assert.True(NodeMatcher.Matches("fov.*", "fov."));
        Assert.True(NodeMatcher.Matches("fov.***", "fov.use"));
    }

    [Fact]
    public void Null_inputs_never_match()
    {
        Assert.False(NodeMatcher.Matches(null, "fov.use"));
        Assert.False(NodeMatcher.Matches("fov.use", null));
        Assert.False(NodeMatcher.Matches(null, null));
    }

    [Fact]
    public void MatchesAny_short_circuits_on_the_first_hit()
    {
        Assert.True(NodeMatcher.MatchesAny(["a.b", "fov.*", "*"], "fov.use"));
        Assert.False(NodeMatcher.MatchesAny(["a.b", "c.*"], "fov.use"));
        Assert.False(NodeMatcher.MatchesAny(null, "fov.use"));
        Assert.False(NodeMatcher.MatchesAny([], "fov.use"));
    }

    [Theory]
    [InlineData("  fov.use  ", "fov.use")]
    [InlineData("FOV.Use", "fov.use")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Normalize_trims_and_lowercases(string? input, string expected)
    {
        Assert.Equal(expected, NodeMatcher.Normalize(input));
    }

    [Theory]
    [InlineData("-FOV.Use", "-fov.use")]
    [InlineData("fov.use", "fov.use")]
    [InlineData("-", "-*")]
    [InlineData("-  FOV ", "-fov")]
    public void NormalizePattern_keeps_the_negation_sign(string input, string expected)
    {
        Assert.Equal(expected, NodeMatcher.NormalizePattern(input));
    }

    [Theory]
    [InlineData("fov.use", false, "fov.use")]
    [InlineData("-fov.use", true, "fov.use")]
    [InlineData("-", true, "")]
    [InlineData("-*", true, "*")]
    [InlineData("", false, "")]
    public void TrySplitNegation_reports_the_sign_and_the_bare_pattern(string pattern, bool negated, string bare)
    {
        PermissionNodes.TrySplitNegation(pattern, out bool actualNegated, out string actualBare);

        Assert.Equal(negated, actualNegated);
        Assert.Equal(bare, actualBare);
    }

    [Theory]
    [InlineData("@vip", "vip")]
    [InlineData(" @vip ", "vip")]
    [InlineData("@", null)]
    [InlineData("@ ", null)]
    [InlineData("vip", null)]
    [InlineData("-@vip", null)]
    [InlineData("", null)]
    public void GroupReferenceOf_only_accepts_a_usable_at_prefix(string entry, string? expected)
    {
        Assert.Equal(expected, PermissionNodes.GroupReferenceOf(entry));
    }
}