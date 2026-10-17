using System;
using System.Collections.Generic;

namespace PermissionManagerApi;

/// <summary>
/// Wildcard aware node matching. Patterns follow shell globbing conventions:
///
/// <list type="bullet">
/// <item><c>*</c> matches any run of characters, including none.</item>
/// <item><c>?</c> matches exactly one character.</item>
/// <item>Everything else is literal. Matching is case insensitive.</item>
/// </list>
///
/// So <c>fov.*</c> matches <c>fov.use</c>, <c>fov.*</c> does not match <c>fovuse</c>,
/// and <c>*</c> matches everything.
/// </summary>
public static class NodeMatcher
{
    /// <summary>Tests a pattern against a node.</summary>
    public static bool Matches(string? pattern, string? node)
    {
        if (pattern is null || node is null)
            return false;

        // Iterative glob matcher. Keeps a single backtrack point for the last '*'
        // instead of allocating a regex, which matters because this runs per command.
        int p = 0;
        int n = 0;
        int starPattern = -1;
        int starNode = 0;

        while (n < node.Length)
        {
            if (p < pattern.Length)
            {
                char pc = pattern[p];
                char nc = node[n];

                if (pc == '*')
                {
                    starPattern = p++;
                    starNode = n;
                    continue;
                }

                if (pc == '?' || char.ToLowerInvariant(pc) == char.ToLowerInvariant(nc))
                {
                    p++;
                    n++;
                    continue;
                }
            }

            if (starPattern < 0)
                return false;

            // Backtrack: let the last '*' swallow one more character.
            p = starPattern + 1;
            n = ++starNode;
        }

        // Trailing '*' characters can all match the empty string.
        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>True when at least one pattern in the sequence matches the node.</summary>
    public static bool MatchesAny(IEnumerable<string>? patterns, string node)
    {
        if (patterns is null)
            return false;

        foreach (string pattern in patterns)
        {
            if (Matches(pattern, node))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Canonical form used when storing nodes: trimmed and lower cased. Empty input
    /// becomes <see cref="string.Empty"/>.
    /// </summary>
    public static string Normalize(string? node)
    {
        if (string.IsNullOrWhiteSpace(node))
            return string.Empty;

        return node.Trim().ToLowerInvariant();
    }

    /// <summary>Normalizes a stored pattern, preserving a leading negation sign.</summary>
    public static string NormalizePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return string.Empty;

        string trimmed = pattern.Trim();

        if (trimmed.Length > 0 && trimmed[0] == PermissionNodes.NegationPrefix)
        {
            string bare = trimmed[1..].Trim();

            return bare.Length == 0
                ? PermissionNodes.NegationPrefix + PermissionNodes.All
                : PermissionNodes.NegationPrefix + bare.ToLowerInvariant();
        }

        return trimmed.ToLowerInvariant();
    }
}