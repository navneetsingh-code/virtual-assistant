using Vera.Backend.Clarify;
using Vera.Backend.Kb;
using Vera.Backend.Photo;
using MatcherType = Vera.Backend.Matcher.Matcher;

namespace Vera.Backend.Tests;

/// <summary>
/// Deterministic answer-path tests (ported from the TS matcher/clarify/photo
/// suites): direct FAQ matching (Property 3), clarify navigation, and
/// photo-issue resolution.
/// </summary>
public class DeterministicTests
{
    private static (KnowledgeBaseProvider Provider, ReferenceValidator Validator) Setup()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var p = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var v = new ReferenceValidator(p, kb);
        return (p, v);
    }

    [Fact]
    public void DirectFaqExactMatchReturnsApprovedAnswer()
    {
        var (p, v) = Setup();
        var matcher = new MatcherType(p, v);
        var match = matcher.MatchDirectFaq("What image formats are supported?");
        Assert.NotNull(match);
        Assert.Equal("FAQ-UPL-02", match!.SourceFaqId);
        Assert.Equal("Please upload a JPEG or PNG image.", match.DirectAnswer);
        Assert.Equal("2026.10.1", match.KbVersion);
    }

    [Fact]
    public void DirectFaqNoMatchReturnsNull()
    {
        var (p, v) = Setup();
        var matcher = new MatcherType(p, v);
        Assert.Null(matcher.MatchDirectFaq("zxqw vvvv nonsense quux plover"));
    }

    [Fact]
    public void ClarifyGlareIssueResolvesToIssueKey()
    {
        var (_, v) = Setup();
        var engine = new ClarifyFlowEngine(v, TestFixtures.MakeKnowledgeBase());
        var nav = engine.Navigate("upload.checks", "there is glare on the card");
        var issue = Assert.IsType<ClarifyNavigationResult.Issue>(nav);
        Assert.Equal("glare", issue.IssueKey);
    }

    [Fact]
    public void ClarifyUnknownFlowFallsBack()
    {
        var (_, v) = Setup();
        var engine = new ClarifyFlowEngine(v, TestFixtures.MakeKnowledgeBase());
        var nav = engine.Navigate("does.not.exist", "glare");
        var fb = Assert.IsType<ClarifyNavigationResult.Fallback>(nav);
        Assert.Equal(ClarifyFallbackReason.UnknownFlow, fb.Reason);
    }

    [Fact]
    public void ClarifyDanglingOptionIsSkippedNoMatchFallsBack()
    {
        var (_, v) = Setup();
        var engine = new ClarifyFlowEngine(v, TestFixtures.MakeKnowledgeBase());
        // "unsure" targets a dangling FAQ → pruned; matching it falls back.
        var nav = engine.Navigate("upload.checks", "unsure");
        Assert.IsType<ClarifyNavigationResult.Fallback>(nav);
    }

    [Fact]
    public void PhotoIssueGlareResolvesApprovedAnswer()
    {
        var (_, v) = Setup();
        var resolver = new PhotoIssueResolver(v);
        var result = resolver.Resolve("glare");
        var ok = Assert.IsType<PhotoIssueResult.Resolved>(result);
        Assert.Equal("glare", ok.IssueKey);
        Assert.Contains("glare", ok.Answer);
        Assert.All(ok.Statements, s => Assert.True(Eligibility.IsEligible(s)));
    }

    [Fact]
    public void PhotoIssueIneligibleFocusFallsBack()
    {
        var (_, v) = Setup();
        var resolver = new PhotoIssueResolver(v);
        var result = resolver.Resolve("focus"); // pending in fixture
        var fb = Assert.IsType<PhotoIssueResult.Fallback>(result);
        Assert.Equal(PhotoIssueFallbackReason.UnresolvedReference, fb.Reason);
    }

    [Fact]
    public void PhotoIssueInvalidKeyFallsBack()
    {
        var (_, v) = Setup();
        var resolver = new PhotoIssueResolver(v);
        var result = resolver.Resolve("sideways");
        var fb = Assert.IsType<PhotoIssueResult.Fallback>(result);
        Assert.Equal(PhotoIssueFallbackReason.InvalidKey, fb.Reason);
    }
}
