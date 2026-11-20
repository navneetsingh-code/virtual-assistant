/**
 * Layer 1 (widget / client-side) PII redaction (tasks.md 11.2).
 *
 * This is the FIRST of Vera's two independent redaction layers (design.md ->
 * Components and Interfaces -> Chat_Widget; Correctness Property 15). Before a
 * user message is added to the visible conversation history AND before it is
 * transmitted to the Backend_API, the widget detects date-of-birth, driver's
 * license number, and Social Security number patterns and replaces each with a
 * descriptive placeholder:
 *
 *   DOB     -> [REDACTED_DOB]
 *   license -> [REDACTED_LICENSE_NUMBER]
 *   SSN     -> [REDACTED_SSN]
 *
 * These placeholder tokens are IDENTICAL to Layer 2 (the server) so that a
 * value the client already redacted is recognised server-side and never
 * re-wrapped or mistaken for raw PII (design.md -> Data Models -> PII
 * placeholders).
 *
 * This layer is **best-effort** (Property 15 note): pattern/heuristic detection
 * improves UX and reduces exposure, but it does NOT guarantee every possible
 * Sensitive_Value is caught. The server NEVER trusts this layer — it
 * independently re-screens every message (Layer 2, backend task 5.1). The two
 * layers are deliberately redundant.
 *
 * The detector mirrors the server's patterns so client and server agree on
 * what a Sensitive_Value looks like. It is pure and side-effect free.
 *
 * Requirements: 7.1 (detect + placeholder before display/transmit), 7.2
 * (surface the appropriate PII notices).
 */

/** The kinds of Sensitive_Value the widget screens for (matches the server). */
export type SensitiveValueKind = "dob" | "license" | "ssn";

/**
 * The descriptive placeholders substituted for each detected Sensitive_Value.
 * These EXACT tokens are shared with Layer 2 (the backend `PiiRedactor`).
 */
export const PII_PLACEHOLDERS: Record<SensitiveValueKind, string> = {
  dob: "[REDACTED_DOB]",
  license: "[REDACTED_LICENSE_NUMBER]",
  ssn: "[REDACTED_SSN]",
};

/** All placeholder tokens, for convenience / recognition of prior redaction. */
export const ALL_PII_PLACEHOLDERS: readonly string[] =
  Object.values(PII_PLACEHOLDERS);

/** The result of a Layer 1 redaction pass. */
export interface WidgetRedactionResult {
  /**
   * The message with every detected Sensitive_Value replaced by its
   * placeholder. This is what is shown in visible history AND transmitted.
   */
  redactedMessage: string;
  /** The distinct kinds detected and replaced, in first-seen order. */
  redactedKinds: SensitiveValueKind[];
  /** Total occurrences replaced across all kinds. */
  redactionCount: number;
}

/** A single detector: a kind plus a global, case-insensitive pattern. */
interface Detector {
  kind: SensitiveValueKind;
  pattern: RegExp;
}

/**
 * Build the detector set. Patterns mirror the server's Layer 2 detectors so the
 * two layers agree. Each pattern is global and rebuilt per call so `lastIndex`
 * never leaks between messages. SSN and DOB run before the generic license
 * token so a dated or SSN-shaped value is classified as the more specific kind.
 */
function buildDetectors(): Detector[] {
  return [
    // SSN: 9 digits as 3-2-4 (with - or space) or a bare 9-run, guarded.
    { kind: "ssn", pattern: /\b(?:\d{3}[-\s]\d{2}[-\s]\d{4}|\d{9})\b/g },
    // DOB: an explicitly labelled birth date (captures the date after the label).
    {
      kind: "dob",
      pattern:
        /\b(?:dob|d\.o\.b\.?|date of birth|born(?: on)?|birth\s?date)\b[:\s]*(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{1,2}-\d{1,2}|[A-Za-z]+\.?\s+\d{1,2},?\s+\d{4})/gi,
    },
    // DOB: a bare numeric date (MM/DD/YYYY, M-D-YY, YYYY-MM-DD).
    {
      kind: "dob",
      pattern:
        /\b(?:\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}[/-]\d{1,2}[/-]\d{1,2})\b/g,
    },
    // Driver's license: explicitly labelled license / DL number.
    {
      kind: "license",
      pattern:
        /\b(?:driver'?s?\s?licen[sc]e|dl|licen[sc]e)\s?(?:number|no\.?|#)?[:\s]*[A-Za-z0-9]{5,15}\b/gi,
    },
    // Driver's license: mixed alpha-numeric token (>=1 letter, >=1 digit), 6-12.
    {
      kind: "license",
      pattern:
        /\b(?=[A-Za-z0-9]{6,12}\b)(?=[A-Za-z0-9]*[A-Za-z])(?=[A-Za-z0-9]*\d)[A-Za-z0-9]+\b/g,
    },
  ];
}

/**
 * Detect and redact Sensitive_Value occurrences in `message`. Pure and
 * deterministic. A token that is ALREADY a placeholder is left untouched and
 * not re-counted (so re-running is idempotent).
 */
export function redactPii(message: string): WidgetRedactionResult {
  const input = typeof message === "string" ? message : "";
  const detectors = buildDetectors();
  const kindsSeen: SensitiveValueKind[] = [];
  let redactionCount = 0;
  let redacted = input;

  for (const { kind, pattern } of detectors) {
    redacted = redacted.replace(pattern, (match) => {
      if (ALL_PII_PLACEHOLDERS.includes(match)) {
        return match; // already redacted — leave as-is, do not re-count
      }
      redactionCount += 1;
      if (!kindsSeen.includes(kind)) {
        kindsSeen.push(kind);
      }
      return PII_PLACEHOLDERS[kind];
    });
  }

  return { redactedMessage: redacted, redactedKinds: kindsSeen, redactionCount };
}
