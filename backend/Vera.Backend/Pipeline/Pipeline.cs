using Vera.Backend.Clarify;
using Vera.Backend.Generation;
using Vera.Backend.Kb;
using Vera.Backend.Logging;
using Vera.Backend.Matcher;
using Vera.Backend.Models;
using Vera.Backend.Photo;
using Vera.Backend.Pii;

namespace Vera.Backend.Pipeline;

/// <summary>The components the pipeline orchestrates. All are pre-built per cold start.</summary>
public sealed record PipelineDependencies
{
    public required KnowledgeBaseProvider Provider { get; init; }
    public required PiiRedactor Redactor { get; init; }
    public required ILogger Logger { get; init; }
    public required Matcher.Matcher Matcher { get; init; }
    public required ClarifyFlowEngine Clarify { get; init; }
    public required PhotoIssueResolver Photo { get; init; }
    public required Retrieval Retrieval { get; init; }
    public required Generation.Generation Generation { get; init; }
}

/// <summary>
/// Backend request pipeline + graceful degradation (ported from
/// backend-ts-reference/src/pipeline/pipeline.ts).
///
/// Wires the components in the design-mandated order: Layer 2 PII redaction
/// (before any sink) → operational logging → routing (direct FAQ → clarify /
/// photo-issue when flow context present → retrieval + grounded generation →
/// fallback). Deterministic paths never call Bedrock; Bedrock is invoked only on
/// the free-form path and only when retrieval found relevant approved content
/// (Property 5).
///
/// Graceful degradation (Property 14): the whole routing body runs inside a
/// guard; a model no-answer, retrieval/generation unavailability or timeout, or
/// any unexpected error resolves to the messages.* fallback; unexpected errors
/// are additionally logged at error level with no PII / no raw body.
///
/// Stateless (Req 8.2): no per-request state beyond locals; nothing persisted.
///
/// Requirements: 3.12, 5.4, 6.3, 8.2, 12.1, 12.2, 12.3.
/// </summary>
public sealed class Pipeline
{
    private readonly PipelineDependencies _deps;
    private readonly KbMessages _messages;
    private readonly string _kbVersion;

    public Pipeline(PipelineDependencies deps)
    {
        _deps = deps;
        _messages = deps.Provider.Messages;
        _kbVersion = deps.Provider.KbVersion;
    }

    private MessageResponse Fallback(string sessionId, FallbackReason reason, string? reply = null)
    {
        _deps.Logger.Log(new LogInput { SessionId = sessionId, KbVersion = _kbVersion, FallbackReason = reason });
        return new MessageResponse { Reply = reply ?? _messages.Fallback, IsFallback = true, KbVersion = _kbVersion };
    }

    private void LogAnswer(string sessionId)
        => _deps.Logger.Log(new LogInput { SessionId = sessionId, KbVersion = _kbVersion });

    /// <summary>Handle one message end-to-end. Never throws: any error degrades to
    /// the messages.* fallback (Property 14).</summary>
    public async Task<MessageResponse> HandleAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        var sessionId = request?.SessionId ?? "";
        var rawMessage = request?.Message ?? "";

