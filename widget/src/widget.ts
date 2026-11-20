/**
 * Chat_Widget UI + controller (tasks.md 11.1, 11.2, 11.3).
 *
 * Renders an accessible open control and the Conversation for the current
 * Session, shows the submitted message plus a "preparing a response" indicator,
 * renders the returned reply, and degrades without blocking the host IDVA page
 * on asset/transport failure (design.md -> Components -> Chat_Widget;
 * Requirements 1.x, 2.5, 8.1).
 *
 * The flow for each submission (design.md -> Request Flow nodes A/B/C):
 *   1. **Layer 1 PII redaction** (task 11.2): redact DOB/DL/SSN to placeholders
 *      BEFORE the message is added to visible history AND before transmission;
 *      surface `messages.piiNotice` / the relevant `messages.piiReminder.*` /
 *      `messages.maskingNotice` when redaction occurs (Req 7.1, 7.2).
 *   2. Show the (redacted) user turn + a preparing indicator (Req 1.3).
 *   3. **Call the Backend_API over HTTPS** with NO auth token (Phase 1) and the
 *      shared `{ sessionId, message }` contract (task 11.3).
 *   4. Render the `reply` / `isFallback`; on transport failure surface
 *      `messages.networkError` / `messages.busy` without blocking (Req 2.5, 8.3).
 *
 * Accessibility (Req 1.5, WCAG 2.1 AA target): the open control is a real
 * <button> with an accessible name; the conversation is an ARIA log
 * (`role="log"`, `aria-live="polite"`) so new turns are announced; the input
 * has an associated <label>; the preparing indicator is announced politely.
 * (Full WCAG conformance still requires manual testing with assistive
 * technologies and expert review — see tasks.md 11.5.)
 *
 * The controller logic (redact → session → API → render turns) is separated
 * from DOM wiring so it is unit/property-testable without a real browser.
 */

import type { KbMessages, MessageResponse } from "@vera/shared";
import { redactPii, type SensitiveValueKind } from "./pii-redactor.js";
import { createApiClient, ApiError, type ApiClient } from "./api-client.js";
import {
  createSessionStore,
  type SessionStore,
  type Turn,
} from "./session.js";

/** A notice surfaced to the user alongside a turn (sourced from `messages.*`). */
export interface Notice {
  kind: "pii" | "network" | "busy";
  text: string;
}

/** The outcome of handling one user submission (for the view to render). */
export interface SubmitOutcome {
  /** The redacted user turn that was added to history + transmitted. */
  userTurn: Turn;
  /** The assistant turn (reply or fallback), when the call succeeded. */
  assistantTurn?: Turn;
  /** Any notices to display (PII redaction, network/busy on failure). */
  notices: Notice[];
}

/** Configuration for the widget controller. */
export interface WidgetControllerOptions {
  messages: KbMessages;
  apiClient: ApiClient;
  session?: SessionStore;
}

/**
 * Map a redacted Sensitive_Value kind to the matching `messages.piiReminder.*`
 * copy (design.md -> Chat_Widget: surface the relevant reminder).
 */
function reminderFor(kind: SensitiveValueKind, messages: KbMessages): string {
  switch (kind) {
    case "dob":
      return messages.piiReminder.dob;
    case "license":
      return messages.piiReminder.dl;
    case "ssn":
      return messages.piiReminder.ssn;
    default:
      return messages.piiReminder.general;
  }
}

/**
 * The headless controller: all submission logic, no DOM. The DOM view
 * ({@link mountWidget}) calls {@link handleSubmit} and renders the outcome.
 */
export interface WidgetController {
  readonly session: SessionStore;
  /**
   * Handle a raw user input string. Performs Layer 1 redaction, records the
   * redacted user turn, calls the Backend_API, records the assistant turn, and
   * returns everything the view needs. Never throws: transport failures become
   * network/busy notices so the host page is never blocked.
   */
  handleSubmit(rawInput: string): Promise<SubmitOutcome>;
}

/** Build the headless widget controller. */
export function createWidgetController(
  options: WidgetControllerOptions,
): WidgetController {
  const { messages, apiClient } = options;
  const session = options.session ?? createSessionStore();

  return {
    session,

    async handleSubmit(rawInput: string): Promise<SubmitOutcome> {
      const notices: Notice[] = [];

      // 1) Layer 1 redaction BEFORE visible history + transmission (Req 7.1).
      const { redactedMessage, redactedKinds, redactionCount } =
        redactPii(rawInput);

      if (redactionCount > 0) {
        // Surface the PII notice + masking notice + per-kind reminders (Req 7.2).
        notices.push({ kind: "pii", text: messages.piiNotice });
        notices.push({ kind: "pii", text: messages.maskingNotice });
        for (const kind of redactedKinds) {
          notices.push({ kind: "pii", text: reminderFor(kind, messages) });
        }
      }

      const userTurn: Turn = { role: "user", text: redactedMessage };
      session.add(userTurn);

      // 2 + 3) Transmit the redacted message (Phase 1: no token).
      let response: MessageResponse | undefined;
      try {
        response = await apiClient.sendMessage({
          sessionId: session.sessionId,
          message: redactedMessage,
        });
      } catch (error) {
        // Transport/network failure → non-blocking notice (Req 2.5, 8.3).
        const text =
          error instanceof ApiError && error.message.includes("timed out")
            ? messages.busy
            : messages.networkError;
        notices.push({
          kind: text === messages.busy ? "busy" : "network",
          text,
        });
        return { userTurn, notices };
      }

      // 4) Record + return the assistant turn.
      const assistantTurn: Turn = {
        role: "assistant",
        text: response.reply,
        isFallback: response.isFallback,
        ...(response.sourceFaqId !== undefined
          ? { sourceFaqId: response.sourceFaqId }
          : {}),
      };
      session.add(assistantTurn);
      return { userTurn, assistantTurn, notices };
    },
  };
}

