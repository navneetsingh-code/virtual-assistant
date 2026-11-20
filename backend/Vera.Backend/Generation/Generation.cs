using System.Text;
using Vera.Backend.Models;

namespace Vera.Backend.Generation;

/// <summary>A request to the model: the grounded prompt and bounded generation knobs.</summary>
public sealed record BedrockRequest(string Prompt, int MaxTokens);

/// <summary>The model's reply. Empty/no-answer text → fallback.</summary>
public sealed record BedrockResponse(string Text);

/// <summary>
/// The injected Bedrock boundary. The concrete AWS SDK adapter is provided by
/// infrastructure wiring; this module depends only on the interface so it is
/// testable with a fake and makes no AWS call itself. Implementations SHOULD
/// enforce a timeout and surface unavailability by throwing — the grounding
/// component converts any throw into a safe fallback.
/// </summary>
public interface IBedrockClient
{
    Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of the free-form path.</summary>
public sealed record GenerationResult
{
    public required string Reply { get; init; }
    public required bool IsFallback { get; init; }
    public string? SourceFaqId { get; init; }
    public required string KbVersion { get; init; }
}

/// <summary>
/// Generation_Component + Grounding_Component — the free-form answer path (ported
/// from backend-ts-reference/src/generation/generation.ts).
///
/// The ONLY place Bedrock is ever invoked, and only after the deterministic
/// paths declined AND retrieval found RELEVANT APPROVED CONTENT. Hard guarantees
/// (Properties 4, 5, 12, 14): the prompt is built ONLY from approved retrieved
/// statements plus a fixed no-answer instruction; never invoked with empty
/// content; a grounded answer carries the best FAQ id + kbVersion; a no-answer
/// signal / unavailability / timeout / error yields the messages.* fallback.
///
/// Requirements: 5.1, 5.2, 5.3, 6.2, 6.3.
/// </summary>
public sealed class Generation
{
    /// <summary>The fixed token a grounded model emits when approved content does
    /// not support an answer.</summary>
    public const string NoAnswerSignal = "NO_ANSWER";

    /// <summary>Conservative default generation cap.</summary>
    public const int DefaultMaxTokens = 512;

    private readonly IBedrockClient _client;
    private readonly KbMessages _messages;
    private readonly string _kbVersion;
    private readonly int _maxTokens;

    public Generation(IBedrockClient client, KbMessages messages, string kbVersion, int? maxTokens = null)
    {
        _client = client;
        _messages = messages;
        _kbVersion = kbVersion;
        _maxTokens = maxTokens ?? DefaultMaxTokens;
    }

    private GenerationResult Fallback()
        => new() { Reply = _messages.Fallback, IsFallback = true, KbVersion = _kbVersion };

    /// <summary>Produce a grounded answer for a free-form query using ONLY the
    /// approved content in <paramref name="retrieval"/>. If retrieval produced no
    /// statements, returns fallback and does NOT invoke Bedrock (Property 5). Any
    /// model no-answer signal / unavailability / timeout / error also yields
    /// fallback (Property 14).</summary>
    public async Task<GenerationResult> AnswerAsync(
        string query, RetrievalResult retrieval, CancellationToken cancellationToken = default)
    {
        // Property 5 (defense-in-depth): never invoke Bedrock without approved content.
        if (retrieval.Statements.Count == 0) return Fallback();

        var approvedStatements = retrieval.Statements.Select(r => r.Statement).ToList();
        var prompt = BuildGroundedPrompt(query, approvedStatements);

        BedrockResponse response;
        try
        {
            response = await _client.GenerateAsync(new BedrockRequest(prompt, _maxTokens), cancellationToken);
        }
        catch
        {
            // Unavailable / timeout / any error → safe fallback (Property 14).
            // The error itself is logged by the pipeline (no PII / no raw body).
            return Fallback();
        }

        if (IsNoAnswer(response.Text)) return Fallback();

        return new GenerationResult
        {
            Reply = response.Text.Trim(),
            IsFallback = false,
            SourceFaqId = retrieval.BestFaqId,
            KbVersion = _kbVersion,
        };
    }

    /// <summary>Build the grounded prompt from APPROVED statements only (Property 4).
    /// The only content inputs are the eligible statements; the instruction block
    /// is fixed copy. Exposed for the grounding property test.</summary>
    public static string BuildGroundedPrompt(string query, IReadOnlyList<Statement> statements)
    {
        var context = new StringBuilder();
        for (var i = 0; i < statements.Count; i++)
        {
            if (i > 0) context.Append('\n');
            context.Append($"[{i + 1}] ({statements[i].Id}) {statements[i].Text}");
        }

        return string.Join('\n',
            "You are Vera, a FAQ assistant for a document-verification app.",
            "Answer the user's question USING ONLY the approved context below.",
            "Do not use outside knowledge. Do not infer facts not present in the context.",
            $"If the context does not support an answer, reply with exactly: {NoAnswerSignal}",
            "",
            "APPROVED CONTEXT:",
            context.ToString(),
            "",
            $"USER QUESTION: {query}",
            "",
            "ANSWER:");
    }

    private static bool IsNoAnswer(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return true;
        var upper = trimmed.ToUpperInvariant();
        return upper == NoAnswerSignal || upper.StartsWith(NoAnswerSignal, StringComparison.Ordinal);
    }
}
