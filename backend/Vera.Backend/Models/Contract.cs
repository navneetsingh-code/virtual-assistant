using System.Text.Json.Serialization;

namespace Vera.Backend.Models;

/// <summary>
/// Shared request/response contract between the Chat_Widget and the Backend_API
/// (ported 1:1 from shared/src/contract.ts). The on-the-wire JSON is identical
/// to the TypeScript contract so the existing TypeScript widget is unaffected
/// by the backend migration to C#/.NET 10.
///
/// Design: design.md -> Components and Interfaces -> Chat_Widget interface.
/// Requirements: 2.4 (widget calls Backend_API), 5.3 (traceability), 9.5
/// (version + id).
/// </summary>
public sealed record MessageRequest
{
    /// <summary>Client-generated identifier for the current Session.</summary>
    [JsonPropertyName("sessionId")]
    public string SessionId { get; init; } = "";

    /// <summary>The user's message, already Layer-1 redacted by the widget.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";

    /// <summary>
    /// Optional clarify-flow context supplied by the (stateful) widget. When
    /// present the message is treated as a selection within that flow.
    /// </summary>
    [JsonPropertyName("flowId")]
    public string? FlowId { get; init; }
}

/// <summary>
/// Response body returned by the Backend_API to the Chat_Widget. Property names
/// match the TypeScript <c>MessageResponse</c> exactly; <see cref="SourceFaqId"/>
/// is omitted from the JSON when null (matches the optional TS field).
/// </summary>
public sealed record MessageResponse
{
    /// <summary>The user-facing reply (an approved answer or a fallback message).</summary>
    [JsonPropertyName("reply")]
    public required string Reply { get; init; }

    /// <summary>True when <see cref="Reply"/> is a Fallback_Response.</summary>
    [JsonPropertyName("isFallback")]
    public required bool IsFallback { get; init; }

    /// <summary>
    /// The source FAQ_Entry identifier when the answer is traceable to a FAQ
    /// (direct or generated). Absent for fallbacks and non-FAQ paths.
    /// </summary>
    [JsonPropertyName("sourceFaqId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceFaqId { get; init; }

    /// <summary>The knowledge-base version used to produce this response.</summary>
    [JsonPropertyName("kbVersion")]
    public required string KbVersion { get; init; }
}
