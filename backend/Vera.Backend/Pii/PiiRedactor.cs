using System.Text.RegularExpressions;
using Vera.Backend.Models;

namespace Vera.Backend.Pii;

/// <summary>The kinds of Sensitive_Value Layer 2 screens for.</summary>
public enum SensitiveValueKind { Dob, License, Ssn }

/// <summary>
/// The outcome of server-side screening. The pipeline uses <see cref="Safe"/>
/// to decide whether to continue (redact-and-continue) or return the
/// privacy-safe fallback, and uses <see cref="RedactedMessage"/> wherever
/// message content would otherwise reach a sink.
/// </summary>
public sealed record RedactionResult
{
    /// <summary>The message with every detected Sensitive_Value replaced by its
    /// placeholder. ALWAYS the redacted form — never the raw input.</summary>
    public required string RedactedMessage { get; init; }

    /// <summary>Distinct kinds detected and replaced, in first-seen order.</summary>
    public required IReadOnlyList<SensitiveValueKind> RedactedKinds { get; init; }

    /// <summary>Total occurrences replaced across all kinds.</summary>
    public required int RedactionCount { get; init; }

    /// <summary>Whether the message is safe to continue serving.</summary>
    public required bool Safe { get; init; }

    /// <summary>When <see cref="Safe"/> is false, the privacy-safe fallback text
    /// sourced from messages.*; null otherwise.</summary>
    public string? FallbackMessage { get; init; }
}

/// <summary>
/// Layer 2 (server-side) PII redaction — the PiiRedactor. Ported from
/// backend-ts-reference/src/pii/pii-redactor.ts.
///
/// NEVER trusts the client: independently re-screens every incoming message for
/// DOB / driver's license / SSN and replaces each detected occurrence with a
/// descriptive placeholder BEFORE the pipeline writes any log entry, performs
/// retrieval, or composes a Bedrock request (Properties 7, 8, 9, 16).
///
/// Pure and side-effect free: it decides WHAT the redacted text and safety
/// verdict are; it does not log, call Bedrock, or persist anything.
///
/// Requirements: 7.3, 7.4, 7.5, 7.6, 7.7, 7.8.
/// </summary>
public sealed class PiiRedactor
{
    /// <summary>Placeholders substituted for each detected Sensitive_Value. These
    /// EXACT tokens are shared with Layer 1 (the widget).</summary>
    public static readonly IReadOnlyDictionary<SensitiveValueKind, string> Placeholders =
        new Dictionary<SensitiveValueKind, string>
        {
            [SensitiveValueKind.Dob] = "[REDACTED_DOB]",
            [SensitiveValueKind.License] = "[REDACTED_LICENSE_NUMBER]",
            [SensitiveValueKind.Ssn] = "[REDACTED_SSN]",
        };

    /// <summary>All placeholder tokens, for recognition of prior redaction.</summary>
    public static readonly IReadOnlyList<string> AllPlaceholders = [.. Placeholders.Values];

    private readonly int _maxOccurrences;
    private readonly double _placeholderShareUnsafe;

    /// <param name="maxOccurrences">If strictly more than this many occurrences are
    /// detected, the message is treated as unsafe. Default 3.</param>
    /// <param name="placeholderShareUnsafe">If the placeholder share of the redacted
    /// message is at least this fraction, the message is "mostly personal detail"
    /// and treated as unsafe. Default 0.5.</param>
    public PiiRedactor(int maxOccurrences = 3, double placeholderShareUnsafe = 0.5)
    {
        _maxOccurrences = maxOccurrences;
        _placeholderShareUnsafe = placeholderShareUnsafe;
    }

    private sealed record Detector(SensitiveValueKind Kind, Regex Pattern);

    // Order matters: SSN and DOB run before the generic license token so a dated
    // or SSN-shaped value is classified as the more specific kind first. Patterns
    // mirror the TypeScript reference exactly.
    private static readonly Detector[] Detectors =
    [
        new(SensitiveValueKind.Ssn, new Regex(
            @"\b(?:\d{3}[-\s]\d{2}[-\s]\d{4}|\d{9})\b",
            RegexOptions.Compiled)),
        new(SensitiveValueKind.Dob, new Regex(
            @"\b(?:dob|d\.o\.b\.?|date of birth|born(?: on)?|birth\s?date)\b[:\s]*(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{1,2}-\d{1,2}|[A-Za-z]+\.?\s+\d{1,2},?\s+\d{4})",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        new(SensitiveValueKind.Dob, new Regex(
            @"\b(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b",
            RegexOptions.Compiled)),
        new(SensitiveValueKind.License, new Regex(
            @"\b(?:driver'?s?\s?licen[sc]e|dl|licen[sc]e)\s?(?:number|no\.?|#)?[:\s]*[A-Za-z0-9]{5,15}\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        new(SensitiveValueKind.License, new Regex(
            @"\b(?=[A-Za-z0-9]{6,12}\b)(?=[A-Za-z0-9]*[A-Za-z])(?=[A-Za-z0-9]*\d)[A-Za-z0-9]+\b",
            RegexOptions.Compiled)),
    ];

    /// <summary>Screens and redacts a single incoming message. Pure and
    /// deterministic for a given input and <paramref name="messages"/> block.</summary>
    public RedactionResult Redact(string? message, KbMessages messages)
    {
        var input = message ?? "";
        var kindsSeen = new List<SensitiveValueKind>();
        var redactionCount = 0;
        var redacted = input;

        foreach (var (kind, pattern) in Detectors)
        {
            redacted = pattern.Replace(redacted, match =>
            {
                // A token ALREADY a placeholder is left exactly as-is: not raw PII,
                // not counted, not re-wrapped (lets the server re-screen harmlessly).
                if (AllPlaceholders.Contains(match.Value)) return match.Value;
                redactionCount++;
                if (!kindsSeen.Contains(kind)) kindsSeen.Add(kind);
                return Placeholders[kind];
            });
        }

        // Safety verdict (best-effort). Default is redact-and-continue; escalate to
        // the privacy-safe fallback only when confidence is low.
        var placeholderChars = kindsSeen.Sum(kind =>
            CountOccurrences(redacted, Placeholders[kind]) * Placeholders[kind].Length);
        var redactedLength = Math.Max(redacted.Trim().Length, 1);
        var placeholderShare = (double)placeholderChars / redactedLength;

        var mostlyPersonalDetail = redactionCount > 0 && placeholderShare >= _placeholderShareUnsafe;
        var tooManyValues = redactionCount > _maxOccurrences;
        var safe = !(mostlyPersonalDetail || tooManyValues);

        if (safe)
        {
            return new RedactionResult
            {
                RedactedMessage = redacted,
                RedactedKinds = kindsSeen,
                RedactionCount = redactionCount,
                Safe = true,
            };
        }

        // Unsafe: prefer piiRephrase when mostly personal detail, else generic fallback.
        var fallbackMessage = mostlyPersonalDetail ? messages.PiiRephrase : messages.Fallback;
        return new RedactionResult
        {
            RedactedMessage = redacted, // still redacted — never raw
            RedactedKinds = kindsSeen,
            RedactionCount = redactionCount,
            Safe = false,
            FallbackMessage = fallbackMessage,
        };
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index != -1)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }
}
