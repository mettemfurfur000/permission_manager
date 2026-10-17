namespace PermissionManagerApi;

/// <summary>
/// Well known nodes and prefixes understood by the permission manager.
/// Third party plugins are expected to use their own dot notation namespace,
/// e.g. <c>fov.use</c>, <c>shop.buy</c>.
/// </summary>
public static class PermissionNodes
{
    /// <summary>Node required to run the <c>css_perm_*</c> administration commands.</summary>
    public const string Admin = "perm.admin";

    /// <summary>Node that grants literally everything.</summary>
    public const string All = "*";

    /// <summary>CounterStrikeSharp's built in root admin flag, honoured when <c>UseCounterStrikeSharpAdmins</c> is on.</summary>
    public const string CssRoot = "@css/root";

    /// <summary>Prefix that turns a node pattern into an explicit deny. <c>-fov.use</c> beats an earlier allow.</summary>
    public const char NegationPrefix = '-';

    /// <summary>Prefix used inside a group definition to inherit another group's nodes. <c>"@moderator"</c>.</summary>
    public const char GroupParentPrefix = '@';

    /// <summary>
    /// Splits a raw stored pattern into its negation flag and the bare node pattern.
    /// A bare <c>*</c> or <c>-*</c> is returned as-is for <paramref name="bare"/>.
    /// </summary>
    public static bool TrySplitNegation(string pattern, out bool negated, out string bare)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            negated = false;
            bare = string.Empty;
            return false;
        }

        negated = pattern[0] == NegationPrefix;
        bare = negated ? pattern[1..] : pattern;
        return true;
    }

    /// <summary>True when the pattern inherits another group rather than granting a node.</summary>
    public static bool IsGroupReference(string entry)
    {
        return !string.IsNullOrWhiteSpace(entry)
            && entry.TrimStart().Length > 0
            && entry.TrimStart()[0] == GroupParentPrefix
            && entry.TrimStart()[1..].Trim().Length > 0;
    }

    /// <summary>Returns the referenced group name, or <c>null</c> when the entry is not a group reference.</summary>
    public static string? GroupReferenceOf(string entry)
    {
        if (!IsGroupReference(entry))
            return null;

        return entry.TrimStart()[1..].Trim();
    }
}