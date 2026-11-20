using System.Text.Json.Serialization;

namespace Vera.Backend.Models;

/// <summary>
/// C# records mirroring the approved knowledge base (`kb.json`) as-is, ported
/// from shared/src/kb.ts. The file is consumed WITHOUT modification; eligibility
/// filtering, reference pruning, and runtime guards are applied against these
/// shapes (design.md -> Data Models). No derived/computed fields are added.
///
/// Requirements: 9.5 (version identifier + stable entry identifiers).
/// </summary>

/// <summary>
/// Lifecycle status of a KB entry. Eligible to be served only when
/// <c>active</c> (combined with a confirmed review). Draft/pending retained so
/// the loader can exclude them. Serialized as the exact lowercase strings.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbStatus>))]
public enum KbStatus
{
    [JsonStringEnumMemberName("active")] Active,
    [JsonStringEnumMemberName("draft")] Draft,
    [JsonStringEnumMemberName("pending")] Pending,
}

/// <summary>
/// Team review state of a KB entry. Eligible only when <c>team-confirmed</c>.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<KbReview>))]
public enum KbReview
{
    [JsonStringEnumMemberName("team-confirmed")] TeamConfirmed,
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("unconfirmed")] Unconfirmed,
}

/// <summary>A grounding-corpus statement (e.g. ST-LQC-01).</summary>
public sealed record Statement
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("topic")] public required string Topic { get; init; }
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("status")] public KbStatus Status { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    [JsonPropertyName("review")] public KbReview Review { get; init; }
    [JsonPropertyName("updated")] public string Updated { get; init; } = "";
}

/// <summary>A matchable FAQ entry (e.g. FAQ-UPL-02). Directly answerable only
/// when <see cref="DirectAnswer"/> is non-null (and the entry is eligible).</summary>
public sealed record Faq
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("topic")] public required string Topic { get; init; }
    [JsonPropertyName("suggested")] public bool Suggested { get; init; }
    [JsonPropertyName("questions")] public IReadOnlyList<string> Questions { get; init; } = [];
    [JsonPropertyName("statementIds")] public IReadOnlyList<string> StatementIds { get; init; } = [];

    /// <summary>The approved direct answer, or null when not directly answerable.</summary>
    [JsonPropertyName("directAnswer")] public string? DirectAnswer { get; init; }

    [JsonPropertyName("status")] public KbStatus Status { get; init; }
    [JsonPropertyName("review")] public KbReview Review { get; init; }
    [JsonPropertyName("updated")] public string Updated { get; init; } = "";
}

/// <summary>A single option within a clarify flow. Exactly one target field
/// (<see cref="Next"/>/<see cref="Faq"/>/<see cref="Answer"/>+<see cref="StatementIds"/>/<see cref="Issue"/>)
/// is present per option; <see cref="FreeText"/> options accept free-form input.</summary>
public sealed record ClarifyFlowOption
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("label")] public string Label { get; init; } = "";
    [JsonPropertyName("match")] public IReadOnlyList<string> Match { get; init; } = [];
    [JsonPropertyName("next")] public string? Next { get; init; }
    [JsonPropertyName("faq")] public string? Faq { get; init; }
    [JsonPropertyName("answer")] public string? Answer { get; init; }
    [JsonPropertyName("statementIds")] public IReadOnlyList<string>? StatementIds { get; init; }
    [JsonPropertyName("issue")] public string? Issue { get; init; }
    [JsonPropertyName("freeText")] public bool? FreeText { get; init; }
}

/// <summary>A deterministic clarify-flow decision node (e.g. "upload.checks").</summary>
public sealed record ClarifyFlow
{
    [JsonPropertyName("prompt")] public string Prompt { get; init; } = "";
    [JsonPropertyName("options")] public IReadOnlyList<ClarifyFlowOption> Options { get; init; } = [];
}

/// <summary>Per-issue remediation content (focus/glare/lighting/size).</summary>
public sealed record PhotoIssue
{
    [JsonPropertyName("answer")] public required string Answer { get; init; }
    [JsonPropertyName("statementIds")] public IReadOnlyList<string> StatementIds { get; init; } = [];
    [JsonPropertyName("status")] public KbStatus Status { get; init; }
    [JsonPropertyName("origin")] public string Origin { get; init; } = "";
    [JsonPropertyName("review")] public KbReview Review { get; init; }
    [JsonPropertyName("updated")] public string Updated { get; init; } = "";
}

