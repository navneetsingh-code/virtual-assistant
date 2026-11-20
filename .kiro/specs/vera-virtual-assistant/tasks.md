# Implementation Plan: Vera Virtual Assistant

## Overview

This plan implements the Vera Virtual Assistant following the architecture in design.md. The **Backend_API Lambda is implemented in C# on .NET 10** (AWS Lambda managed `dotnet10` runtime); the **Chat_Widget is TypeScript** and the **infrastructure is TypeScript AWS CDK**. Work proceeds in incremental, test-driven steps: shared contracts and tooling first, then the approved-content foundation (knowledge-base loading, eligibility filtering, and reference integrity), followed by deterministic answer paths, server-side PII redaction, operational logging, grounded generation with fallback, the request pipeline, the browser widget, and Phase 1 infrastructure. Each step builds on the previous and ends by wiring into the pipeline so no code is left orphaned. Backend property-based tests use **xUnit + FsCheck**; the widget uses Vitest + fast-check — both with pragmatic iteration counts. Phase 2 Cognito authentication is deferred and excluded from the Phase 1 execution sequence.

**Architecture note (final).** The Backend_API is **C#/.NET 10 only** (`backend/Vera.Backend`, tested with xUnit + FsCheck). The Chat_Widget and the shared frontend contract are **TypeScript**; the infrastructure is **AWS CDK in TypeScript**. Node.js is retained solely for the frontend widget, the shared frontend contracts, the CDK project, and their development tooling. The backend was originally prototyped in TypeScript and then rewritten in C#; that transitional TypeScript backend has been **removed** (no `backend-ts-reference/`). The backend domain logic, thresholds, and the 14 correctness properties are implemented in C#; backend subtasks (2, 4, 5, 6, 8, 9) and checkpoints (3, 7, 10) reflect the C# implementation. Widget (11), infra (12), and integration/security (13) follow.

## Tasks

- [x] 1. Project structure, shared types, and tooling
  - Create `widget/`, `backend/`, `infra/`, and `shared/` workspaces
  - Set up TypeScript config, linting, and the test runner (Vitest) with fast-check (widget/shared/infra)
  - Define the shared request/response contract `{ sessionId, message }` → `{ reply, isFallback, sourceFaqId?, kbVersion }`
  - Define KB types mirroring kb.json as-is
  - _Requirements: 2.4, 5.3, 9.5_

- [x] 0. Backend in C#/.NET 10 (final architecture)
  - [x] 0.1 Verify .NET 10 SDK locally and the Lambda managed `dotnet10` runtime availability
    - Local SDK 10.0.4xx present; Lambda managed `dotnet10` runtime confirmed available (AL2023)
  - [x] 0.2 Remove the obsolete Node.js backend (no `backend-ts-reference/`); keep Node only for widget, shared contracts, CDK, and their tooling
    - Dropped `backend` from npm workspaces, root tsconfig references, and the regenerated lockfile
  - [x] 0.3 Create the C# solution: `backend/Vera.Backend` (class library) + `Vera.Backend.Tests` (xUnit + FsCheck)
    - Target `net10.0`; added `Amazon.Lambda.Core`, `Amazon.Lambda.Serialization.SystemTextJson`, `Amazon.Lambda.APIGatewayEvents`, `AWSSDK.BedrockRuntime`, `FsCheck.Xunit`
  - [x] 0.4 Shared contract + KB model as C# records/DTOs (wire-identical JSON so the TS widget is unaffected)
    - _Requirements: 2.4, 5.3, 9.5_
  - [x] 0.5 Backend domain modules in C# (kb/eligibility/reference, matcher, clarify, photo, pii, logging, retrieval, generation, pipeline, assembly)
  - [x] 0.6 Backend tests in C# (xUnit + FsCheck) — 61/61 passing (Properties 2,3,4,5,6,7,8,9,10,12,13,14)

