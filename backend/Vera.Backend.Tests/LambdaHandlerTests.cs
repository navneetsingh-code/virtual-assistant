using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Vera.Backend;
using Vera.Backend.Clarify;
using Vera.Backend.Generation;
using Vera.Backend.Kb;
using Vera.Backend.Logging;
using Vera.Backend.Models;
using Vera.Backend.Photo;
using Vera.Backend.Pii;
using Vera.Backend.Pipeline;
using MatcherType = Vera.Backend.Matcher.Matcher;
using GenerationType = Vera.Backend.Generation.Generation;

namespace Vera.Backend.Tests;

/// <summary>
/// Tests for the API Gateway Lambda entry adapter (tasks.md 9.1 completion).
/// Asserts request parsing, the wire contract round-trip, and that a malformed
/// body yields a 400 without echoing the raw body.
/// </summary>
public class LambdaHandlerTests
{
    private sealed class FakeClient(string reply) : IBedrockClient
    {
        public Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken ct = default)
            => Task.FromResult(new BedrockResponse(reply));
    }

    private static Pipeline.Pipeline BuildPipeline()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var provider = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var validator = new ReferenceValidator(provider, kb);
        return new Pipeline.Pipeline(new PipelineDependencies
        {
            Provider = provider,
            Redactor = new PiiRedactor(),
            Logger = new Logger(new InMemoryLogSink()),
            Matcher = new MatcherType(provider, validator),
            Clarify = new ClarifyFlowEngine(validator, kb),
            Photo = new PhotoIssueResolver(validator),
            Retrieval = new Retrieval(validator, provider),
            Generation = new GenerationType(new FakeClient("unused"), provider.Messages, provider.KbVersion),
        });
    }

    [Fact]
    public async Task ReturnsDirectFaqAnswerFor200()
    {
        var handler = new LambdaHandler(BuildPipeline());
        var req = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new { sessionId = "s1", message = "What image formats are supported?" }),
        };

        var res = await handler.HandleAsync(req, new TestLambdaContext());

        Assert.Equal(200, res.StatusCode);
        var body = JsonSerializer.Deserialize<MessageResponse>(res.Body);
        Assert.NotNull(body);
        Assert.False(body!.IsFallback);
        Assert.Equal("FAQ-UPL-02", body.SourceFaqId);
        Assert.Equal("Please upload a JPEG or PNG image.", body.Reply);
    }

    [Fact]
    public async Task MalformedBodyReturns400WithoutEchoingBody()
    {
        var handler = new LambdaHandler(BuildPipeline());
        var req = new APIGatewayProxyRequest { Body = "{ not json" };

        var res = await handler.HandleAsync(req, new TestLambdaContext());

        Assert.Equal(400, res.StatusCode);
        Assert.DoesNotContain("not json", res.Body);
        Assert.Contains("Invalid request body", res.Body);
    }

    [Fact]
    public async Task MissingBodyReturns400()
    {
        var handler = new LambdaHandler(BuildPipeline());
        var res = await handler.HandleAsync(new APIGatewayProxyRequest { Body = null }, new TestLambdaContext());
        Assert.Equal(400, res.StatusCode);
    }

    [Fact]
    public async Task EmitsCorsAllowOriginHeaderOnResponses()
    {
        // The browser can only read the POST response if it carries this header
        // (the API Gateway preflight only covers OPTIONS).
        var handler = new LambdaHandler(BuildPipeline(), corsAllowOrigin: "https://idva.example.gov");
        var req = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new { sessionId = "s", message = "What image formats are supported?" }),
        };

        var res = await handler.HandleAsync(req, new TestLambdaContext());

        Assert.True(res.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.Equal("https://idva.example.gov", res.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task CorsHeaderDefaultsToWildcardWhenUnset()
    {
        var handler = new LambdaHandler(BuildPipeline(), corsAllowOrigin: null);
        var req = new APIGatewayProxyRequest { Body = "{ bad" };
        var res = await handler.HandleAsync(req, new TestLambdaContext());
        // Even the 400 path carries a CORS header so the browser can read the error.
        Assert.Equal("*", res.Headers["Access-Control-Allow-Origin"]);
    }

    [Fact]
    public async Task RedactsPiiAndStillAnswers()
    {
        var handler = new LambdaHandler(BuildPipeline());
        var req = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new
            {
                sessionId = "s2",
                message = "What image formats are supported? my ssn is 123-45-6789",
            }),
        };

        var res = await handler.HandleAsync(req, new TestLambdaContext());

        Assert.Equal(200, res.StatusCode);
        // The raw SSN never appears in the response envelope.
        Assert.DoesNotContain("123-45-6789", res.Body);
        var body = JsonSerializer.Deserialize<MessageResponse>(res.Body);
        Assert.Equal("FAQ-UPL-02", body!.SourceFaqId);
    }
}
