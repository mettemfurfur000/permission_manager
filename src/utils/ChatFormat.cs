using System;
using System.Collections.Generic;
using System.Text;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace permission_manager.src.utils;

/// <summary>
/// Renders configured message templates.
///
/// <para>Templates stay human readable in JSON by using named tags instead of the raw
/// control characters <c>PrintToChat</c> expects:</para>
/// <code>{lightred}You do not have permission to use {command} (needs {node}).</code>
///
/// <para>Supported tags: <c>{command}</c>, <c>{node}</c>, <c>{group}</c>, <c>{player}</c>
/// and the colour tags <c>{default} {white} {darkred} {lightpurple} {green} {olive} {lime}
/// {red} {lightblue} {purple} {yellow} {silver} {gray} {grey} {gold}</c>, matched case
/// insensitively. Unknown tags are left untouched so nothing is silently swallowed.</para>
/// </summary>
public static class ChatFormat
{
    private static readonly Dictionary<string, char> Colors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{default}"] = ChatColors.Default,
        ["{white}"] = ChatColors.White,
        ["{darkred}"] = ChatColors.DarkRed,
        ["{lightpurple}"] = ChatColors.LightPurple,
        ["{green}"] = ChatColors.Green,
        ["{olive}"] = ChatColors.Olive,
        ["{lime}"] = ChatColors.Lime,
        ["{red}"] = ChatColors.Red,
        // CounterStrikeSharp's palette has no separate light red, so this is an alias.
        ["{lightred}"] = ChatColors.Red,
        ["{lightblue}"] = ChatColors.LightBlue,
        ["{purple}"] = ChatColors.Purple,
        ["{yellow}"] = ChatColors.Yellow,
        ["{lightyellow}"] = ChatColors.LightYellow,
        ["{silver}"] = ChatColors.Silver,
        ["{gray}"] = ChatColors.Grey,
        ["{grey}"] = ChatColors.Grey,
        ["{gold}"] = ChatColors.Gold,
    };

    /// <summary>
    /// Substitutes placeholders, then colour tags. Returns the template unchanged when it is
    /// null or blank.
    /// </summary>
    public static string Render(string? template, string command = "", string node = "", string group = "", CCSPlayerController? player = null)
    {
        if (string.IsNullOrEmpty(template))
            return string.Empty;

        string message = template;

        if (message.Contains('{'))
        {
            message = message
                .Replace("{command}", command, StringComparison.OrdinalIgnoreCase)
                .Replace("{node}", node, StringComparison.OrdinalIgnoreCase)
                .Replace("{group}", group, StringComparison.OrdinalIgnoreCase)
                .Replace("{player}", player?.PlayerName ?? "console", StringComparison.OrdinalIgnoreCase);
        }

        return Paint(message);
    }

    /// <summary>Replaces colour tags with the control characters the game client expects.</summary>
    public static string Paint(string message)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains('{'))
            return message;

        StringBuilder builder = new(message.Length);

        for (int i = 0; i < message.Length; i++)
        {
            if (message[i] != '{')
            {
                builder.Append(message[i]);
                continue;
            }

            int close = message.IndexOf('}', i);

            if (close < 0)
            {
                builder.Append(message[i..]);
                break;
            }

            string tag = message[i..(close + 1)];

            if (Colors.TryGetValue(tag, out char color))
            {
                builder.Append(color);
                i = close;
                continue;
            }

            builder.Append(tag);
            i = close;
        }

        return builder.ToString();
    }
}