using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Vera.Backend.Generation;

namespace Vera.Backend.Tests;

/// <summary>
/// Tests for the real Bedrock adapter (tasks.md 8.2 / 9 completion). A fake
/// IAmazonBedrockRuntime stands in for AWS, so no network/AWS call is made.
/// Asserts the grounded prompt is passed through to the Converse request and
/// the model's text is returned; a thrown SDK error propagates (the
/// Grounding_Component converts it to a safe fallback upstream).
/// </summary>
public class BedrockClientTests
{
    private sealed class FakeRuntime(Func<ConverseRequest, ConverseResponse> handler)
        : AmazonBedrockRuntimeClient
    {
        private readonly Func<ConverseRequest, ConverseResponse> _handler = handler;
        public ConverseRequest? LastRequest { get; private set; }

        public override Task<ConverseResponse> ConverseAsync(
            ConverseRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }

    private static ConverseResponse TextResponse(string text) => new()
    {
        Output = new ConverseOutput
        {
            Message = new Message
            {
                Role = ConversationRole.Assistant,
                Content = [new ContentBlock { Text = text }],
            },
        },
    };

    [Fact]
    public async Task PassesGroundedPromptAndReturnsModelText()
    {
        var runtime = new FakeRuntime(_ => TextResponse("Please upload a JPEG or PNG image."));
        var client = new BedrockClient(runtime, new BedrockOptions { ModelId = "test.model" });

        var result = await client.GenerateAsync(new BedrockRequest("GROUNDED PROMPT", 256));

        Assert.Equal("Please upload a JPEG or PNG image.", result.Text);
        Assert.Equal("test.model", runtime.LastRequest!.ModelId);
        Assert.Equal(256, runtime.LastRequest.InferenceConfig.MaxTokens);
        Assert.Equal("GROUNDED PROMPT", runtime.LastRequest.Messages[0].Content[0].Text);
    }

    [Fact]
    public async Task EmptyOutputYieldsEmptyText()
    {
        var runtime = new FakeRuntime(_ => new ConverseResponse { Output = new ConverseOutput() });
        var client = new BedrockClient(runtime, new BedrockOptions { ModelId = "test.model" });

        var result = await client.GenerateAsync(new BedrockRequest("p", 128));

        // Empty text → the Grounding_Component treats it as a no-answer fallback.
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task SdkErrorPropagates()
    {
        var runtime = new FakeRuntime(_ => throw new AmazonBedrockRuntimeException("unavailable"));
        var client = new BedrockClient(runtime, new BedrockOptions { ModelId = "test.model" });

        await Assert.ThrowsAsync<AmazonBedrockRuntimeException>(
            () => client.GenerateAsync(new BedrockRequest("p", 128)));
    }
}
