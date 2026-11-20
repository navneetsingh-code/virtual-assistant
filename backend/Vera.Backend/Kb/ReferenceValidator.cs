using Vera.Backend.Models;

namespace Vera.Backend.Kb;

/// <summary>
/// The kind of answer-producing path a resolved reference belongs to
/// (design.md -> Reference Integrity paths a–f).
/// </summary>
public enum AnswerPathKind
{
    /// <summary>(a) A FAQ served via its directAnswer.</summary>
    DirectFaq,
    /// <summary>(b) A clarify-flow inline answer option (+ statementIds).</summary>
    ClarifyAnswer,
    /// <summary>(c) A clarify-flow option that resolves to a FAQ id.</summary>
    ClarifyFaq,
    /// <summary>(d) A clarify-flow option that resolves to a photoIssues key.</summary>
    ClarifyIssue,
    /// <summary>(e) A photoIssues answer (+ statementIds).</summary>
    PhotoIssue,
}

/// <summary>The kind of id that failed to resolve to an eligible target.</summary>
public enum RefKind { Faq, Statement, PhotoIssue }

/// <summary>Why a pruned reference failed: id absent entirely vs. present but filtered out.</summary>
public enum PruneReason { Dangling, Ineligible }

/// <summary>A reference that FAILED validation, retained for logging / fallback-reason
/// reporting. The reference was pruned (never served).</summary>
public sealed record PrunedReference(
    AnswerPathKind Path,
    string Source,
    RefKind RefKind,
    string RefId,
    PruneReason Reason);

/// <summary>A direct-FAQ path that survived reference validation.</summary>
public sealed record ValidatedDirectFaq(Faq Faq, IReadOnlyList<Statement> Statements);

/// <summary>The resolved target form of a validated clarify option (exactly one case).</summary>
public abstract record ClarifyTarget
{
    public sealed record Next(string FlowId) : ClarifyTarget;
    public sealed record FaqTarget(string FaqId) : ClarifyTarget;
    public sealed record Answer(string AnswerText, IReadOnlyList<Statement> Statements) : ClarifyTarget;
    public sealed record Issue(string IssueKey) : ClarifyTarget;
    public sealed record FreeText : ClarifyTarget;
}

/// <summary>A clarify-flow option that survived reference validation.</summary>
public sealed record ValidatedClarifyOption(
    string FlowId,
    string OptionId,
    string Label,
    ClarifyTarget Target);

/// <summary>A photoIssues entry that survived reference validation.</summary>
public sealed record ValidatedPhotoIssue(
    string Key,
    PhotoIssue Issue,
    IReadOnlyList<Statement> Statements);

/// <summary>
/// ReferenceValidator + reference-graph pruning (ported from
/// backend-ts-reference/src/kb/reference-validator.ts).
///
/// Layers reference integrity on top of the provider's eligibility: for every
/// answer-producing path it resolves and validates every referenced id (FAQ id,
/// photoIssues key, every statementId) against the eligible set, and PRUNES
/// references that are dangling / draft / pending / unapproved (design.md ->
/// Data Models -> Reference Integrity, Property 6).
///
/// Requirements: 4.1, 5.2, 6.1, 6.2, 9.5.
/// </summary>
public sealed class ReferenceValidator
{
    private readonly KnowledgeBaseProvider _provider;
    private readonly KnowledgeBase? _rawKb;
    private readonly List<PrunedReference> _pruned = [];

    private readonly HashSet<string> _rawStatementIds;
    private readonly HashSet<string> _rawFaqIds;
    private readonly HashSet<string> _rawPhotoIssueKeys;
    private readonly IReadOnlyDictionary<string, ClarifyFlow> _clarifyFlows;

    public string KbVersion => _provider.KbVersion;
    public string FallbackMessage => _provider.Messages.Fallback;

    /// <param name="provider">The eligibility-filtered provider. All served content
    /// comes exclusively from here.</param>
    /// <param name="rawKb">The raw KB, used solely for clarify-flow navigation
    /// structure and dangling-vs-ineligible classification. Never a source of
    /// served content. When null, clarify navigation is unavailable and broken
    /// references are conservatively reported as dangling.</param>
    public ReferenceValidator(KnowledgeBaseProvider provider, KnowledgeBase? rawKb = null)
    {
        _provider = provider;
        _rawKb = rawKb;
        _rawStatementIds = rawKb is null ? [] : [.. rawKb.Statements.Select(s => s.Id)];
        _rawFaqIds = rawKb is null ? [] : [.. rawKb.Faqs.Select(f => f.Id)];
        _rawPhotoIssueKeys = rawKb is null ? [] : [.. rawKb.PhotoIssues.Keys];
        _clarifyFlows = rawKb?.ClarifyFlows ?? new Dictionary<string, ClarifyFlow>();
    }

    private PruneReason Classify(bool existsInRaw)
        // When we have the raw KB and the id exists there, it was filtered for
        // eligibility; otherwise it is genuinely dangling.
        => _rawKb is not null && existsInRaw ? PruneReason.Ineligible : PruneReason.Dangling;

    private void RecordStatement(AnswerPathKind path, string source, string id)
        => _pruned.Add(new PrunedReference(path, source, RefKind.Statement, id,
            Classify(_rawStatementIds.Contains(id))));

