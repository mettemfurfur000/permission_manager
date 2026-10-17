using permission_manager.src.utils;
using Xunit;

namespace permission_manager.tests;

/// <summary>
/// Message templating for denials and admin replies. Verifies the tags are replaced with
/// the control characters <c>PrintToChat</c> actually sends.
/// </summary>
public class ChatFormatTests
{
    [Fact]
    public void Substitutes_command_and_node()
    {
        string message = ChatFormat.Render("no {command} for you, needs {node}", "css_fov", "fov.use");

        Assert.Equal("no css_fov for you, needs fov.use", message);
    }

    [Fact]
    public void Placeholders_are_case_insensitive()
    {
        Assert.Equal("css_fov/fov.use", ChatFormat.Render("{Command}/{NODE}", "css_fov", "fov.use"));
    }

    [Fact]
    public void Missing_placeholders_become_empty()
    {
        Assert.Equal("needs ", ChatFormat.Render("needs {node}", "css_fov"));
    }

    [Fact]
    public void Player_placeholder_falls_back_to_console()
    {
        Assert.Equal("hi console", ChatFormat.Render("hi {player}"));
    }

    [Theory]
    [InlineData("{red}", '\a')]
    [InlineData("{green}", '\u0004')]
    [InlineData("{lightblue}", '\u000b')]
    [InlineData("{lightred}", '\a')]
    [InlineData("{yellow}", '\t')]
    [InlineData("{default}", '\u0001')]
    [InlineData("{grey}", '\b')]
    public void Colour_tags_become_control_characters(string tag, char expected)
    {
        Assert.Equal(expected, ChatFormat.Paint($"a{tag}b").ToCharArray()[1]);
    }

    [Fact]
    public void Colour_tags_are_case_insensitive()
    {
        Assert.Equal(ChatFormat.Paint("{red}"), ChatFormat.Paint("{RED}"));
    }

    [Fact]
    public void Unknown_tags_are_left_alone()
    {
        Assert.Equal("keep {notacolour} me", ChatFormat.Paint("keep {notacolour} me"));
    }

    [Fact]
    public void Unterminated_tag_is_left_alone()
    {
        Assert.Equal("dangling {red", ChatFormat.Paint("dangling {red"));
    }

    [Fact]
    public void Text_without_tags_is_returned_unchanged()
    {
        Assert.Equal("plain text", ChatFormat.Paint("plain text"));
        Assert.Equal(string.Empty, ChatFormat.Paint(string.Empty));
    }

    [Fact]
    public void Null_template_renders_empty()
    {
        Assert.Equal(string.Empty, ChatFormat.Render(null));
        Assert.Equal(string.Empty, ChatFormat.Render(string.Empty));
    }

    [Fact]
    public void Full_template_round_trip()
    {
        string rendered = ChatFormat.Render("{lightred}You do not have permission to use {command} (needs {node}).", "css_fov", "fov.use");

        Assert.Equal(
            "\u0007You do not have permission to use css_fov (needs fov.use).",
            rendered);
    }
}

/// <summary>
/// Target argument parsing. The offline-grant path (a bare SteamID64) is pure and is the
/// part most likely to regress.
/// </summary>
public class TargetResolverTests
{
    [Theory]
    [InlineData("76561198000000000")]
    [InlineData("@76561198000000000")]
    [InlineData("  76561198000000000  ")]
    public void Bare_steamids_are_recognised(string raw)
    {
        Assert.Equal([76561198000000000UL], TargetResolver.ParseSteamIds(raw));
    }

    [Fact]
    public void Lists_of_steamids_are_recognised()
    {
        Assert.Equal(
            [1UL, 2UL, 3UL],
            TargetResolver.ParseSteamIds("1, 2;3"));
    }

    [Fact]
    public void Duplicates_are_collapsed()
    {
        Assert.Equal([42UL], TargetResolver.ParseSteamIds("42, 42"));
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("@all")]
    [InlineData("#ct")]
    [InlineData("76561198000000000abc")]
    [InlineData("")]
    [InlineData("   ")]
    public void Anything_that_is_not_a_steamid_list_is_left_to_the_target_system(string raw)
    {
        // An empty result makes Resolve() fall through to CounterStrikeSharp's own syntax.
        Assert.Empty(TargetResolver.ParseSteamIds(raw));
    }

    [Fact]
    public void Null_input_is_safe()
    {
        Assert.Empty(TargetResolver.ParseSteamIds(null!));
    }
}