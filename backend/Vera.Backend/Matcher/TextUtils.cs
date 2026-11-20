using System.Text.RegularExpressions;

namespace Vera.Backend.Matcher;

/// <summary>
/// Shared text normalization + token-overlap utilities used by the direct FAQ
/// matcher and the free-form retrieval gate (ported from the normalize/tokenize/
/// tokenOverlapScore helpers in backend-ts-reference/src/matcher/matcher.ts).
/// </summary>
public static class TextUtils
{
    private static readonly Regex NonAlphaNum = new("[^a-z0-9]+", RegexOptions.Compiled);

    /// <summary>Normalize text for matching: lowercase, replace any non-alphanumeric
    /// run with a single space, and trim. Keeps digits.</summary>
    public static string NormalizeText(string input)
        => NonAlphaNum.Replace(input.ToLowerInvariant(), " ").Trim();

    /// <summary>Tokenize a message into a set of normalized tokens for overlap scoring.</summary>
    public static HashSet<string> Tokenize(string input)
    {
        var normalized = NormalizeText(input);
        return normalized.Length == 0
            ? []
            : [.. normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>Symmetric token-overlap score in [0, 1] (Jaccard: shared / union).
    /// Returns 0 when either side is empty.</summary>
    public static double TokenOverlapScore(IReadOnlySet<string> a, IReadOnlySet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var shared = a.Count(b.Contains);
        var union = a.Count + b.Count - shared;
        return union == 0 ? 0 : (double)shared / union;
    }
}