    /// <summary>Resolve statement ids with accurate dangling/ineligible classification.
    /// Returns the eligible subset and whether ANY id was pruned.</summary>
    private (List<Statement> Statements, bool AnyPruned) ResolveStatements(
        IReadOnlyList<string> statementIds, AnswerPathKind path, string source)
    {
        var statements = new List<Statement>();
        var anyPruned = false;
        foreach (var id in statementIds)
        {
            var statement = _provider.GetStatementById(id);
            if (statement is null)
            {
                RecordStatement(path, source, id);
                anyPruned = true;
                continue;
            }
            statements.Add(statement);
        }
        return (statements, anyPruned);
    }

    private ValidatedClarifyOption? ResolveClarifyOptionInternal(string flowId, ClarifyFlowOption option)
    {
        var source = $"clarifyFlow {flowId}/{option.Id}";

        // freeText options accept free-form input and carry no id reference.
        if (option.FreeText == true)
        {
            return new ValidatedClarifyOption(flowId, option.Id, option.Label, new ClarifyTarget.FreeText());
        }

        // (c) FAQ-id option — the FAQ must exist and be eligible.
        if (option.Faq is not null)
        {
            var faq = _provider.GetFaqById(option.Faq);
            if (faq is null)
            {
                _pruned.Add(new PrunedReference(AnswerPathKind.ClarifyFaq, source, RefKind.Faq,
                    option.Faq, Classify(_rawFaqIds.Contains(option.Faq))));
                return null;
            }
            return new ValidatedClarifyOption(flowId, option.Id, option.Label, new ClarifyTarget.FaqTarget(faq.Id));
        }

        // (d) issue option — the photoIssues key must exist and be eligible.
        if (option.Issue is not null)
        {
            var issue = _provider.GetPhotoIssueByKey(option.Issue);
            if (issue is null)
            {
                _pruned.Add(new PrunedReference(AnswerPathKind.ClarifyIssue, source, RefKind.PhotoIssue,
                    option.Issue, Classify(_rawPhotoIssueKeys.Contains(option.Issue))));
                return null;
            }
            return new ValidatedClarifyOption(flowId, option.Id, option.Label, new ClarifyTarget.Issue(option.Issue));
        }

        // (b) inline answer option — served ONLY if ALL statementIds are eligible.
        if (option.Answer is not null)
        {
            var ids = option.StatementIds ?? [];
            var (statements, anyPruned) = ResolveStatements(ids, AnswerPathKind.ClarifyAnswer, source);
            if (anyPruned) return null; // all-or-nothing
            return new ValidatedClarifyOption(flowId, option.Id, option.Label,
                new ClarifyTarget.Answer(option.Answer, statements));
        }

        // `next` option — branches to another flow; no id-to-content reference here.
        if (option.Next is not null)
        {
            return new ValidatedClarifyOption(flowId, option.Id, option.Label, new ClarifyTarget.Next(option.Next));
        }

        // Malformed option (none of next/faq/answer/issue/freeText): skip to fallback.
        return null;
    }

    /// <summary>
    /// Resolve a direct-FAQ answer path for a FAQ id. Returns null (→ caller uses
    /// messages.fallback) when the FAQ is missing, ineligible, or not directly
    /// answerable. Grounding statements are pruned to the eligible subset; the
    /// approved directAnswer is the served content for this path.
    /// </summary>
    public ValidatedDirectFaq? ResolveDirectFaq(string faqId)
    {
        var faq = _provider.GetFaqById(faqId);
        if (faq is null)
        {
            _pruned.Add(new PrunedReference(AnswerPathKind.DirectFaq, faqId, RefKind.Faq, faqId,
                Classify(_rawFaqIds.Contains(faqId))));
            return null;
        }
        if (string.IsNullOrEmpty(faq.DirectAnswer))
        {
            // Eligible but not directly answerable → not a direct-FAQ answer path.
            return null;
        }
        var (statements, _) = ResolveStatements(faq.StatementIds, AnswerPathKind.DirectFaq, faq.Id);
        return new ValidatedDirectFaq(faq, statements);
    }

    public ValidatedClarifyOption? ResolveClarifyOption(string flowId, string optionId)
    {
        if (!_clarifyFlows.TryGetValue(flowId, out var flow)) return null;
        var option = flow.Options.FirstOrDefault(o => o.Id == optionId);
        return option is null ? null : ResolveClarifyOptionInternal(flowId, option);
    }

    public IReadOnlyList<ValidatedClarifyOption> ResolveClarifyFlow(string flowId)
    {
        if (!_clarifyFlows.TryGetValue(flowId, out var flow)) return [];
        var resolved = new List<ValidatedClarifyOption>();
        foreach (var option in flow.Options)
        {
            var validated = ResolveClarifyOptionInternal(flowId, option);
            if (validated is not null) resolved.Add(validated);
        }
        return resolved;
    }

    public ValidatedPhotoIssue? ResolvePhotoIssue(string key)
    {
        var issue = _provider.GetPhotoIssueByKey(key);
        if (issue is null)
        {
            _pruned.Add(new PrunedReference(AnswerPathKind.PhotoIssue, key, RefKind.PhotoIssue, key,
                Classify(_rawPhotoIssueKeys.Contains(key))));
            return null;
        }
        var (statements, _) = ResolveStatements(issue.StatementIds, AnswerPathKind.PhotoIssue, key);
        return new ValidatedPhotoIssue(key, issue, statements);
    }

    /// <summary>The eligible statements usable to ground a Bedrock prompt (path f).</summary>
    public IReadOnlyList<Statement> GetGroundingStatements() => _provider.GetEligibleStatements();

    /// <summary>All broken references discovered while building the pruned graph.</summary>
    public IReadOnlyList<PrunedReference> GetPrunedReferences() => [.. _pruned];
}
