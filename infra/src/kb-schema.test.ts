import { describe, it, expect } from "vitest";
import { validateKnowledgeBase, validateKnowledgeBaseJson } from "./kb-schema.js";

/**
 * Deploy-time schema-gating tests (tasks.md 12.5).
 *
 * **Property 11: Schema validation gates deployment; invalid files do not
 * deploy.** For any candidate FAQ_File, schema validation is performed, and a
 * schema-invalid file is rejected (the deploy step keeps the prior file).
 * **Validates: Requirements 9.1, 9.2**
 */

function validKb(): unknown {
  return {
    kbVersion: "2026.10.1",
    lastUpdated: "2026-10-18",
    uiLabels: ["Ask Vera"],
    messages: {
      welcome: "Hi",
      fallback: "Sorry, contact DDS support.",
      piiRephrase: "Rephrase please.",
      networkError: "Network error.",
      busy: "Preparing.",
    },
    clarifyFlows: {},
    photoIssues: {
      glare: {
        answer: "Try again without glare.",
        statementIds: ["ST-LQC-01"],
        status: "active",
        origin: "team-decision",
        review: "team-confirmed",
        updated: "2026-10-18",
      },
    },
    statements: [
      {
        id: "ST-LQC-01",
        topic: "photo-checks",
        text: "Four checks run before submit.",
        status: "active",
        origin: "ui-screenshot",
        review: "team-confirmed",
        updated: "2026-10-16",
      },
    ],
    faqs: [
      {
        id: "FAQ-UPL-02",
        topic: "formats",
        suggested: true,
        questions: ["What image formats are supported?"],
        statementIds: ["ST-LQC-01"],
        directAnswer: "JPEG or PNG.",
        status: "active",
        review: "team-confirmed",
        updated: "2026-10-16",
      },
    ],
  };
}

describe("validateKnowledgeBase (Property 11)", () => {
  it("accepts a well-formed kb.json", () => {
    const result = validateKnowledgeBase(validKb());
    expect(result.valid).toBe(true);
    expect(result.errors).toEqual([]);
  });

  it("accepts a FAQ with directAnswer null", () => {
    const kb = validKb() as Record<string, unknown>;
    (kb.faqs as Record<string, unknown>[])[0].directAnswer = null;
    expect(validateKnowledgeBase(kb).valid).toBe(true);
  });

  it("rejects a missing top-level key", () => {
    const kb = validKb() as Record<string, unknown>;
    delete kb.statements;
    const result = validateKnowledgeBase(kb);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.includes("statements"))).toBe(true);
  });

  it("rejects an invalid status enum", () => {
    const kb = validKb() as Record<string, unknown>;
    (kb.faqs as Record<string, unknown>[])[0].status = "archived";
    const result = validateKnowledgeBase(kb);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.includes("status"))).toBe(true);
  });

  it("rejects a missing required message", () => {
    const kb = validKb() as Record<string, unknown>;
    delete (kb.messages as Record<string, unknown>).fallback;
    const result = validateKnowledgeBase(kb);
    expect(result.valid).toBe(false);
    expect(result.errors.some((e) => e.includes("messages.fallback"))).toBe(true);
  });

  it("rejects non-JSON input without throwing", () => {
    const result = validateKnowledgeBaseJson("{ not json");
    expect(result.valid).toBe(false);
    expect(result.errors[0]).toMatch(/not valid JSON/i);
  });

  it("round-trips a valid JSON string", () => {
    const result = validateKnowledgeBaseJson(JSON.stringify(validKb()));
    expect(result.valid).toBe(true);
  });
});
