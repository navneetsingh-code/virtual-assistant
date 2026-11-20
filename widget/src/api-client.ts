/**
 * Backend_API client (tasks.md 11.3).
 *
 * Calls the Backend_API through API Gateway over HTTPS/TLS 1.2+ (enforced by
 * the deployment: CloudFront/API Gateway only serve HTTPS — the client simply
 * uses the configured `https://` base URL). The widget POSTs the shared
 * `{ sessionId, message }` contract and renders the `{ reply, isFallback,
 * sourceFaqId?, kbVersion }` response.
 *
 * **Phase 1 (POC): NO authentication token is sent** — IDVA has no auth yet, so
 * the widget attaches no Authorization header (design.md -> Chat_Widget
 * interface; Phase 1). Phase 2 token forwarding is deferred (tasks.md 15.2).
 *
 * The message carried here is ALREADY Layer-1 redacted by the widget; the
 * server independently re-screens it (Layer 2). On transport/network failure
 * the client surfaces a typed error so the UI can show `messages.networkError`
 * / `messages.busy` without blocking (design.md -> Error Handling).
 *
 * Requirements: 2.3 (HTTPS call), 2.4 (widget → Backend_API), 3.2/3.3 (Phase 1
 * no token; no per-user data), 6.4 / 8.3 (render reply/fallback).
 */

import type { MessageRequest, MessageResponse } from "@vera/shared";

/** A transport/network/server failure, distinct from a normal fallback reply. */
export class ApiError extends Error {
  constructor(
    message: string,
    /** HTTP status when available (0 for network/transport failures). */
    readonly status: number,
  ) {
    super(message);
    this.name = "ApiError";
  }
}

/** Configuration for the API client. */
export interface ApiClientOptions {
  /**
   * The HTTPS base URL of the Backend_API (e.g.
   * `https://api.example.com/prod`). MUST be `https://` — the client refuses a
   * non-HTTPS base so redacted content is never sent in cleartext.
   */
  baseUrl: string;
  /**
   * Injected `fetch` (defaults to the global `fetch`). Injectable so tests can
   * provide a fake without a real network call.
   */
  fetchFn?: typeof fetch;
  /** Request timeout in milliseconds (default 10s). */
  timeoutMs?: number;
}

/** The widget's view of the Backend_API. */
export interface ApiClient {
  /**
   * Send a (Layer-1 redacted) message for the current session. Resolves with
   * the {@link MessageResponse}, or rejects with an {@link ApiError} on a
   * transport/HTTP failure (never with raw server internals).
   */
  sendMessage(request: MessageRequest): Promise<MessageResponse>;
}

const DEFAULT_TIMEOUT_MS = 10_000;

/** Create the Phase 1 Backend_API client. */
export function createApiClient(options: ApiClientOptions): ApiClient {
  const { baseUrl } = options;
  if (!/^https:\/\//i.test(baseUrl)) {
    throw new Error(
      "ApiClient: baseUrl must be an https:// URL (TLS 1.2+ required; " +
        "redacted content is never sent over cleartext).",
    );
  }
  const fetchFn = options.fetchFn ?? globalThis.fetch;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const endpoint = `${baseUrl.replace(/\/+$/, "")}/message`;

  return {
    async sendMessage(request: MessageRequest): Promise<MessageResponse> {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), timeoutMs);

      let response: Response;
      try {
        response = await fetchFn(endpoint, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          // Phase 1: NO Authorization header (IDVA has no auth yet).
          body: JSON.stringify({
            sessionId: request.sessionId,
            message: request.message,
          }),
          signal: controller.signal,
        });
      } catch (cause) {
        // Network failure or timeout (abort). Surface a typed, PII-free error.
        throw new ApiError(
          cause instanceof Error && cause.name === "AbortError"
            ? "Request timed out."
            : "Network error contacting the assistant.",
          0,
        );
      } finally {
        clearTimeout(timer);
      }

      if (!response.ok) {
        throw new ApiError(
          `Backend_API responded with status ${response.status}.`,
          response.status,
        );
      }

      let data: MessageResponse;
      try {
        data = (await response.json()) as MessageResponse;
      } catch {
        throw new ApiError("Malformed response from the assistant.", response.status);
      }

      if (typeof data?.reply !== "string" || typeof data?.isFallback !== "boolean") {
        throw new ApiError("Unexpected response shape from the assistant.", response.status);
      }
      return data;
    },
  };
}
