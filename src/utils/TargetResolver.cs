using System.Collections.Generic;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;

namespace permission_manager.src.utils;

/// <summary>
/// Turns a command's target argument into SteamID64s.
///
/// <para>CounterStrikeSharp's own <c>@all</c> / <c>#ct</c> / <c>@7656119...</c> syntax is
/// used for online players, but grants also have to work for people who are not on the
/// server yet, so a bare SteamID64 (or a comma separated list of them) is handled here and
/// never filtered by connectivity.</para>
/// </summary>
public static class TargetResolver
{
    /// <summary>SteamID64 used for the console / server, which has no real identity.</summary>
    public const ulong ConsoleSteamId = 0UL;

    /// <summary>
    /// Collects every SteamID64 the target argument refers to.
    /// </summary>
    /// <param name="commandInfo">Command being executed.</param>
    /// <param name="index">Argument index holding the target.</param>
    /// <param name="online">Receives the online controllers the target matched, in command order.</param>
    public static List<ulong> Resolve(CommandInfo commandInfo, int index, out List<CCSPlayerController> online)
    {
        List<ulong> ids = new();
        online = new();

        string raw = commandInfo.GetArg(index);
        List<ulong> explicitIds = ParseSteamIds(raw);

        if (explicitIds.Count > 0)
        {
            ids.AddRange(explicitIds);

            // Record who is online anyway, so the reply can name them.
            foreach (CCSPlayerController player in Utilities.GetPlayers())
            {
                ulong? steamId = SteamIdOf(player);

                if (steamId is not null && explicitIds.Contains(steamId.Value))
                    online.Add(player);
            }

            return ids;
        }

        foreach (CCSPlayerController player in commandInfo.GetArgTargetResult(index))
        {
            if (player is null || !player.IsValid)
                continue;

            online.Add(player);

            ulong? steamId = SteamIdOf(player);

            if (steamId is not null && !ids.Contains(steamId.Value))
                ids.Add(steamId.Value);
        }

        return ids;
    }

    /// <summary>
    /// Parses a SteamID64, an <c>@</c> prefixed SteamID64, or a comma separated list of
    /// either. Returns an empty list for anything that looks like a name, so the caller can
    /// fall back to CounterStrikeSharp's target system.
    /// </summary>
    public static List<ulong> ParseSteamIds(string raw)
    {
        List<ulong> ids = new();

        if (string.IsNullOrWhiteSpace(raw))
            return ids;

        string cleaned = raw.Trim().TrimStart('@').Trim();

        if (!LooksLikeSteamIdList(cleaned))
            return ids;

        foreach (string part in cleaned.Split(',', ';', ' '))
        {
            string candidate = part.Trim();

            if (candidate.Length > 0 && ulong.TryParse(candidate, out ulong id) && !ids.Contains(id))
                ids.Add(id);
        }

        return ids;
    }

    private static bool LooksLikeSteamIdList(string value)
    {
        if (value.Length == 0)
            return false;

        foreach (char c in value)
        {
            if (!char.IsDigit(c) && c != ',' && c != ';' && c != ' ')
                return false;
        }

        return true;
    }

    /// <summary>
    /// SteamID64 of a controller, or <c>null</c> for bots, the HLTV and anybody whose Steam
    /// identity has not been authorised yet.
    /// </summary>
    public static ulong? SteamIdOf(CCSPlayerController? player)
    {
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV)
            return null;

        SteamID? authorized = player.AuthorizedSteamID;

        if (authorized is null || !authorized.IsValid())
            return null;

        return authorized.SteamId64;
    }

    /// <summary>Display name, or a stable placeholder for console, bots and unauthorised players.</summary>
    public static string NameOf(CCSPlayerController? player)
    {
        if (player is null)
            return "console";

        if (player.PlayerName is { Length: > 0 } name)
            return name;

        return player.IsBot ? "<bot>" : "<unauthorised>";
    }
}