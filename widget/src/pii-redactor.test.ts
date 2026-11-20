import { describe, it, expect } from "vitest";
import fc from "fast-check";
import {
  redactPii,
  PII_PLACEHOLDERS,
  ALL_PII_PLACEHOLDERS,
} from "./pii-redactor.js";

/**
 * Tests for Layer 1 (widget) PII redaction (tasks.md 11.2 + 11.4).
 *
 * **Property 15 (tasks.md 11.4): Detected Sensitive_Value content is
 * placeholder-redacted at the widget.** For any message in which the widget's
 * detector identifies a DOB / driver's license / SSN, each detected occurrence
 * is replaced with its placeholder before the message is added to visible
 * history and before transmission. Best-effort detection.
 * **Validates: Requirements 7.1, 7.2**
 */

describe("widget redactPii — detection (Req 7.1)", () => {
  it("redacts a dashed SSN", () => {
    const r = redactPii("my ssn is 123-45-6789 please");
    expect(r.redactedMessage).toContain(PII_PLACEHOLDERS.ssn);
    expect(r.redactedMessage).not.toContain("123-45-6789");
    expect(r.redactedKinds).toContain("ssn");
  });

  it("redacts a labelled DOB", () => {
    const r = redactPii("DOB 01/15/1990");
    expect(r.redactedMessage).toContain(PII_PLACEHOLDERS.dob);
    expect(r.redactedMessage).not.toContain("01/15/1990");
  });

  it("redacts a labelled license number", () => {
    const r = redactPii("driver's license number D1234567");
    expect(r.redactedMessage).toContain(PII_PLACEHOLDERS.license);
    expect(r.redactedMessage).not.toContain("D1234567");
  });

  it("leaves a clean message unchanged", () => {
    const clean = "what image formats are supported";
    const r = redactPii(clean);
    expect(r.redactedMessage).toBe(clean);
    expect(r.redactionCount).toBe(0);
    expect(r.redactedKinds).toEqual([]);
  });

  it("is idempotent: existing placeholders are not re-wrapped or re-counted", () => {
    const r = redactPii(`already ${PII_PLACEHOLDERS.ssn}`);
    expect(r.redactionCount).toBe(0);
    expect(r.redactedMessage).toContain(PII_PLACEHOLDERS.ssn);
  });
});

describe("Property 15 — detected values are placeholder-redacted before use", () => {
  const ssnGen = fc
    .tuple(
      fc.integer({ min: 100, max: 999 }),
      fc.integer({ min: 10, max: 99 }),
      fc.integer({ min: 1000, max: 9999 }),
    )
    .map(([a, b, c]) => `${a}-${b}-${c}`);

  it("never leaves a detected raw SSN in the redacted output", () => {
    fc.assert(
      fc.property(ssnGen, fc.string(), (ssn, tail) => {
        const r = redactPii(`question ${ssn} ${tail}`);
        expect(r.redactedMessage).not.toContain(ssn);
        expect(
          ALL_PII_PLACEHOLDERS.some((p) => r.redactedMessage.includes(p)),
        ).toBe(true);
      }),
      { numRuns: 200 },
    );
  });

  it("re-running on its own output is a fixed point", () => {
    fc.assert(
      fc.property(ssnGen, (ssn) => {
        const once = redactPii(ssn);
        const twice = redactPii(once.redactedMessage);
        expect(twice.redactedMessage).toBe(once.redactedMessage);
        expect(twice.redactionCount).toBe(0);
      }),
      { numRuns: 200 },
    );
  });
});
