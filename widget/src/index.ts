/**
 * Chat_Widget (browser) public surface.
 *
 * The accessible UI + controller (session-only history, Layer 1 PII redaction,
 * and the Phase 1 HTTPS Backend_API call with NO auth token) implemented by
 * tasks 11.1–11.3 (design.md -> Components -> Chat_Widget).
 */

import type { MessageRequest, MessageResponse } from "@vera/shared";

export type { MessageRequest, MessageResponse };

export {
  redactPii,
  PII_PLACEHOLDERS,
  ALL_PII_PLACEHOLDERS,
} from "./pii-redactor.js";
export type {
  SensitiveValueKind,
  WidgetRedactionResult,
} from "./pii-redactor.js";

export { createApiClient, ApiError } from "./api-client.js";
export type { ApiClient, ApiClientOptions } from "./api-client.js";

export {
  createSessionStore,
  generateSessionId,
} from "./session.js";
export type { SessionStore, Turn, Role } from "./session.js";

export {
  createWidgetController,
  mountWidget,
} from "./widget.js";
export type {
  WidgetController,
  WidgetControllerOptions,
  Notice,
  SubmitOutcome,
  MountOptions,
  MountedWidget,
} from "./widget.js";
