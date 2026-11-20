using FsCheck;
using FsCheck.Fluent;
using Vera.Backend.Pii;

namespace Vera.Backend.Tests;

/// <summary>
/// Layer 2 PiiRedactor tests (ported from the TS pii suite). Covers unit
/// behavior plus Property 7 (independent re-screening), Property 8 (no raw PII
/// at any sink), Property 9 (redact-and-continue), Property 16 (unsafe-content
/// fallback). Property tests use FsCheck with pragmatic iteration counts.
/// </summary>
public class PiiTests
{
    private static readonly PiiRedactor Redactor = new();
    private static readonly Vera.Backend.Models.KbMessages Messages = TestFixtures.Messages();

    private static RedactionResult Redact(string message) => Redactor.Redact(message, Messages);

    private static readonly string SsnPlaceholder = PiiRedactor.Placeholders[SensitiveValueKind.Ssn];
    private static readonly string DobPlaceholder = PiiRedactor.Placeholders[SensitiveValueKind.Dob];
    private static readonly string LicPlaceholder = PiiRedactor.Placeholders[SensitiveValueKind.License];

    // --- Generators (FsCheck 3.x fluent) ---

    private static readonly Gen<string> SsnGen =
        from a in Gen.Choose(100, 999)
        from b in Gen.Choose(10, 99)
        from c in Gen.Choose(1000, 9999)
        select $"{a}-{b}-{c}";

    private static Gen<string> TextGen() =>
        Gen.Elements("", "please help", "about my upload", "question here", "tell me more");

    [Theory]
    [InlineData("My number is 123-45-6789 thanks", "123-45-6789")]
    [InlineData("ssn 123 45 6789", "123 45 6789")]
    [InlineData("here it is 123456789 ok", "123456789")]
    public void RedactsSsn(string message, string raw)
    {
        var r = Redact(message);
        Assert.Contains(SsnPlaceholder, r.RedactedMessage);
        Assert.DoesNotContain(raw, r.RedactedMessage);
        Assert.Contains(SensitiveValueKind.Ssn, r.RedactedKinds);
    }

    [Theory]
    [InlineData("My DOB is 01/15/1990", "01/15/1990")]
    [InlineData("born 1990-01-15 somewhere", "1990-01-15")]
    public void RedactsDob(string message, string raw)
    {
        var r = Redact(message);
        Assert.Contains(DobPlaceholder, r.RedactedMessage);
        Assert.DoesNotContain(raw, r.RedactedMessage);
    }

    [Theory]
    [InlineData("my driver's license number is D1234567", "D1234567")]
    [InlineData("is A1B2C3D4 valid?", "A1B2C3D4")]
    public void RedactsLicense(string message, string raw)
    {
        var r = Redact(message);
        Assert.Contains(LicPlaceholder, r.RedactedMessage);
        Assert.DoesNotContain(raw, r.RedactedMessage);
    }

    [Fact]
    public void RedactAndContinueKeepsTheRemainingQuestionSafe()
    {
        var r = Redact("What formats are supported? My SSN is 123-45-6789.");
        Assert.True(r.Safe);
        Assert.Null(r.FallbackMessage);
        Assert.Contains("What formats are supported?", r.RedactedMessage);
        Assert.Contains(SsnPlaceholder, r.RedactedMessage);
    }

    [Fact]
    public void NoPiiReturnsOriginalSafeAndUnchanged()
    {
        const string clean = "How do I upload my document?";
        var r = Redact(clean);
        Assert.Equal(clean, r.RedactedMessage);
        Assert.True(r.Safe);
        Assert.Equal(0, r.RedactionCount);
        Assert.Empty(r.RedactedKinds);
    }

    [Fact]
    public void IndependentReScreeningIgnoresClientClaims()
    {
        var r = Redact("[client redacted] but really 123-45-6789");
        Assert.DoesNotContain("123-45-6789", r.RedactedMessage);
        Assert.Contains(SsnPlaceholder, r.RedactedMessage);
    }

