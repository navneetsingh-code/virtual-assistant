/**
 * Shared request/response contract between the Chat_Widget and the Backend_API.
 *
 * The widget POSTs a {@link MessageRequest} to API Gateway over HTTPS/TLS 1.2+;
 * the backend returns a {@link MessageResponse}. The message carried in the
 * request is already Layer-1 (widget) redacted, and the backend independently
 * re-screens it (Layer 2) before any sink.
 *
 * Design: design.md -> Components and Interfaces -> Chat_Widget interface.
 * Requirements: 2.4 (widget calls Backend_API), 5.3 (traceability), 9.5 (version + id).
 */

/**
 * Request body sent by the Chat_Widget to the Backend_API.
 *
 * Phase 1 (POC) carries no authentication token. In Phase 2 the widget also
 * forwards an IDVA-provided Cognito token via the Authorization header (handled
 * at the transport layer, not in this body).
 */
export interface MessageRequest {
  /** Client-generated identifier for the current Session. */
  sessionId: string;
  /** The user's message, already Layer-1 redacted by the widget. */
  message: string;
}

/**
 * Response body returned by the Backend_API to the Chat_Widget.
 */
export interface MessageResponse {
  /** The user-facing reply text (an approved answer or a fallback message). */
  reply: string;
  /** True when {@link reply} is a Fallback_Response rather than a matched answer. */
  isFallback: boolean;
  /**
   * The source FAQ_Entry identifier when the answer is traceable to a FAQ
   * (direct or generated). Absent for fallbacks and non-FAQ paths.
   */
  sourceFaqId?: string;
  /** The knowledge-base version used to produce this response. */
  kbVersion: string;
}