        try
        {
            // 1) Layer 2 redaction — BEFORE any sink. Never trust the client.
            var redaction = _deps.Redactor.Redact(rawMessage, _messages);

            // 2) Unsafe content → privacy-safe fallback; do NOT log/process the
            //    unsafe original (we log only the operational reason, no content).
            if (!redaction.Safe)
            {
                return Fallback(sessionId, FallbackReason.PiiUnsafe, redaction.FallbackMessage ?? _messages.Fallback);
            }

            var message = redaction.RedactedMessage;

            // 3) Clarify-flow routing (only when the widget supplied flow context).
            if (!string.IsNullOrEmpty(request?.FlowId))
            {
                return await RouteClarifyAsync(request.FlowId!, message, sessionId, cancellationToken);
            }

            // 4) Direct FAQ match (deterministic, no Bedrock).
            var direct = _deps.Matcher.MatchDirectFaq(message);
            if (direct is not null)
            {
                LogAnswer(sessionId);
                return new MessageResponse
                {
                    Reply = direct.DirectAnswer,
                    IsFallback = false,
                    SourceFaqId = direct.SourceFaqId,
                    KbVersion = _kbVersion,
                };
            }

            // 5) Free-form path: retrieval gate, then grounded generation.
            return await RouteFreeFormAsync(message, sessionId, cancellationToken);
        }
        catch (Exception error)
        {
            // Unexpected error → safe fallback + error log (no PII, no raw body).
            _deps.Logger.Log(new LogInput
            {
                SessionId = sessionId,
                KbVersion = _kbVersion,
                FallbackReason = FallbackReason.UnexpectedError,
                Level = LogLevel.Error,
                ErrorKind = error.GetType().Name,
            });
            return new MessageResponse { Reply = _messages.Fallback, IsFallback = true, KbVersion = _kbVersion };
        }
    }

    /// <summary>Free-form path: retrieval gate, then grounded generation, else fallback.</summary>
    private async Task<MessageResponse> RouteFreeFormAsync(
        string message, string sessionId, CancellationToken cancellationToken)
    {
        // No relevant approved content => fallback, Bedrock NOT invoked (Property 5).
        var retrieved = _deps.Retrieval.Retrieve(message);
        if (retrieved.Statements.Count == 0) return Fallback(sessionId, FallbackReason.NoMatch);

        var generated = await _deps.Generation.AnswerAsync(message, retrieved, cancellationToken);
        if (generated.IsFallback)
        {
            // Model emitted no-answer, or generation was unavailable/errored.
            return Fallback(sessionId, FallbackReason.ModelNoAnswer, generated.Reply);
        }

        LogAnswer(sessionId);
        return new MessageResponse
        {
            Reply = generated.Reply,
            IsFallback = false,
            SourceFaqId = generated.SourceFaqId,
            KbVersion = _kbVersion,
        };
    }

    /// <summary>Deterministic clarify-flow navigation. No Bedrock on the
    /// deterministic branches; free-text options defer to the free-form path.</summary>
    private async Task<MessageResponse> RouteClarifyAsync(
        string flowId, string message, string sessionId, CancellationToken cancellationToken)
    {
        var nav = _deps.Clarify.Navigate(flowId, message);
        switch (nav)
        {
            case ClarifyNavigationResult.FaqResult faqRes:
            {
                var faq = _deps.Provider.GetFaqById(faqRes.FaqId);
                if (faq is null || string.IsNullOrEmpty(faq.DirectAnswer))
                {
                    return Fallback(sessionId, FallbackReason.NoApprovedContent);
                }
                LogAnswer(sessionId);
                return new MessageResponse
                {
                    Reply = faq.DirectAnswer!,
                    IsFallback = false,
                    SourceFaqId = faq.Id,
                    KbVersion = _kbVersion,
                };
            }
            case ClarifyNavigationResult.Answer ans:
                LogAnswer(sessionId);
                return new MessageResponse { Reply = ans.AnswerText, IsFallback = false, KbVersion = _kbVersion };
            case ClarifyNavigationResult.Issue issue:
            {
                var resolved = _deps.Photo.Resolve(issue.IssueKey);
                if (resolved is PhotoIssueResult.Fallback fb)
                {
                    return Fallback(sessionId, FallbackReason.NoApprovedContent, fb.Reply);
                }
                var ok = (PhotoIssueResult.Resolved)resolved;
                LogAnswer(sessionId);
                return new MessageResponse { Reply = ok.Answer, IsFallback = false, KbVersion = _kbVersion };
            }
            case ClarifyNavigationResult.Next next:
            {
                var prompt = _deps.Clarify.GetPrompt(next.FlowId);
                if (prompt is null) return Fallback(sessionId, FallbackReason.NoApprovedContent);
                LogAnswer(sessionId);
                return new MessageResponse { Reply = prompt, IsFallback = false, KbVersion = _kbVersion };
            }
            case ClarifyNavigationResult.FreeText:
                return await RouteFreeFormAsync(message, sessionId, cancellationToken);
            case ClarifyNavigationResult.Fallback fallback:
            default:
                var reply = (nav as ClarifyNavigationResult.Fallback)?.Reply;
                return Fallback(sessionId, FallbackReason.NoMatch, reply);
        }
    }
}
