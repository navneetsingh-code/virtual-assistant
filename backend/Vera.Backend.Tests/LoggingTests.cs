using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using Vera.Backend.Logging;

namespace Vera.Backend.Tests;

/// <summary>
/// Operational Logger tests (ported from the TS logging suite). Covers Property
/// 13 (field presence) and the Property 8 companion (no content/raw body can
/// appear in an entry).
/// </summary>
public class LoggingTests
{
    private static readonly Func<DateTimeOffset> FixedClock =
        () => DateTimeOffset.Parse("2026-10-21T20:00:00.000Z");

    private static readonly FallbackReason[] AllReasons =
    [
        FallbackReason.NoMatch, FallbackReason.PiiUnsafe, FallbackReason.NoApprovedContent,
        FallbackReason.ModelNoAnswer, FallbackReason.RetrievalUnavailable,
        FallbackReason.GenerationUnavailable, FallbackReason.UnexpectedError,
    ];

    private static readonly HashSet<string> AllowedKeys =
        ["timestamp", "level", "sessionId", "kbVersion", "isFallback", "fallbackReason", "errorKind"];

    private static Gen<string> IdGen() =>
        Gen.Elements("", "s1", "sess-abc", "kb-2026", "2026.10.1");

    private static Gen<FallbackReason?> ReasonOrNull() =>
        Gen.Elements<FallbackReason?>(
            [null, .. AllReasons.Select(r => (FallbackReason?)r)]);

    [Fact]
    public void NormalEntryHasSessionTimestampKbVersion()
    {
        var sink = new InMemoryLogSink();
        var logger = new Logger(sink, FixedClock);
        var entry = logger.Log(new LogInput { SessionId = "sess-123", KbVersion = "kb-2026-10-16" });

        Assert.Equal("sess-123", entry.SessionId);
        Assert.Equal("kb-2026-10-16", entry.KbVersion);
        Assert.Equal("2026-10-21T20:00:00.000Z", entry.Timestamp);
        Assert.Equal(LogLevel.Info, entry.Level);
        Assert.False(entry.IsFallback);
        Assert.Null(entry.FallbackReason);
        Assert.Single(sink.Entries);
    }

    [Fact]
    public void FallbackEntryRecordsReason()
    {
        var sink = new InMemoryLogSink();
        var logger = new Logger(sink, FixedClock);
        var entry = logger.Log(new LogInput
        {
            SessionId = "sess-abc", KbVersion = "kb", FallbackReason = FallbackReason.NoMatch,
        });

        Assert.True(entry.IsFallback);
        Assert.Equal(FallbackReason.NoMatch, entry.FallbackReason);
    }

    [Fact]
    public void ErrorEntrySanitizesMultilineErrorKind()
    {
        var sink = new InMemoryLogSink();
        var logger = new Logger(sink, FixedClock);
        var entry = logger.Log(new LogInput
        {
            SessionId = "e", KbVersion = "kb", FallbackReason = FallbackReason.UnexpectedError,
            Level = LogLevel.Error, ErrorKind = "TypeError: boom\n    at handler\n    at run",
        });

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.NotNull(entry.ErrorKind);
        Assert.DoesNotContain("\n", entry.ErrorKind);
    }

    [Fact]
    public void Property13_EveryEntryHasRequiredFields()
    {
        var gen =
            from sessionId in IdGen()
            from kbVersion in IdGen()
            from reason in ReasonOrNull()
            select (sessionId, kbVersion, reason);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var sink = new InMemoryLogSink();
            var logger = new Logger(sink, FixedClock);
            var entry = logger.Log(new LogInput
            {
                SessionId = t.sessionId, KbVersion = t.kbVersion, FallbackReason = t.reason,
            });

            if (entry.Timestamp != "2026-10-21T20:00:00.000Z") return false;
            if (t.reason is null) return !entry.IsFallback && entry.FallbackReason is null;
            return entry.IsFallback && entry.FallbackReason == t.reason;
        }).QuickCheckThrowOnFailure();
    }

    [Fact]
    public void Property8_EntryKeysConfinedToOperationalSet()
    {
        var gen =
            from sessionId in IdGen()
            from kbVersion in IdGen()
            from errorKind in Gen.Elements("", "boom", "TypeError", "message body leak attempt")
            select (sessionId, kbVersion, errorKind);

        Prop.ForAll(Arb.From(gen), t =>
        {
            var sink = new InMemoryLogSink();
            var logger = new Logger(sink);
            logger.Log(new LogInput
            {
                SessionId = t.sessionId, KbVersion = t.kbVersion,
                FallbackReason = FallbackReason.UnexpectedError, Level = LogLevel.Error,
                ErrorKind = t.errorKind,
            });
            var entry = sink.Entries[^1];
            var json = JsonSerializer.Serialize(entry);
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!AllowedKeys.Contains(prop.Name)) return false;
            }
            var lower = json.ToLowerInvariant();
            return !lower.Contains("\"message\"") && !lower.Contains("\"body\"") && !lower.Contains("rawbody");
        }).QuickCheckThrowOnFailure();
    }
}
