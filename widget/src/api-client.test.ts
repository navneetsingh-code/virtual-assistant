import { describe, it, expect } from "vitest";
import { createApiClient, ApiError } from "./api-client.js";
import type { MessageResponse } from "@vera/shared";

/**
 * Tests for the Phase 1 Backend_API client (tasks.md 11.3).
 * Asserts: HTTPS is required, NO Authorization header is sent (Phase 1), the
 * shared contract body is posted, and transport failures surface as ApiError.
 */

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("ApiClient — Phase 1 behavior", () => {
  it("rejects a non-HTTPS base URL", () => {
    expect(() => createApiClient({ baseUrl: "http://example.com" })).toThrow(/https/i);
  });

  it("POSTs { sessionId, message } with NO Authorization header", async () => {
    let capturedUrl = "";
    let capturedInit: RequestInit | undefined;
    const fakeFetch: typeof fetch = async (url, init) => {
      capturedUrl = String(url);
      capturedInit = init;
      const body: MessageResponse = {
        reply: "Please upload a JPEG or PNG image.",
        isFallback: false,
        sourceFaqId: "FAQ-UPL-02",
        kbVersion: "2026.10.1",
      };
      return jsonResponse(body);
    };

    const client = createApiClient({
      baseUrl: "https://api.example.com/prod",
      fetchFn: fakeFetch,
    });
    const res = await client.sendMessage({ sessionId: "s1", message: "formats?" });

    expect(capturedUrl).toBe("https://api.example.com/prod/message");
    expect(capturedInit?.method).toBe("POST");
    const headers = capturedInit?.headers as Record<string, string>;
    // Phase 1: no auth token.
    expect(Object.keys(headers).map((k) => k.toLowerCase())).not.toContain("authorization");
    expect(headers["Content-Type"]).toBe("application/json");
    const sent = JSON.parse(String(capturedInit?.body));
    expect(sent).toEqual({ sessionId: "s1", message: "formats?" });
    expect(res.sourceFaqId).toBe("FAQ-UPL-02");
  });

  it("raises ApiError on a non-OK HTTP status", async () => {
    const fakeFetch: typeof fetch = async () => jsonResponse({}, 500);
    const client = createApiClient({ baseUrl: "https://api.example.com", fetchFn: fakeFetch });
    await expect(client.sendMessage({ sessionId: "s", message: "x" })).rejects.toBeInstanceOf(ApiError);
  });

  it("raises ApiError on a network failure", async () => {
    const fakeFetch: typeof fetch = async () => {
      throw new Error("ECONNREFUSED");
    };
    const client = createApiClient({ baseUrl: "https://api.example.com", fetchFn: fakeFetch });
    await expect(client.sendMessage({ sessionId: "s", message: "x" })).rejects.toBeInstanceOf(ApiError);
  });
});
