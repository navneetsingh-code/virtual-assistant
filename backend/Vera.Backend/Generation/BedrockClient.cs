using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

namespace Vera.Backend.Generation;

/// <summary>
/// Configuration for the real Amazon Bedrock adapter.
/// </summary>
public sealed record BedrockOptions
{
    /// <summary>The Bedrock model id / inference profile id to invoke (free-form path only).</summary>
    public required string ModelId { get; init; }

    /// <summary>Per-call timeout. The Grounding_Component converts a timeout into a safe
    /// fallback (Property 14), so this bounds free-form latency.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Sampling temperature (low: keep answers close to the grounded context).</summary>
    public float Temperature { get; init; } = 0.2f;
}

/// <summary>
/// The production <see cref="IBedrockClient"/> — an adapter over
/// <see cref="IAmazonBedrockRuntime"/> using the Converse API. This is the only
/// place the AWS Bedrock SDK is touched; the domain depends only on
/// <see cref="IBedrockClient"/> so tests never hit AWS.
///
/// The adapter is deliberately thin: it sends the ALREADY-GROUNDED prompt built
/// by the Generation_Component (approved content only) and returns the model's
/// text. It applies the configured timeout and surfaces any failure by throwing,
/// which the Grounding_Component converts into the privacy-safe fallback
/// (design.md -> Error Handling; Property 14). It performs no PII handling and
/// no prompt construction of its own.
/// </summary>
public sealed class BedrockClient : IBedrockClient
{
    private readonly IAmazonBedrockRuntime _runtime;
    private readonly BedrockOptions _options;

    public BedrockClient(IAmazonBedrockRuntime runtime, BedrockOptions options)
    {
        _runtime = runtime;
        _options = options;
    }

    public async Task<BedrockResponse> GenerateAsync(
        BedrockRequest request, CancellationToken cancellationToken = default)
    {
        var converse = new ConverseRequest
        {
            ModelId = _options.ModelId,
            Messages =
            [
                new Message
                {
                    Role = ConversationRole.User,
                    Content = [new ContentBlock { Text = request.Prompt }],
                },
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens = request.MaxTokens,
                Temperature = _options.Temperature,
            },
        };

        // Enforce the configured timeout on top of any cancellation from the host.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        var response = await _runtime.ConverseAsync(converse, timeoutCts.Token);

        // Extract the first text content block. A missing/empty reply is treated
        // as a no-answer by the Grounding_Component (it returns fallback).
        var text = response.Output?.Message?.Content?
            .FirstOrDefault(c => c.Text is not null)?.Text ?? "";

        return new BedrockResponse(text);
    }
}
