using FsCheck;
using FsCheck.Fluent;
using Vera.Backend.Kb;
using GenerationType = Vera.Backend.Generation.Generation;
using Vera.Backend.Generation;

namespace Vera.Backend.Tests;

/// <summary>
/// Retrieval + Generation tests (ported from the TS generation suite). Covers
/// Property 5 (Bedrock not invoked without approved content), Property 4
/// (grounded prompt uses only approved content), Property 12 (traceability),
/// and the no-answer / unavailable fallback (Property 14 support).
/// </summary>
public class GenerationTests
{
    private sealed class FakeClient(string reply) : IBedrockClient
    {
        public List<BedrockRequest> Requests { get; } = [];
        public int CallCount { get; private set; }
        public Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken ct = default)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(new BedrockResponse(reply));
        }
    }

    private sealed class ThrowingClient : IBedrockClient
    {
        public Task<BedrockResponse> GenerateAsync(BedrockRequest request, CancellationToken ct = default)
            => throw new TimeoutException("bedrock unavailable");
    }

    private static (KnowledgeBaseProvider Provider, ReferenceValidator Validator, Retrieval Retrieval) Setup()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var p = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var v = new ReferenceValidator(p, kb);
        return (p, v, new Retrieval(v, p));
    }

    [Fact]
    public void RetrievalReturnsApprovedStatementsForOnTopicQuery()
    {
        var (_, _, retrieval) = Setup();
        var result = retrieval.Retrieve("what image formats are supported");
        Assert.NotEmpty(result.Statements);
        Assert.All(result.Statements, r => Assert.True(Eligibility.IsEligible(r.Statement)));
    }

    [Fact]
    public void RetrievalReturnsEmptyForOffTopicQuery()
    {
        var (_, _, retrieval) = Setup();
        var result = retrieval.Retrieve("zxqw vvvv nonsense quux plover");
        Assert.Empty(result.Statements);
    }

    [Fact]
    public void RetrievalNeverSurfacesIneligibleStatements()
    {
        var (_, _, retrieval) = Setup();
        var result = retrieval.Retrieve("pending draft unconfirmed excluded results statement");
        var ids = result.Statements.Select(r => r.Statement.Id).ToHashSet();
        foreach (var excluded in new[] { "ST-UPL-06", "ST-RES-01", "ST-RES-02", "ST-RES-03", "ST-DRAFT-01", "ST-UNCONF-01" })
            Assert.DoesNotContain(excluded, ids);
    }

    [Fact]
    public async Task Property5_EmptyRetrievalReturnsFallbackAndNeverCallsClient()
    {
        var (p, _, _) = Setup();
        var client = new FakeClient("should never be returned");
        var gen = new GenerationType(client, p.Messages, p.KbVersion);

        var result = await gen.AnswerAsync("anything", new RetrievalResult { Statements = [] });

        Assert.True(result.IsFallback);
        Assert.Equal(p.Messages.Fallback, result.Reply);
        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task Property4_PromptContainsOnlyApprovedContentPlusNoAnswerInstruction()
    {
        var (p, _, retrieval) = Setup();
        var client = new FakeClient("Please upload a JPEG or PNG image.");
        var gen = new GenerationType(client, p.Messages, p.KbVersion);

        var r = retrieval.Retrieve("what image formats are supported");
        Assert.NotEmpty(r.Statements);
        await gen.AnswerAsync("what image formats are supported", r);

        var prompt = Assert.Single(client.Requests).Prompt;
        Assert.Contains(GenerationType.NoAnswerSignal, prompt);
        foreach (var s in r.Statements) Assert.Contains(s.Statement.Text, prompt);

        foreach (var ineligible in new[]
                 {
                     "Pending statement, not yet confirmed.", "Draft statement.",
                     "Active but review not confirmed.", "Excluded results statement (pending review).",
                 })
        {
            Assert.DoesNotContain(ineligible, prompt);
        }
    }

    [Fact]
    public async Task ModelNoAnswerSignalFallsBack()
    {
        var (p, _, retrieval) = Setup();
        var gen = new GenerationType(new FakeClient(GenerationType.NoAnswerSignal), p.Messages, p.KbVersion);
        var r = retrieval.Retrieve("what image formats are supported");
        var result = await gen.AnswerAsync("what image formats are supported", r);
        Assert.True(result.IsFallback);
        Assert.Equal(p.Messages.Fallback, result.Reply);
    }

    [Fact]
    public async Task GenerationUnavailableFallsBack()
    {
        var (p, _, retrieval) = Setup();
        var gen = new GenerationType(new ThrowingClient(), p.Messages, p.KbVersion);
        var r = retrieval.Retrieve("what image formats are supported");
        var result = await gen.AnswerAsync("what image formats are supported", r);
        Assert.True(result.IsFallback);
        Assert.Equal(p.Messages.Fallback, result.Reply);
    }

    [Fact]
    public async Task Property12_GroundedAnswerCarriesFaqIdAndKbVersion()
    {
        var (p, _, retrieval) = Setup();
        var gen = new GenerationType(new FakeClient("A grounded answer about formats."), p.Messages, p.KbVersion);
        var r = retrieval.Retrieve("what image formats are supported");
        Assert.NotNull(r.BestFaqId);
        var result = await gen.AnswerAsync("what image formats are supported", r);
        Assert.False(result.IsFallback);
        Assert.Equal(r.BestFaqId, result.SourceFaqId);
        Assert.Equal(p.KbVersion, result.KbVersion);
    }

    [Fact]
    public void Property5_OffTopicQueriesNeverInvokeModel()
    {
        var (p, _, retrieval) = Setup();
        var gen = Gen.NonEmptyListOf(
            Gen.Elements("zxqw", "vvvv", "plover", "quux", "frobnic", "wibble", "grault"));

        Prop.ForAll(Arb.From(gen), tokens =>
        {
            var query = string.Join(' ', tokens);
            var r = retrieval.Retrieve(query);
            if (r.Statements.Count > 0) return true; // skip rare accidental match
            var client = new FakeClient("x");
            var g = new GenerationType(client, p.Messages, p.KbVersion);
            var result = g.AnswerAsync(query, r).GetAwaiter().GetResult();
            return result.IsFallback && client.CallCount == 0;
        }).QuickCheckThrowOnFailure();
    }
}
