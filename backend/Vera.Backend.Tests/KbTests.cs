using Vera.Backend.Kb;
using Vera.Backend.Models;

namespace Vera.Backend.Tests;

/// <summary>
/// Eligibility filtering + reference integrity + no-per-user-data tests
/// (ported from the TS kb test suite). Covers Property 6 (reference integrity)
/// and Property 2 (no per-user data access).
/// </summary>
public class KbTests
{
    private static KnowledgeBaseProvider Provider() =>
        KnowledgeBaseProvider.FromKnowledgeBase(TestFixtures.MakeKnowledgeBase());

    [Fact]
    public void EligibilityExcludesDraftPendingUnconfirmedAndNullAnswer()
    {
        var p = Provider();
        var faqIds = p.GetEligibleFaqs().Select(f => f.Id).ToHashSet();
        foreach (var excluded in TestFixtures.ExcludedFaqIds)
            Assert.DoesNotContain(excluded, faqIds);

        var stIds = p.GetEligibleStatements().Select(s => s.Id).ToHashSet();
        foreach (var excluded in TestFixtures.ExcludedStatementIds)
            Assert.DoesNotContain(excluded, stIds);
    }

    [Fact]
    public void EligibleRealExamplesAreAvailable()
    {
        var p = Provider();
        var directIds = p.GetDirectlyAnswerableFaqs().Select(f => f.Id).ToHashSet();
        Assert.Contains("FAQ-UPL-02", directIds);
        Assert.Contains("FAQ-LQC-01", directIds);
    }

    [Fact]
    public void EligibleButNullAnswerFaqIsNotDirectlyAnswerable()
    {
        var p = Provider();
        Assert.Contains(p.GetEligibleFaqs(), f => f.Id == "FAQ-LQC-00");
        Assert.DoesNotContain(p.GetDirectlyAnswerableFaqs(), f => f.Id == "FAQ-LQC-00");
    }

    [Fact]
    public void RuntimeGuardRejectsIneligibleLookups()
    {
        var p = Provider();
        Assert.Null(p.GetStatementById("ST-UPL-06"));   // pending
        Assert.Null(p.GetStatementById("ST-DRAFT-01"));  // draft
        Assert.Null(p.GetStatementById("ST-UNCONF-01")); // unconfirmed
        Assert.NotNull(p.GetStatementById("ST-UPL-03")); // eligible
        Assert.Null(p.GetFaqById("FAQ-UPL-06"));
        Assert.Null(p.GetPhotoIssueByKey("focus"));
        Assert.NotNull(p.GetPhotoIssueByKey("glare"));
    }

    [Fact]
    public void ForbiddenDevPathIsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => KnowledgeBaseProvider.ResolveConfiguredPath(@"D:\WWW\vera\knowledge\kb.json"));
        Assert.Contains("developer authoring path", ex.Message);
    }

    [Fact]
    public void EmptyConfiguredPathIsRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => KnowledgeBaseProvider.ResolveConfiguredPath("   "));
    }

    // --- Property 6: reference integrity across answer-producing paths ---

    [Fact]
    public void DirectFaqResolvesOnlyEligibleGroundingStatements()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var p = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var v = new ReferenceValidator(p, kb);

        var resolved = v.ResolveDirectFaq("FAQ-UPL-02");
        Assert.NotNull(resolved);
        Assert.All(resolved!.Statements, s => Assert.True(Eligibility.IsEligible(s)));
    }

    [Fact]
    public void DanglingClarifyFaqOptionIsPrunedToFallback()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var p = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var v = new ReferenceValidator(p, kb);

        // The "unsure" option targets FAQ-LQC-03, which does not exist → pruned.
        var options = v.ResolveClarifyFlow("upload.checks");
        Assert.DoesNotContain(options, o => o.OptionId == "unsure");
        // The eligible "glare" issue option survives.
        Assert.Contains(options, o => o.OptionId == "glare");

        var pruned = v.GetPrunedReferences();
        Assert.Contains(pruned, r => r.RefId == "FAQ-LQC-03" && r.Reason == PruneReason.Dangling);
    }

    [Fact]
    public void PhotoIssueDanglingOrIneligibleResolvesNull()
    {
        var kb = TestFixtures.MakeKnowledgeBase();
        var p = KnowledgeBaseProvider.FromKnowledgeBase(kb);
        var v = new ReferenceValidator(p, kb);

        Assert.NotNull(v.ResolvePhotoIssue("glare"));   // eligible
        Assert.Null(v.ResolvePhotoIssue("focus"));       // pending → pruned
        Assert.Null(v.ResolvePhotoIssue("nonexistent")); // dangling
    }

    // --- Property 2: no per-user application/verification data access ---

    [Fact]
    public void ProviderExposesOnlyApprovedKbContentNoPerUserData()
    {
        // Structural guarantee: the provider's surface is entirely KB content
        // (statements/faqs/photoIssues/messages). There is no accessor that
        // takes a user id or returns per-user application/verification data.
        var p = Provider();
        var methods = typeof(IKnowledgeBaseProvider).GetMethods()
            .Select(m => m.Name).ToList();
        Assert.All(methods, name =>
            Assert.DoesNotContain("user", name, StringComparison.OrdinalIgnoreCase));
        Assert.All(methods, name =>
            Assert.DoesNotContain("application", name, StringComparison.OrdinalIgnoreCase));
    }
}