/// <summary>Canned PII reminder copy (messages.piiReminder).</summary>
public sealed record PiiReminderMessages
{
    [JsonPropertyName("dl")] public string Dl { get; init; } = "";
    [JsonPropertyName("ssn")] public string Ssn { get; init; } = "";
    [JsonPropertyName("dob")] public string Dob { get; init; } = "";
    [JsonPropertyName("general")] public string General { get; init; } = "";
}

/// <summary>Canned small-talk copy (messages.smallTalk).</summary>
public sealed record SmallTalkMessages
{
    [JsonPropertyName("greeting")] public string Greeting { get; init; } = "";
    [JsonPropertyName("thanks")] public string Thanks { get; init; } = "";
    [JsonPropertyName("appreciation")] public string Appreciation { get; init; } = "";
    [JsonPropertyName("acknowledgment")] public string Acknowledgment { get; init; } = "";
    [JsonPropertyName("farewell")] public string Farewell { get; init; } = "";
}

/// <summary>Canned decline copy for out-of-scope request kinds (messages.decline).</summary>
public sealed record DeclineMessages
{
    [JsonPropertyName("eligibility_decision")] public string EligibilityDecision { get; init; } = "";
    [JsonPropertyName("legal_advice")] public string LegalAdvice { get; init; } = "";
    [JsonPropertyName("unconfirmed_detail")] public string UnconfirmedDetail { get; init; } = "";
}

/// <summary>Canonical user-facing copy. Vera sources these strings and does not
/// invent them (design.md -> Data Models -> messages).</summary>
public sealed record KbMessages
{
    [JsonPropertyName("welcome")] public string Welcome { get; init; } = "";
    [JsonPropertyName("piiNotice")] public string PiiNotice { get; init; } = "";
    [JsonPropertyName("maskingNotice")] public string MaskingNotice { get; init; } = "";
    [JsonPropertyName("fallback")] public string Fallback { get; init; } = "";
    [JsonPropertyName("tryAgainLater")] public string TryAgainLater { get; init; } = "";
    [JsonPropertyName("clarify")] public string Clarify { get; init; } = "";
    [JsonPropertyName("piiRephrase")] public string PiiRephrase { get; init; } = "";
    [JsonPropertyName("busy")] public string Busy { get; init; } = "";
    [JsonPropertyName("networkError")] public string NetworkError { get; init; } = "";
    [JsonPropertyName("unresolved")] public string Unresolved { get; init; } = "";
    [JsonPropertyName("piiUploadGuidance")] public string PiiUploadGuidance { get; init; } = "";
    [JsonPropertyName("decline")] public DeclineMessages Decline { get; init; } = new();
    [JsonPropertyName("piiReminder")] public PiiReminderMessages PiiReminder { get; init; } = new();
    [JsonPropertyName("smallTalk")] public SmallTalkMessages SmallTalk { get; init; } = new();
    [JsonPropertyName("clarifyRetry")] public string ClarifyRetry { get; init; } = "";
}

/// <summary>The full knowledge base as authored in `kb.json`, consumed as-is.</summary>
public sealed record KnowledgeBase
{
    [JsonPropertyName("kbVersion")] public required string KbVersion { get; init; }
    [JsonPropertyName("lastUpdated")] public string LastUpdated { get; init; } = "";
    [JsonPropertyName("uiLabels")] public IReadOnlyList<string> UiLabels { get; init; } = [];
    [JsonPropertyName("messages")] public KbMessages Messages { get; init; } = new();
    [JsonPropertyName("clarifyFlows")] public IReadOnlyDictionary<string, ClarifyFlow> ClarifyFlows { get; init; }
        = new Dictionary<string, ClarifyFlow>();
    [JsonPropertyName("photoIssues")] public IReadOnlyDictionary<string, PhotoIssue> PhotoIssues { get; init; }
        = new Dictionary<string, PhotoIssue>();
    [JsonPropertyName("statements")] public IReadOnlyList<Statement> Statements { get; init; } = [];
    [JsonPropertyName("faqs")] public IReadOnlyList<Faq> Faqs { get; init; } = [];
}
