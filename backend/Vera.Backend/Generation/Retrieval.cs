using Vera.Backend.Kb;
using Vera.Backend.Matcher;
using Vera.Backend.Models;

namespace Vera.Backend.Generation;

/// <summary>A single retrieved, approved statement with its relevance score.</summary>
public sealed record RetrievedStatement(Statement Statement, double Score);

/// <summary>The result of a retrieval pass. When <see cref="Statements"/> is empty
/// the caller returns the Fallback_Response WITHOUT invoking generation (Property 5).</summary>
public sealed record RetrievalResult
{
    public required IReadOnlyList<RetrievedStatement> Statements { get; init; }
    /// <summary>The eligible FAQ most relevant to the query (traceability only), or null.</summary>
    public string? BestFaqId { get; init; }
}

/// <summary>
/// Retrieval_Component — the relevance gate for the free-form path (ported from
/// backend-ts-reference/src/generation/retrieval.ts, including the query-coverage
/// scoring + light stemming relevance fix).
///
/// Searches ONLY the eligible, reference-validated grounding corpus plus eligible
/// FAQ text — never the raw KB. Uses a query-coverage score (fraction of the
/// query's meaningful, stopword-filtered tokens present in the content), so a
/// short on-topic question is not penalized by long content. If nothing clears
/// the threshold it returns an EMPTY result and the caller MUST NOT invoke
/// generation (Property 5).
///
/// Requirements: 6.1.
/// </summary>
public class Retrieval
{
    /// <summary>Default relevance threshold on the query-coverage score: at least
    /// this fraction of the query's meaningful tokens must appear in a candidate.</summary>
    public const double DefaultRetrievalThreshold = 0.5;

    /// <summary>Default cap on grounding statements placed into a prompt.</summary>
    public const int DefaultMaxStatements = 6;

    // Stemmed stopwords (query tokens are stemmed before this set is consulted).
    private static readonly HashSet<string> Stopwords =
        new[]
        {
            "a", "an", "and", "are", "as", "at", "be", "by", "can", "do", "does", "for",
            "from", "how", "i", "in", "is", "it", "its", "me", "my", "of", "on", "or",
            "that", "the", "this", "to", "was", "what", "when", "where", "which", "who",
            "why", "will", "with", "you", "your",
        }.Select(Stem).ToHashSet();

    private sealed record StatementIndexEntry(Statement Statement, HashSet<string> Tokens);
    private sealed record FaqIndexEntry(Faq Faq, HashSet<string> Tokens);

    private readonly double _minScore;
    private readonly int _maxStatements;
    private readonly List<StatementIndexEntry> _statementIndex;
    private readonly List<FaqIndexEntry> _faqIndex;

    public Retrieval(
        ReferenceValidator validator,
        KnowledgeBaseProvider provider,
        double? minScore = null,
        int? maxStatements = null)
    {
        _minScore = minScore ?? DefaultRetrievalThreshold;
        _maxStatements = maxStatements ?? DefaultMaxStatements;

        _statementIndex = validator.GetGroundingStatements()
            .Select(s => new StatementIndexEntry(s, ToTokenSet($"{s.Topic} {s.Text}")))
            .Where(e => e.Tokens.Count > 0)
            .ToList();

        _faqIndex = provider.GetEligibleFaqs()
            .Select(f => new FaqIndexEntry(
                f, ToTokenSet($"{f.Topic} {string.Join(' ', f.Questions)} {f.DirectAnswer ?? ""}")))
            .Where(e => e.Tokens.Count > 0)
            .ToList();
    }

    public virtual RetrievalResult Retrieve(string query)
    {
        var queryTokens = ToTokenSet(query);
        if (queryTokens.Count == 0) return new RetrievalResult { Statements = [] };

        var scored = new List<RetrievedStatement>();
        foreach (var entry in _statementIndex)
        {
            var score = QueryCoverageScore(queryTokens, entry.Tokens);
            if (score >= _minScore) scored.Add(new RetrievedStatement(entry.Statement, score));
        }

        // Deterministic order: score desc, then id asc for stable tie-breaking.
        scored.Sort((a, b) =>
            b.Score != a.Score
                ? b.Score.CompareTo(a.Score)
                : string.CompareOrdinal(a.Statement.Id, b.Statement.Id));

        var statements = scored.Take(_maxStatements).ToList();

        // Best eligible FAQ for traceability only (not required to ground).
        (string Id, double Score)? bestFaq = null;
        foreach (var entry in _faqIndex)
        {
            var score = QueryCoverageScore(queryTokens, entry.Tokens);
            if (score < _minScore) continue;
            if (bestFaq is null || score > bestFaq.Value.Score ||
                (score == bestFaq.Value.Score && string.CompareOrdinal(entry.Faq.Id, bestFaq.Value.Id) < 0))
            {
                bestFaq = (entry.Faq.Id, score);
            }
        }

        return new RetrievalResult { Statements = statements, BestFaqId = bestFaq?.Id };
    }

    /// <summary>Query-coverage relevance score in [0, 1]: of the query's meaningful
    /// (non-stopword) tokens, the fraction present in the content token set.</summary>
    public static double QueryCoverageScore(IReadOnlySet<string> queryTokens, IReadOnlySet<string> contentTokens)
    {
        if (contentTokens.Count == 0) return 0;
        var meaningful = queryTokens.Where(t => !Stopwords.Contains(t)).ToList();
        // If the whole query was stopwords, fall back to all query tokens.
        var terms = meaningful.Count > 0 ? meaningful : queryTokens.ToList();
        if (terms.Count == 0) return 0;
        var matched = terms.Count(contentTokens.Contains);
        return (double)matched / terms.Count;
    }

    /// <summary>Light, conservative stem for plural/verb-form folding (mirrors the
    /// TypeScript reference fix: "images" -> "image", "formats" -> "format",
    /// "supported" -> "support"). Relevance-normalization only; never affects
    /// eligibility or what content may be grounded.</summary>
    public static string Stem(string token)
    {
        var t = token;
        if (t.Length > 4 && t.EndsWith("ies", StringComparison.Ordinal))
            t = t[..^3] + "y";
        else if (t.Length > 4 && t.EndsWith("ing", StringComparison.Ordinal))
            t = t[..^3];
        else if (t.Length > 4 && t.EndsWith("ed", StringComparison.Ordinal))
            t = t[..^2];
        else if (t.Length > 4 && t.EndsWith("es", StringComparison.Ordinal) && IsSibilantEsPlural(t))
            t = t[..^2];
        else if (t.Length > 3 && t.EndsWith("s", StringComparison.Ordinal) && !t.EndsWith("ss", StringComparison.Ordinal))
            t = t[..^1];
        return t;
    }

    // True "-es" plurals on sibilant stems: s/x/z/ch/sh + "es". So "images" does
    // NOT lose "es" here (handled by the plain -s rule -> "image").
    private static bool IsSibilantEsPlural(string t)
    {
        // t ends with "es"; inspect the character(s) before it.
        var stem = t[..^2];
        return stem.EndsWith("s", StringComparison.Ordinal)
            || stem.EndsWith("x", StringComparison.Ordinal)
            || stem.EndsWith("z", StringComparison.Ordinal)
            || stem.EndsWith("ch", StringComparison.Ordinal)
            || stem.EndsWith("sh", StringComparison.Ordinal);
    }

    private static HashSet<string> ToTokenSet(string text)
    {
        var normalized = TextUtils.NormalizeText(text);
        if (normalized.Length == 0) return [];
        return normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Stem).ToHashSet();
    }
}