/** Options for mounting the DOM widget into a host page. */
export interface MountOptions {
  /** The HTTPS base URL of the Backend_API. */
  apiBaseUrl: string;
  /** Canonical `messages.*` copy (fetched/bundled with the widget). */
  messages: KbMessages;
  /** The host element to mount into (defaults to document.body). */
  host?: HTMLElement;
  /** Injected fetch for testing. */
  fetchFn?: typeof fetch;
}

/** A handle to an unmount function so the host can tear the widget down. */
export interface MountedWidget {
  unmount(): void;
  controller: WidgetController;
}

/**
 * Mount the accessible Chat_Widget into the DOM. Builds the open control, the
 * ARIA log conversation region, the labelled input, and the preparing
 * indicator, and wires submissions through {@link createWidgetController}.
 *
 * Asset/mount failure is caught and the widget is simply omitted rather than
 * throwing into the host page (Req 2.5): callers may ignore a null return.
 */
export function mountWidget(options: MountOptions): MountedWidget | null {
  try {
    const host = options.host ?? document.body;
    const apiClient = createApiClient({
      baseUrl: options.apiBaseUrl,
      fetchFn: options.fetchFn,
    });
    const controller = createWidgetController({
      messages: options.messages,
      apiClient,
    });

    const root = document.createElement("div");
    root.className = "vera-widget";

    const openButton = document.createElement("button");
    openButton.type = "button";
    openButton.textContent = "Ask Vera";
    openButton.setAttribute("aria-label", "Open the Vera assistant");
    openButton.setAttribute("aria-expanded", "false");

    const panel = document.createElement("section");
    panel.setAttribute("aria-label", "Vera assistant");
    panel.hidden = true;

    const log = document.createElement("div");
    log.setAttribute("role", "log");
    log.setAttribute("aria-live", "polite");
    log.setAttribute("aria-label", "Conversation");

    const status = document.createElement("div");
    status.setAttribute("role", "status");
    status.setAttribute("aria-live", "polite");

    const form = document.createElement("form");
    const label = document.createElement("label");
    const inputId = "vera-input";
    label.setAttribute("for", inputId);
    label.textContent = "Your message";
    const input = document.createElement("input");
    input.id = inputId;
    input.type = "text";
    input.autocomplete = "off";
    const send = document.createElement("button");
    send.type = "submit";
    send.textContent = "Send";

    form.append(label, input, send);
    panel.append(log, status, form);
    root.append(openButton, panel);
    host.append(root);

    // Render the welcome copy as the first assistant turn.
    appendTurn(log, { role: "assistant", text: options.messages.welcome });

    openButton.addEventListener("click", () => {
      const nowOpen = panel.hidden;
      panel.hidden = !nowOpen;
      openButton.setAttribute("aria-expanded", String(nowOpen));
      if (nowOpen) {
        input.focus();
      }
    });

    form.addEventListener("submit", (event) => {
      event.preventDefault();
      const raw = input.value;
      if (raw.trim().length === 0) {
        return;
      }
      input.value = "";
      status.textContent = options.messages.busy; // preparing indicator
      void controller.handleSubmit(raw).then((outcome) => {
        appendTurn(log, outcome.userTurn);
        for (const notice of outcome.notices) {
          appendNotice(log, notice.text);
        }
        if (outcome.assistantTurn) {
          appendTurn(log, outcome.assistantTurn);
        }
        status.textContent = "";
      });
    });

    return {
      controller,
      unmount(): void {
        root.remove();
      },
    };
  } catch {
    // Asset/mount failure must not block the host IDVA page (Req 2.5).
    return null;
  }
}

/** Append a conversation turn to the ARIA log region. */
function appendTurn(log: HTMLElement, turn: Turn): void {
  const el = document.createElement("p");
  el.className = `vera-turn vera-${turn.role}`;
  if (turn.isFallback) {
    el.dataset.fallback = "true";
  }
  el.textContent = turn.text;
  log.append(el);
}

/** Append a notice (e.g. PII redaction, network) to the log region. */
function appendNotice(log: HTMLElement, text: string): void {
  const el = document.createElement("p");
  el.className = "vera-notice";
  el.textContent = text;
  log.append(el);
}
