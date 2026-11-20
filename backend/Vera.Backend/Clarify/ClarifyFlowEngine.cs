using System.Text.RegularExpressions;
using Vera.Backend.Kb;
using Vera.Backend.Models;

namespace Vera.Backend.Clarify;

/// <summary>The outcome of a single navigation step within a clarify flow.</summary>
public abstract record ClarifyNavigationResult
{
    public sealed record Next(string FlowId) : ClarifyNavigationResult;
    public sealed record FaqResult(string FaqId) : ClarifyNavigationResult;
    public sealed record Answer(string AnswerText, IReadOnlyList<Statement> Statements) : ClarifyNavigationResult;
    public sealed record Issue(string IssueKey) : ClarifyNavigationResult;
    public sealed record FreeText : ClarifyNavigationResult;
    public sealed record Fallback(string Reply, ClarifyFallbackReason Reason) : ClarifyNavigationResult;
}

/// <summary>Why a navigation step resolved to fallback, for operational logging.</summary>
public enum ClarifyFallbackReason { UnknownFlow, FlowPrunedEmpty, NoOptionMatch }

/// <summary>
/// ClarifyFlowEngine — static, deterministic navigation of `clarifyFlows`
/// (ported from backend-ts-reference/src/clarify/clarify-flow-engine.ts).
///
/// Given a flow id and user selection/message: normalizes input, matches against
/// each surviving option's authored match[] keywords (plus id/label), and
/// resolves the matched option through the <see cref="ReferenceValidator"/> into
/// exactly one target form. All id validation / reference pruning is delegated
/// to the validator, so a dangling/draft/pending/unapproved reference is never
/// navigated to and never served. Fully deterministic: NO Bedrock.
///
/// Requirements: 4.2, 4.3, 4.5.
/// </summary>
public sealed class ClarifyFlowEngine
{
    private static readonly Regex NonLetterNumber = new(@"[^\p{L}\p{N}\s]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ReferenceValidator _validator;
    private readonly IReadOnlyDictionary<string, ClarifyFlow> _flows;

    public ClarifyFlowEngine(ReferenceValidator validator, KnowledgeBase? rawKb = null)
    {
        _validator = validator;
        _flows = rawKb?.ClarifyFlows ?? new Dictionary<string, ClarifyFlow>();
    }

    /// <summary>Normalize text for deterministic keyword matching.</summary>
    public static string NormalizeClarifyInput(string text)
    {
        var lowered = text.ToLowerInvariant();
        var stripped = NonLetterNumber.Replace(lowered, " ");
        return Whitespace.Replace(stripped, " ").Trim();
    }

    public bool HasFlow(string flowId) => _flows.ContainsKey(flowId);

    public string? GetPrompt(string flowId)
        => _flows.TryGetValue(flowId, out var flow) ? flow.Prompt : null;

    public IReadOnlyList<ValidatedClarifyOption> ListOptions(string flowId)
        => _validator.ResolveClarifyFlow(flowId);

    public ClarifyNavigationResult Navigate(string flowId, string input)
    {
        if (!HasFlow(flowId)) return Fallback(ClarifyFallbackReason.UnknownFlow);

        var options = ListOptions(flowId);
        if (options.Count == 0) return Fallback(ClarifyFallbackReason.FlowPrunedEmpty);

        var normalizedInput = NormalizeClarifyInput(input);

        // 1) Prefer an exact option-id match (the widget may send the id).
        var byId = options.FirstOrDefault(o => NormalizeClarifyInput(o.OptionId) == normalizedInput);
        if (byId is not null) return ToNavigationResult(byId);

        // 2) Match free-form input against each option's keywords, in authored
        //    order (deterministic: the first surviving option wins).
        foreach (var option in options)
        {
            if (InputMatchesKeywords(normalizedInput, KeywordsFor(flowId, option)))
            {
                return ToNavigationResult(option);
            }
        }

        return Fallback(ClarifyFallbackReason.NoOptionMatch);
    }

    public ClarifyNavigationResult SelectOption(string flowId, string optionId)
    {
        if (!HasFlow(flowId)) return Fallback(ClarifyFallbackReason.UnknownFlow);
        var options = ListOptions(flowId);
        if (options.Count == 0) return Fallback(ClarifyFallbackReason.FlowPrunedEmpty);
        var option = options.FirstOrDefault(o => o.OptionId == optionId);
        return option is null ? Fallback(ClarifyFallbackReason.NoOptionMatch) : ToNavigationResult(option);
    }

    private ClarifyNavigationResult.Fallback Fallback(ClarifyFallbackReason reason)
        => new(_validator.FallbackMessage, reason);

    /// <summary>Authored match[] keywords for a surviving option, pulled from the raw
    /// flow structure. Option id and label are also accepted as selectors.</summary>
    private IReadOnlyList<string> KeywordsFor(string flowId, ValidatedClarifyOption option)
    {
        var keywords = new List<string> { option.OptionId, option.Label };
        if (_flows.TryGetValue(flowId, out var rawFlow))
        {
            var rawOption = rawFlow.Options.FirstOrDefault(o => o.Id == option.OptionId);
            if (rawOption is not null) keywords.AddRange(rawOption.Match);
        }
        return keywords;
    }

    private static bool InputMatchesKeywords(string normalizedInput, IReadOnlyList<string> keywords)
    {
        if (normalizedInput.Length == 0) return false;
        foreach (var keyword in keywords)
        {
            var normalizedKeyword = NormalizeClarifyInput(keyword);
            if (normalizedKeyword.Length == 0) continue;
            if (normalizedInput == normalizedKeyword
                || normalizedInput.Contains(normalizedKeyword, StringComparison.Ordinal)
                || normalizedKeyword.Contains(normalizedInput, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static ClarifyNavigationResult ToNavigationResult(ValidatedClarifyOption option)
        => option.Target switch
        {
            ClarifyTarget.Next n => new ClarifyNavigationResult.Next(n.FlowId),
            ClarifyTarget.FaqTarget f => new ClarifyNavigationResult.FaqResult(f.FaqId),
            ClarifyTarget.Answer a => new ClarifyNavigationResult.Answer(a.AnswerText, a.Statements),
            ClarifyTarget.Issue i => new ClarifyNavigationResult.Issue(i.IssueKey),
            ClarifyTarget.FreeText => new ClarifyNavigationResult.FreeText(),
            _ => throw new InvalidOperationException("Unhandled clarify target."),
        };
}
