/**
 * Session-only Conversation history (tasks.md 11.1, Req 8.1).
 *
 * Conversation history is retained on the CLIENT for the current Session ONLY
 * and is NEVER persisted (design.md -> Components -> Chat_Widget; Data Models ->
 * Session / Conversation; Property 10 is the server-side counterpart). This
 * store holds turns in memory for the lifetime of the page/session and exposes
 * no persistence API — there is deliberately no localStorage/sessionStorage or
 * network write here. When the page is closed or reloaded the history is gone.
 *
 * Requirements: 8.1 (session-only client history; never persisted).
 */

/** Who authored a turn. */
export type Role = "user" | "assistant";

/** A single rendered conversation turn (already Layer-1 redacted for `user`). */
export interface Turn {
  role: Role;
  /** Display text. For user turns this is the Layer-1 redacted message. */
  text: string;
  /** True when an assistant turn is a Fallback_Response. */
  isFallback?: boolean;
  /** Source FAQ id for a traceable assistant answer, when present. */
  sourceFaqId?: string;
}

/** In-memory, session-scoped conversation store. No persistence. */
export interface SessionStore {
  /** A stable identifier for the current Session (client-generated). */
  readonly sessionId: string;
  /** Append a turn to the in-memory history. */
  add(turn: Turn): void;
  /** A copy of the current turns, oldest first. */
  getTurns(): Turn[];
  /** Clear the in-memory history (e.g. a new conversation). */
  clear(): void;
}

/** Generate a reasonably unique, non-PII session id for the current Session. */
export function generateSessionId(): string {
  const cryptoObj = globalThis.crypto;
  if (cryptoObj && typeof cryptoObj.randomUUID === "function") {
    return cryptoObj.randomUUID();
  }
  // Fallback for environments without crypto.randomUUID.
  return `sess-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * Create a session-only store. History lives solely in this closure's array;
 * nothing is written to storage or the network.
 *
 * @param sessionId - optional explicit id (defaults to a generated one).
 */
export function createSessionStore(sessionId: string = generateSessionId()): SessionStore {
  const turns: Turn[] = [];
  return {
    sessionId,
    add(turn: Turn): void {
      turns.push(turn);
    },
    getTurns(): Turn[] {
      return turns.map((t) => ({ ...t }));
    },
    clear(): void {
      turns.length = 0;
    },
  };
}