- [x] 2. Knowledge-base loading, eligibility filtering, and reference integrity  _(C# ✓ — 61/61 C# tests)_
  - [x] 2.1 KnowledgeBaseProvider load-and-filter
    - Load once per cold start from a CONFIGURED location (never the absolute dev path)
    - Eligible = `status == active` AND `review == team-confirmed`; `directAnswer != null` required for direct answers
    - Add a runtime guard enforcing eligibility
    - _Requirements: 4.1, 6.1, 9.3, 9.4_

  - [x] 2.2 Unit tests for eligibility filtering
    - Assert excluded FAQ-UPL-06/07, FAQ-AIQ-04, FAQ-RES-01/02, FAQ-REF-01, ST-UPL-06, ST-RES-01/02/03 never surfaced
    - Assert eligible FAQ-UPL-02 / FAQ-LQC-01 available
    - _Requirements: 4.1, 6.1_

  - [x] 2.3 ReferenceValidator + reference-graph pruning
    - Validate all referenced ids across answer-producing paths
    - Prune dangling / draft / pending / unapproved references
    - Serve an inline clarify answer only if all `statementIds` are eligible
    - Empty path → `messages.fallback`
    - _Requirements: 4.1, 5.2, 6.1, 6.2, 9.5_

  - [x] 2.4 Property test reference integrity
    - **Property 6**
    - **Validates: Requirements 4.1, 5.2, 6.1, 6.2, 9.5**

  - [x]* 2.5 Unit test no per-user data access
    - **Property 2**
    - **Validates: Requirements 3.12, 4.4**

- [x] 3. Checkpoint - approved-content foundation  _(C# ✓ — all C# tests pass)_
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Deterministic answer paths (no Bedrock)  _(C# ✓)_
  - [x] 4.1 In-memory keyword/synonym Matcher + direct FAQ matching
    - Return `directAnswer` + FAQ id + `kbVersion`
    - _Requirements: 4.1, 4.2, 4.3, 4.5_

  - [ ]* 4.2 Property test direct FAQ matches
    - **Property 3**
    - **Validates: Requirements 4.1**

  - [x] 4.3 ClarifyFlowEngine
    - Navigate `next` / `faq` / `answer` + `statementIds` / `issue`; support `freeText`
    - Validate ids; no Bedrock
    - _Requirements: 4.2, 4.3, 4.5_

  - [x] 4.4 PhotoIssueResolver
    - Resolve focus / glare / lighting / size; validate ids; no Bedrock
    - _Requirements: 4.5_

  - [ ]* 4.5 Unit tests deterministic navigation
    - `upload.checks` incl. unsure → FAQ-LQC-03, glare lookup, id + `kbVersion`
    - _Requirements: 4.1, 4.5, 5.3, 9.5_

- [x] 5. Layer 2 PII redaction (Lambda, never trusts the client)  _(C# ✓)_
  - [x] 5.1 PiiRedactor
    - DOB / DL / SSN → placeholders; re-screen before any sink
    - Redact-and-continue; privacy-safe fallback when unsafe; best-effort
    - _Requirements: 7.3, 7.4, 7.5, 7.6, 7.7, 7.8_

  - [x] 5.2 Property test independent server-side re-screening order
    - **Property 7**
    - **Validates: Requirements 7.3, 7.4**

  - [x] 5.3 Property test no raw PII / no raw body at any sink
    - **Property 8**
    - **Validates: Requirements 7.7, 7.8, 10.3**

  - [x] 5.4 Property tests redact-and-continue + unsafe-content fallback
    - **Property 9** (**Validates: Requirements 7.5**)
    - **Property 16** (**Validates: Requirements 7.6**)

- [x] 6. Operational logging (PII-excluded, no raw body)  _(C# ✓)_
  - [x] 6.1 Logger
    - Log session id, timestamp, `kbVersion`, and fallback reason
    - No PII, no raw body
    - _Requirements: 10.1, 10.2, 10.3_

  - [x]* 6.2 Unit test log field presence + PII exclusion
    - **Property 13**
    - **Validates: Requirements 10.1, 10.2**

- [x] 7. Checkpoint - deterministic paths, redaction, and logging  _(C# ✓)_
  - Ensure all tests pass, ask the user if questions arise.

- [x] 8. Grounded generation and fallback (Bedrock, free-form only)  _(C# ✓ — injected IBedrockClient, no AWS call in tests)_
  - [x] 8.1 Retrieval_Component relevance gate
    - In-memory baseline; no relevant content → signal fallback, no generation
    - _Requirements: 6.1_

  - [x] 8.2 Generation_Component + Grounding_Component
    - Bedrock only on free-form; prompt only approved retrieved content + no-answer instruction
    - Fallback from `messages.*` (DDS support); include FAQ id + `kbVersion`
    - _Requirements: 5.1, 5.2, 5.3, 6.2, 6.3_

  - [x] 8.3 Property test grounded prompt construction
    - **Property 4**
    - **Validates: Requirements 5.2, 6.2**

  - [x] 8.4 Property test Bedrock-not-invoked-when-no-content
    - **Property 5**
    - **Validates: Requirements 6.1**

  - [x]* 8.5 Property test answer traceability
    - **Property 12**
    - **Validates: Requirements 5.3, 9.5**

- [x] 9. Backend request pipeline and graceful degradation  _(C# ✓ — pipeline + API Gateway Lambda handler + Bedrock Converse adapter; 68/68 C# tests)_
  - [x] 9.1 Wire Lambda handler pipeline
    - Layer 2 redaction → logging → routing (direct FAQ → clarify / photo-issue → retrieval + generation → fallback)
    - Stateless, no server-side persistence
    - _Requirements: 3.12, 8.2_

  - [x] 9.2 Graceful degradation + error handling
    - no-answer / retrieval-unavailable / generation-unavailable-or-timeout / unexpected-error → `messages.*` fallback
    - Log unexpected errors (no PII / no raw body)
    - _Requirements: 5.4, 6.3, 12.1, 12.2, 12.3_

  - [x] 9.3 Property test graceful degradation
    - **Property 14**
    - **Validates: Requirements 5.4, 6.3, 12.1, 12.2, 12.3**

  - [x]* 9.4 Unit test no server-side persistence
    - **Property 10**
    - **Validates: Requirements 8.2**

- [x] 10. Checkpoint - backend complete end-to-end  _(C# ✓ — 68/68 tests; domain + pipeline + Lambda handler + Bedrock adapter)_
  - Ensure all tests pass, ask the user if questions arise.

- [x] 11. Chat_Widget (browser, TypeScript)  _(18/18 TS tests pass)_
  - [x] 11.1 Widget UI, session-only history, response states
    - Accessible open control (WCAG 2.1 AA target); render Session only
    - Submitted + preparing indicator; asset-load failure non-blocking (`messages.networkError` / `busy`)
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 2.5, 8.1_

  - [x] 11.2 Layer 1 PII redaction in widget
    - DOB / DL / SSN placeholders before visible history + transmission
    - `piiNotice` / `piiReminder.*` / `maskingNotice` / `piiRephrase`; best-effort
    - _Requirements: 7.1, 7.2_

  - [x] 11.3 Backend_API call over HTTPS/TLS 1.2+
    - PHASE 1: NO token; POST `{ sessionId, message }`
    - Render `reply` / `isFallback` + DDS support guidance
    - _Requirements: 2.3, 2.4, 3.2, 3.3, 6.4, 8.3_

  - [x] 11.4 Property test Layer 1 redaction at the widget
    - **Property 15**
    - **Validates: Requirements 7.1, 7.2**

  - [ ]* 11.5 Automated accessibility scan  _(OPTIONAL; deferred — needs jsdom + axe-core harness)_
    - _Requirements: 1.5_

- [x] 12. Infrastructure as code - Phase 1 (POC)  _(AWS CDK TypeScript; 9 synth-assertion tests pass)_
  - [x] 12.1 Widget delivery infra
    - S3 + CloudFront over HTTPS/TLS 1.2+
    - _Requirements: 2.1, 2.2, 2.3_

  - [x] 12.2 Phase 1 API Gateway + Lambda
    - NO authorizer (public); stage/route throttling rate + burst
    - CORS as browser control; reserved concurrency; auto-scaling; .NET 10 Lambda
    - _Requirements: 3.1, 3.2, 3.3, 3.7, 11.2_

  - [x] 12.3 Phase 1 compensating controls monitoring + cost
    - CloudWatch metrics / alarms (errors, throttles, latency); AWS Budgets alert
    - Documented non-auth + no hard cap
    - _Requirements: 3.5, 3.6_

  - [x] 12.4 Build/deploy schema validation for kb.json
    - Validate at build/deploy; reject invalid keeping prior
    - Publish to configured runtime location (never absolute dev path)
    - _Requirements: 9.1, 9.2, 9.3, 9.4_

  - [x]* 12.5 Integration/unit test deploy-time schema gating
    - **Property 11**
    - **Validates: Requirements 9.1, 9.2**

- [ ] 13. Integration and security tests (Phase 1)
  - [ ]* 13.1 Integration tests delivery + wiring  _(BLOCKED on live AWS deploy — requires user approval to deploy)_
    - CloudFront + TLS, API GW → Lambda, deploy-then-query
    - _Requirements: 2.2, 2.3, 2.4, 9.3_

  - [ ] 13.2 Phase 1 compensating-control integration tests  _(BLOCKED on live AWS deploy — synth-asserted in 12; live run needs deploy approval)_
    - Endpoint responds with NO token (3.2/3.3); throttling beyond limit (3.5)
    - Reserved concurrency (3.5/11.2); CloudWatch alarms (3.5); Budgets (3.6)
    - Test data only (3.4); assert API keys NOT auth and CORS NOT authorization (3.6/3.7)
    - _Requirements: 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 11.2_

  - [x] 13.3 Security tests PII + reference integrity  _(C# ✓ — 7 security tests)_
    - Bypass client redaction → Layer 2 redacts, nothing raw to logs/Bedrock/storage
    - Dangling/unapproved id injection pruned with fallback
    - Prompt-elicitation → grounded or fallback
    - _Requirements: 7.4, 7.7, 7.8, 10.3, 5.2, 6.2, 9.5_

- [ ] 14. Checkpoint - all Phase 1 tests pass  _(all local tests pass: C# 75/75, TS 34/34; 13.1/13.2 live-AWS integration deferred pending deploy approval)_
  - Ensure all tests pass, ask the user if questions arise.

- [ ]* 15. (DEFERRED / FUTURE, Phase 2) Cognito authentication
  - DEFERRED: do NOT build now. Reuse IDVA's existing login/tokens, no separate pool, no second login.
  - [ ]* 15.1 Phase 2 API Gateway token validation (DEFERRED)
    - Validate IDVA-provided Cognito token before Lambda; reject missing/invalid without processing
    - `[CONFIRM: Phase 2 API Gateway authorizer type — native Cognito authorizer vs. Lambda/JWT authorizer]`
    - _Requirements: 3.8, 3.9, 3.10, 3.11_

  - [ ]* 15.2 Phase 2 widget token forwarding (DEFERRED)
    - Forward IDVA-provided token; `[CONFIRM: Phase 2 token-sharing mechanism]`
    - _Requirements: 3.9_

  - [ ]* 15.3 Phase 2 authorization tests (DEFERRED)
    - **Property 1: Phase 2 — Authorization is required before any message processing**
    - **Validates: Requirements 3.9, 3.10, 3.11**
    - Assert missing/invalid-token requests rejected with no redaction/logging/retrieval/Bedrock (Phase 2 only)

## Notes

- Critical Phase 1 tests (2.2, 2.4, 5.2, 5.3, 5.4, 8.3, 8.4, 9.3, 11.4, 13.2, 13.3) are MANDATORY and are NOT marked with `*`.
- Less-critical Phase 1 tests (2.5, 4.2, 4.5, 6.2, 8.5, 9.4, 11.5, 12.5, 13.1) remain OPTIONAL and are marked with `*`.
- ALL Phase 2 tasks (15, 15.1, 15.2, 15.3) remain OPTIONAL and DEFERRED and are EXCLUDED from the Phase 1 execution sequence / dependency graph.
- Non-test implementation tasks are mandatory.
- Property-based tests use fast-check with pragmatic iteration counts (no one-test-per-property or fixed-iteration mandate).
- kb.json is consumed as-is from a configured runtime location (never the absolute dev path).
- Checkpoints 3 / 7 / 10 / 14 are sequencing gates.

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1"] },
    { "id": 1, "tasks": ["2.1"] },
    { "id": 2, "tasks": ["2.2", "2.3", "2.5"] },
    { "id": 3, "tasks": ["2.4", "4.1", "4.3", "4.4", "5.1", "6.1"] },
    { "id": 4, "tasks": ["4.2", "4.5", "5.2", "5.3", "5.4", "6.2", "8.1"] },
    { "id": 5, "tasks": ["8.2"] },
    { "id": 6, "tasks": ["8.3", "8.4", "8.5", "9.1"] },
    { "id": 7, "tasks": ["9.2", "11.1", "12.1", "12.4"] },
    { "id": 8, "tasks": ["9.3", "9.4", "11.2", "11.3", "12.2", "12.3", "12.5"] },
    { "id": 9, "tasks": ["11.4", "11.5", "13.1", "13.2", "13.3"] }
  ]
}
```
