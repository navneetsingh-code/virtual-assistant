using Vera.Backend.Kb;
using Vera.Backend.Models;

namespace Vera.Backend.Matcher;

/// <summary>A successful direct-FAQ match. Carries the approved answer plus the
/// traceability fields the Backend_API response needs.</summary>
public sealed record DirectFaqMatch
{
    public required string DirectAnswer { get; init; }
    public required string SourceFaqId { get; init; }
    public required string KbVersion { get; init; }
    public required double Score { get; init; }
}

/// <summary>
/// In-memory keyword/synonym Matcher + direct FAQ matching (ported from
/// backend-ts-reference/src/matcher/matcher.ts).
///
/// Compares a normalized user message against the question variants of ELIGIBLE,
/// DIRECTLY ANSWERABLE FAQs and, on a confident match, returns that FAQ's
/// approved directAnswer with its id and kbVersion. No Bedrock. Every served
/// answer is confirmed through the <see cref="ReferenceValidator"/> (reference
/// integrity / defense-in-depth), so nothing ineligible/dangling can surface.
///
/// Requirements: 4.1, 4.2, 4.3, 4.5 (served from approved FAQ content).
/// </summary>
public sealed class Matcher
{
    /// <summary>Default minimum overlap score for a non-exact match. The single open
    /// "threshold tuning" knob from the design.</summary>
    public const double DefaultMatchThreshold = 0.5;

    private sealed record FaqIndexEntry(
        Faq Faq,
        IReadOnlyList<HashSet<string>> QuestionTokenSets,
        IReadOnlyList<string> NormalizedQuestions);

    private readonly KnowledgeBaseProvider _provider;
    private readonly ReferenceValidator _validator;
    private readonly double _minScore;
    private readonly List<FaqIndexEntry> _index;

    public string KbVersion => _provider.KbVersion;

    public Matcher(KnowledgeBaseProvider provider, ReferenceValidator validator, double? minScore = null)
    {
        _provider = provider;
        _validator = validator;
        _minScore = minScore ?? DefaultMatchThreshold;

        _index = [];
        foreach (var faq in provider.GetDirectlyAnswerableFaqs())
        {
            var normalizedQuestions = new List<string>();
            var questionTokenSets = new List<HashSet<string>>();
            foreach (var question in faq.Questions)
            {
                var normalized = TextUtils.NormalizeText(question);
                if (normalized.Length == 0) continue;
                normalizedQuestions.Add(normalized);
                questionTokenSets.Add([.. normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
            }
            if (normalizedQuestions.Count == 0) continue; // never matchable
            _index.Add(new FaqIndexEntry(faq, questionTokenSets, normalizedQuestions));
        }
    }

    /// <summary>Match a user message against eligible, directly-answerable FAQs.
    /// Returns the best confident match, or null when nothing clears the threshold.</summary>
    public DirectFaqMatch? MatchDirectFaq(string message)
    {
        var normalizedMessage = TextUtils.NormalizeText(message);
        if (normalizedMessage.Length == 0) return null;
        var messageTokens = new HashSet<string>(normalizedMessage.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        (FaqIndexEntry Entry, double Score, bool Exact)? best = null;

        foreach (var entry in _index)
        {
            var isExact = entry.NormalizedQuestions.Contains(normalizedMessage);
            var bestVariantScore = 0.0;
            foreach (var tokenSet in entry.QuestionTokenSets)
            {
                var variantScore = TextUtils.TokenOverlapScore(messageTokens, tokenSet);
                if (variantScore > bestVariantScore) bestVariantScore = variantScore;
            }
            var score = isExact ? 1.0 : bestVariantScore;

            if (best is null)
            {
                best = (entry, score, isExact);
                continue;
            }

            // Prefer higher score; tie-break deterministically by FAQ id (ascending).
            if (score > best.Value.Score ||
                (score == best.Value.Score && string.CompareOrdinal(entry.Faq.Id, best.Value.Entry.Faq.Id) < 0))
            {
                best = (entry, score, isExact);
            }
        }

        if (best is null) return null;
        if (!best.Value.Exact && best.Value.Score < _minScore) return null;

        // Confirm the matched FAQ resolves as a valid direct-FAQ answer path.
        var validated = _validator.ResolveDirectFaq(best.Value.Entry.Faq.Id);
        if (validated is null) return null;

        return new DirectFaqMatch
        {
            DirectAnswer = validated.Faq.DirectAnswer!,
            SourceFaqId = validated.Faq.Id,
            KbVersion = _provider.KbVersion,
            Score = best.Value.Score,
        };
    }
}
