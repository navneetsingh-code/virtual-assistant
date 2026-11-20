import { describe, it, expect } from "vitest";
import { createWidgetController } from "./widget.js";
import { createSessionStore } from "./session.js";
import type { ApiClient } from "./api-client.js";
import { ApiError } from "./api-client.js";
import type { KbMessages, MessageRequest, MessageResponse } from "@vera/shared";
import { PII_PLACEHOLDERS } from "./pii-redactor.js";

/**
 * Tests for the headless widget controller (tasks.md 11.1 + 11.3): Layer-1
 * redaction before history/transmit, session-only history, response-state
 * rendering, and non-blocking transport-failure handling.
 */

const messages: KbMessages = {
  welcome: "Hi, I'm Vera.",
  piiNotice: "I removed some personal details.",
  maskingNotice: "Masked sensitive values.",
  fallback: "I'm sorry, I don't have enough information. Please contact DDS support.",
  tryAgainLater: "Please try again later.",
  clarify: "Can you clarify?",
  piiRephrase: "Please rephrase without personal details.",
  busy: "I'm preparing a response.",
  networkError: "A network error occurred.",
  unresolved: "I couldn't resolve that.",
  piiUploadGuidance: "Do not upload personal identifiers.",
  decline: { eligibility_decision: "", legal_advice: "", unconfirmed_detail: "" },
  piiReminder: { dl: "Avoid DL.", ssn: "Avoid SSN.", dob: "Avoid DOB.", general: "Avoid PII." },
  smallTalk: { greeting: "", thanks: "", appreciation: "", acknowledgment: "", farewell: "" },
  clarifyRetry: "",
};

function stubClient(response: MessageResponse, captured?: { req?: MessageRequest }): ApiClient {
  return {
    async sendMessage(req: MessageRequest): Promise<MessageResponse> {
      if (captured) captured.req = req;
      return response;
    },
  };
}

describe("WidgetController — submission flow", () => {
  it("redacts PII before adding to history and before transmission", async () => {
    const captured: { req?: MessageRequest } = {};
    const client = stubClient(
      { reply: "ok", isFallback: false, kbVersion: "2026.10.1" },
      captured,
    );
    const controller = createWidgetController({ messages, apiClient: client });

    const outcome = await controller.handleSubmit("what formats? my ssn is 123-45-6789");

    // Transmitted message is redacted.
    expect(captured.req?.message).toContain(PII_PLACEHOLDERS.ssn);
    expect(captured.req?.message).not.toContain("123-45-6789");
    // Visible user turn is redacted.
    expect(outcome.userTurn.text).toContain(PII_PLACEHOLDERS.ssn);
    expect(outcome.userTurn.text).not.toContain("123-45-6789");
    // PII notices surfaced.
    expect(outcome.notices.some((n) => n.text === messages.piiNotice)).toBe(true);
    expect(outcome.notices.some((n) => n.text === messages.piiReminder.ssn)).toBe(true);
  });

  it("records and returns the assistant reply", async () => {
    const client = stubClient({
      reply: "Please upload a JPEG or PNG image.",
      isFallback: false,
      sourceFaqId: "FAQ-UPL-02",
      kbVersion: "2026.10.1",
    });
    const controller = createWidgetController({ messages, apiClient: client });
    const outcome = await controller.handleSubmit("formats?");

    expect(outcome.assistantTurn?.text).toBe("Please upload a JPEG or PNG image.");
    expect(outcome.assistantTurn?.sourceFaqId).toBe("FAQ-UPL-02");
    expect(controller.session.getTurns()).toHaveLength(2);
  });

  it("surfaces a non-blocking network notice on transport failure", async () => {
    const client: ApiClient = {
      async sendMessage(): Promise<MessageResponse> {
        throw new ApiError("Network error contacting the assistant.", 0);
      },
    };
    const controller = createWidgetController({ messages, apiClient: client });
    const outcome = await controller.handleSubmit("hello");

    expect(outcome.assistantTurn).toBeUndefined();
    expect(outcome.notices.some((n) => n.kind === "network")).toBe(true);
    // The user turn is still recorded (session-only history).
    expect(controller.session.getTurns()).toHaveLength(1);
  });

  it("keeps history only in memory for the session (no persistence API)", () => {
    const store = createSessionStore("sess-xyz");
    store.add({ role: "user", text: "hi" });
    expect(store.sessionId).toBe("sess-xyz");
    expect(store.getTurns()).toHaveLength(1);
    // Returned turns are copies; mutating them does not affect the store.
    const turns = store.getTurns();
    turns[0].text = "mutated";
    expect(store.getTurns()[0].text).toBe("hi");
  });
});
