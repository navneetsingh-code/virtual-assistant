using FsCheck;
using FsCheck.Fluent;
using Vera.Backend.Clarify;
using Vera.Backend.Generation;
using Vera.Backend.Kb;
using Vera.Backend.Logging;
using Vera.Backend.Models;
using Vera.Backend.Photo;
using Vera.Backend.Pii;
using Vera.Backend.Pipeline;
using PipelineType = Vera.Backend.Pipeline.Pipeline;
using MatcherType = Vera.Backend.Matcher.Matcher;
using GenerationType = Vera.Backend.Generation.Generation;

namespace Vera.Backend.Tests;

/// <summary>
/// Backend request pipeline tests (ported from the TS pipeline suite). Covers
/// routing, Property 14 (graceful degradation), and Property 10 (no server-side
/// persistence).
/// </summary>
public class PipelineTests
{
    private sealed class FakeClient(string reply) : IBedrockClient
    {
        public int CallCount { get; private set; }
        public Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(new BedrockResponse(reply));
        }
    }

    private static readonly KbMessages Msgs = TestFixtures.Messages();

    private static (PipelineType Pipeline, InMemoryLogSink Sink, IBedrockClient Client) Build(IBedrockClient client)
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var provider = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var validator = new ReferenceValidator(provider, kb);
        var sink = new InMemoryLogSink();
        var deps = new PipelineDependencies
        {
            Provider = provider,
            Redactor = new PiiRedactor(),
            Logger = new Logger(sink),
            Matcher = new MatcherType(provider, validator),
            Clarify = new ClarifyFlowEngine(validator, kb),
            Photo = new PhotoIssueResolver(validator),
            Retrieval = new Retrieval(validator, provider),
            Generation = new GenerationType(client, provider.Messages, provider.KbVersion),
        };
        return (new PipelineType(deps), sink, client);
    }

    [Fact]
    public async Task DirectFaqAnswerForExactQuestion()
    {
        var (pipeline, _, _) = Build(new FakeClient("unused"));
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s1", Message = "What image formats are supported?",
        });
        Assert.False(res.IsFallback);
        Assert.Equal("FAQ-UPL-02", res.SourceFaqId);
        Assert.Equal("Please upload a JPEG or PNG image.", res.Reply);
    }

    [Fact]
    public async Task GroundedFreeFormAnswerWhenRetrievalFindsContent()
    {
        var (pipeline, _, _) = Build(new FakeClient("Grounded: JPEG or PNG."));
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s2", Message = "image formats accepted",
        });
        Assert.False(res.IsFallback);
        Assert.Equal("Grounded: JPEG or PNG.", res.Reply);
        Assert.Equal("2026.10.1", res.KbVersion);
    }

    [Fact]
    public async Task ClarifyIssueResolvesToPhotoIssueAnswer()
    {
        var (pipeline, _, _) = Build(new FakeClient("unused"));
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s3", FlowId = "upload.checks", Message = "glare",
        });
        Assert.False(res.IsFallback);
        Assert.Contains("glare", res.Reply);
    }

    [Fact]
    public async Task PiiRedactedBeforeSinkAndRemainderAnswered()
    {
        var (pipeline, sink, _) = Build(new FakeClient("unused"));
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s4", Message = "What image formats are supported? my ssn is 123-45-6789",
        });
        Assert.False(res.IsFallback);
        Assert.Equal("FAQ-UPL-02", res.SourceFaqId);
        Assert.DoesNotContain("123-45-6789", System.Text.Json.JsonSerializer.Serialize(sink.Entries));
    }

    [Fact]
    public async Task UnsafePiiOnlyMessageReturnsPrivacySafeFallback()
    {
        var (pipeline, sink, _) = Build(new FakeClient("unused"));
        var res = await pipeline.HandleAsync(new MessageRequest { SessionId = "s5", Message = "123-45-6789" });
        Assert.True(res.IsFallback);
        Assert.Equal(FallbackReason.PiiUnsafe, sink.Entries[^1].FallbackReason);
        Assert.DoesNotContain("123-45-6789", System.Text.Json.JsonSerializer.Serialize(sink.Entries));
    }

    // --- Property 14: graceful degradation ---

    [Fact]
    public async Task OffTopicFreeFormFallsBackWithoutInvokingModel()
    {
        var client = new FakeClient("should not be used");
        var (pipeline, _, _) = Build(client);
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s6", Message = "zxqw vvvv plover quux nonsense",
        });
        Assert.True(res.IsFallback);
        Assert.Equal(Msgs.Fallback, res.Reply);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ModelNoAnswerFallsBack()
    {
        var (pipeline, _, _) = Build(new FakeClient("NO_ANSWER"));
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s7", Message = "image formats accepted",
        });
        Assert.True(res.IsFallback);
        Assert.Equal(Msgs.Fallback, res.Reply);
    }

    [Fact]
    public async Task GenerationUnavailableFallsBack()
    {
        var throwing = new ThrowingClient();
        var (pipeline, _, _) = Build(throwing);
        var res = await pipeline.HandleAsync(new MessageRequest
        {
            SessionId = "s8", Message = "image formats accepted",
        });
        Assert.True(res.IsFallback);
        Assert.Equal(Msgs.Fallback, res.Reply);
    }

    private sealed class ThrowingClient : IBedrockClient
    {
        public Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken ct = default)
            => throw new TimeoutException();
    }

    [Fact]
    public async Task UnexpectedErrorDegradesToFallbackAndLogsError()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var provider = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var validator = new ReferenceValidator(provider, kb);
        var sink = new InMemoryLogSink();

        // A retrieval that throws simulates an unexpected internal error.
        var deps = new PipelineDependencies
        {
            Provider = provider,
            Redactor = new PiiRedactor(),
            Logger = new Logger(sink),
            Matcher = new MatcherType(provider, validator),
            Clarify = new ClarifyFlowEngine(validator, kb),
            Photo = new PhotoIssueResolver(validator),
            Retrieval = new ThrowingRetrieval(validator, provider),
            Generation = new GenerationType(new FakeClient("x"), provider.Messages, provider.KbVersion),
        };
        var pipeline = new PipelineType(deps);

        var res = await pipeline.HandleAsync(new MessageRequest { SessionId = "s9", Message = "hello there" });
        Assert.True(res.IsFallback);
        Assert.Equal(Msgs.Fallback, res.Reply);
        var errorEntry = sink.Entries.FirstOrDefault(e => e.Level == LogLevel.Error);
        Assert.NotNull(errorEntry);
        Assert.Equal(FallbackReason.UnexpectedError, errorEntry!.FallbackReason);
    }

    /// <summary>A Retrieval subclass whose Retrieve throws, to exercise the
    /// unexpected-error degradation path.</summary>
    private sealed class ThrowingRetrieval(ReferenceValidator v, KnowledgeBaseProvider p) : Retrieval(v, p)
    {
        public override RetrievalResult Retrieve(string query) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Property14_EveryRequestYieldsWellFormedResponseAndNeverThrows()
    {
        var (pipeline, _, _) = Build(new FakeClient("Grounded answer."));
        var gen =
            from sessionId in Gen.Elements("", "s1", "user-xyz")
            from message in Gen.Elements(
                "", "   ", "What image formats are supported?", "image formats accepted",
                "123-45-6789", "zxqw vvvv plover", "glare on the card")
            select (sessionId, message);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var res = pipeline.HandleAsync(new MessageRequest { SessionId = t.sessionId, Message = t.message })
                .GetAwaiter().GetResult();
            return !string.IsNullOrEmpty(res.Reply) && res.KbVersion == "2026.10.1";
        }).QuickCheckThrowOnFailure();
    }

    // --- Property 10: no server-side persistence ---

    [Fact]
    public async Task NoConversationContentPersistedAndStatelessAcrossTurns()
    {
        var (pipeline, sink, _) = Build(new FakeClient("Grounded answer."));
        await pipeline.HandleAsync(new MessageRequest { SessionId = "p1", Message = "What image formats are supported?" });
        await pipeline.HandleAsync(new MessageRequest { SessionId = "p1", Message = "image formats accepted" });

        var json = System.Text.Json.JsonSerializer.Serialize(sink.Entries).ToLowerInvariant();
        Assert.DoesNotContain("\"message\"", json);
        Assert.DoesNotContain("\"reply\"", json);
        Assert.Equal(2, sink.Entries.Count);
    }
}
