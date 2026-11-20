import { describe, it, expect } from "vitest";
import fc from "fast-check";
import type { MessageRequest, MessageResponse } from "./contract.js";

/**
 * Foundation smoke tests: confirm the shared contract types are structurally
 * usable and that the test runner + fast-check tooling is wired up. These are
 * type/shape checks only; behavioral logic is covered by later tasks.
 */
describe("MessageRequest / MessageResponse contract", () => {
  it("accepts a well-formed request shape", () => {
    const req: MessageRequest = { sessionId: "s-1", message: "hello" };
    expect(req.sessionId).toBe("s-1");
    expect(req.message).toBe("hello");
  });

  it("accepts a well-formed response shape with optional sourceFaqId", () => {
    const withFaq: MessageResponse = {
      reply: "Please upload a JPEG or PNG image.",
      isFallback: false,
      sourceFaqId: "FAQ-UPL-02",
      kbVersion: "2026.10.1",
    };
    const fallback: MessageResponse = {
      reply: "I'm sorry, I don't have enough information to answer that.",
      isFallback: true,
      kbVersion: "2026.10.1",
    };

    expect(withFaq.sourceFaqId).toBe("FAQ-UPL-02");
    expect(fallback.sourceFaqId).toBeUndefined();
    expect(fallback.isFallback).toBe(true);
  });

  it("round-trips arbitrary request values through JSON (fast-check wiring)", () => {
    fc.assert(
      fc.property(fc.string(), fc.string(), (sessionId, message) => {
        const req: MessageRequest = { sessionId, message };
        const parsed = JSON.parse(JSON.stringify(req)) as MessageRequest;
        expect(parsed.sessionId).toBe(sessionId);
        expect(parsed.message).toBe(message);
      }),
    );
  });
});
