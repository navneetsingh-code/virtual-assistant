using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vera.Backend.Logging;

/// <summary>
/// The reason a Fallback_Response was returned (Req 10.2). Content-free
/// operational categories. Serialized as the exact kebab-case strings used by
/// the TypeScript reference so log consumers are unchanged.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<FallbackReason>))]
public enum FallbackReason
{
    [JsonStringEnumMemberName("no-match")] NoMatch,
    [JsonStringEnumMemberName("pii-unsafe")] PiiUnsafe,
    [JsonStringEnumMemberName("no-approved-content")] NoApprovedContent,
    [JsonStringEnumMemberName("model-no-answer")] ModelNoAnswer,
    [JsonStringEnumMemberName("retrieval-unavailable")] RetrievalUnavailable,
    [JsonStringEnumMemberName("generation-unavailable")] GenerationUnavailable,
    [JsonStringEnumMemberName("unexpected-error")] UnexpectedError,
}

/// <summary>Severity for an operational log entry.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
public enum LogLevel
{
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("error")] Error,
}

/// <summary>Content-free input the pipeline hands to the Logger. By design there is
/// NO field for the message body, raw request body, or any Sensitive_Value.</summary>
public sealed record LogInput
{
    public required string SessionId { get; init; }
    public required string KbVersion { get; init; }
    public FallbackReason? FallbackReason { get; init; }
    public LogLevel? Level { get; init; }
    public string? ErrorKind { get; init; }
}

/// <summary>A structured operational log entry — a closed shape with no field
/// capable of holding message content or a raw request body.</summary>
public sealed record LogEntry
{
    [JsonPropertyName("timestamp")] public required string Timestamp { get; init; }
    [JsonPropertyName("level")] public required LogLevel Level { get; init; }
    [JsonPropertyName("sessionId")] public required string SessionId { get; init; }
    [JsonPropertyName("kbVersion")] public required string KbVersion { get; init; }
    [JsonPropertyName("isFallback")] public required bool IsFallback { get; init; }

    [JsonPropertyName("fallbackReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FallbackReason? FallbackReason { get; init; }

    [JsonPropertyName("errorKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorKind { get; init; }
}

/// <summary>Where entries go. Injectable so tests can capture entries.</summary>
public interface ILogSink
{
    void Write(LogEntry entry);
}

/// <summary>The Logger surface consumed by the pipeline.</summary>
public interface ILogger
{
    /// <summary>Record one operational entry. Returns the exact entry written.
    /// Never throws for a malformed input: coerces to safe values.</summary>
    LogEntry Log(LogInput input);
}

/// <summary>A console-backed sink (captured by CloudWatch Logs in Lambda).</summary>
public sealed class ConsoleLogSink : ILogSink
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    public void Write(LogEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, JsonOptions);
        if (entry.Level == LogLevel.Error) Console.Error.WriteLine(line);
        else Console.WriteLine(line);
    }
}

/// <summary>An in-memory sink retaining every written entry (tests / inspection).</summary>
public sealed class InMemoryLogSink : ILogSink
{
    private readonly List<LogEntry> _entries = [];
    public IReadOnlyList<LogEntry> Entries => _entries;
    public void Write(LogEntry entry) => _entries.Add(entry);
}

/// <summary>
/// Operational logging — the Logger (ported from
/// backend-ts-reference/src/logging/logger.ts).
///
/// Records a small, fixed set of OPERATIONAL fields for every processed message
/// so the system is observable WITHOUT recording conversational content, PII,
/// or the raw request body (Properties 8, 13). "No raw body / no PII" is
/// guaranteed by construction: the input type has no place to put content.
///
/// Requirements: 10.1, 10.2, 10.3.
/// </summary>
public sealed class Logger : ILogger
{
    private const int MaxErrorKindLength = 120;

    private readonly ILogSink _sink;
    private readonly Func<DateTimeOffset> _clock;

    public Logger(ILogSink? sink = null, Func<DateTimeOffset>? clock = null)
    {
        _sink = sink ?? new ConsoleLogSink();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public LogEntry Log(LogInput input)
    {
        var fallbackReason = input.FallbackReason;
        var isFallback = fallbackReason is not null;
        var errorKind = SanitizeErrorKind(input.ErrorKind);

        var entry = new LogEntry
        {
            Timestamp = _clock().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            Level = input.Level ?? LogLevel.Info,
            SessionId = input.SessionId ?? "",
            KbVersion = input.KbVersion ?? "",
            IsFallback = isFallback,
            FallbackReason = fallbackReason,
            ErrorKind = errorKind,
        };

        _sink.Write(entry);
        return entry;
    }

    /// <summary>Coerce an arbitrary value to a safe, single-line, bounded string
    /// (defense-in-depth): collapse whitespace/newlines, then truncate.</summary>
    private static string? SanitizeErrorKind(string? value)
    {
        if (value is null) return null;
        var collapsed = System.Text.RegularExpressions.Regex.Replace(value, @"\s+", " ").Trim();
        if (collapsed.Length == 0) return null;
        return collapsed.Length > MaxErrorKindLength ? collapsed[..MaxErrorKindLength] : collapsed;
    }
}