    [Fact]
    public void ExistingPlaceholderIsNotReRedactedOrCounted()
    {
        var r = Redact($"What is this? {SsnPlaceholder}");
        Assert.Equal(0, r.RedactionCount);
        Assert.Empty(r.RedactedKinds);
        Assert.Contains(SsnPlaceholder, r.RedactedMessage);
    }

    [Fact]
    public void UnsafeMostlyPersonalDetailUsesPiiRephrase()
    {
        var r = Redact("123-45-6789");
        Assert.False(r.Safe);
        Assert.Equal(Messages.PiiRephrase, r.FallbackMessage);
        Assert.DoesNotContain("123-45-6789", r.RedactedMessage);
    }

    [Fact]
    public void UnsafeManyValuesFlagged()
    {
        var r = Redact("123-45-6789 987-65-4321 111-22-3333 444-55-6666 dob 01/02/1990");
        Assert.False(r.Safe);
        Assert.NotNull(r.FallbackMessage);
    }

    // --- Property 7: server re-screens regardless of client claims ---

    private static readonly string[] ClientClaims =
        ["[client redacted]", "(already masked)", "PII removed:", "redacted by widget", ""];

    [Fact]
    public void Property7_ServerReScreensRawSsnDespiteClientClaim()
    {
        var gen =
            from claim in Gen.Elements(ClientClaims)
            from ssn in SsnGen
            from tail in TextGen()
            select (claim, ssn, tail);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var message = $"{t.claim} {t.ssn} {t.tail}".Trim();
            var r = Redact(message);
            return !r.RedactedMessage.Contains(t.ssn) && r.RedactionCount >= 1;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property7_ReScreeningIsIdempotent()
    {
        Prop.ForAll(Arb.From(SsnGen), raw =>
        {
            var once = Redact(raw);
            var twice = Redact(once.RedactedMessage);
            return twice.RedactedMessage == once.RedactedMessage && twice.RedactionCount == 0;
        }).QuickCheckThrowOnFailure();
    }

    // --- Property 8: no raw Sensitive_Value survives in the redacted output ---

    [Fact]
    public void Property8_RedactedOutputCarriesNoRawSsn()
    {
        var gen =
            from ssn in SsnGen
            from surrounding in TextGen()
            select (ssn, surrounding);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var r = Redact($"{t.surrounding} {t.ssn}");
            return !r.RedactedMessage.Contains(t.ssn) && r.RedactedMessage.Contains(SsnPlaceholder);
        }).QuickCheckThrowOnFailure();
    }

    // --- Property 9: single embedded value stays safe + preserves remainder ---

    private static readonly string[] QuestionStems =
    [
        "what image formats are supported",
        "how do i upload my document",
        "what does pending mean",
        "why did my photo check fail",
        "how long does verification take",
    ];

    [Fact]
    public void Property9_RedactAndContinuePreservesQuestion()
    {
        var gen =
            from stem in Gen.Elements(QuestionStems)
            from ssn in SsnGen
            select (stem, ssn);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var r = Redact($"{t.stem}? my ssn is {t.ssn}");
            if (!r.Safe || r.FallbackMessage is not null) return false;
            if (r.RedactedMessage.Contains(t.ssn)) return false;
            return t.stem.Split(' ').All(word => r.RedactedMessage.Contains(word));
        }).QuickCheckThrowOnFailure();
    }

    // --- Property 16: unsafe content yields a messages.* fallback ---

    [Fact]
    public void Property16_BareSensitiveValueIsUnsafeWithMessagesFallback()
    {
        Prop.ForAll(Arb.From(SsnGen), ssn =>
        {
            var r = Redact(ssn);
            return !r.Safe
                && r.FallbackMessage is not null
                && (r.FallbackMessage == Messages.PiiRephrase || r.FallbackMessage == Messages.Fallback)
                && !r.RedactedMessage.Contains(ssn);
        }).QuickCheckThrowOnFailure();
    }
}
