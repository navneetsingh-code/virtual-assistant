# Vera

Vera is a virtual assistant (FAQ chatbot) designed to be embedded in an identity-document verification web application. It answers user questions about the document upload process, using only pre-approved FAQ content. When it cannot answer from approved content, it falls back to a safe, fixed response instead of guessing.

This repository contains the source code, specs, and infrastructure definition. It intentionally contains **no** real knowledge-base content, applicant data, credentials, account identifiers, or internal organizational information.

## Architecture

```
Browser widget (TypeScript)
   |  Layer-1 PII redaction in the browser
   v
CloudFront (HTTPS) --> S3 (private, static widget assets)
   |
   v
API Gateway (REST) --> Lambda (C# / .NET 10) --> Amazon Bedrock (free-form path only)
                          |
                          +-- approved kb.json (bundled at deploy time, not in this repo)
```

| Path | Description |
| --- | --- |
| `backend/Vera.Backend` | C# / .NET 10 Lambda backend (pipeline, matcher, clarify flow, retrieval, Bedrock client, PII redactor, logging) |
| `backend/Vera.Backend.Tests` | xUnit + FsCheck tests for the backend |
| `widget/` | TypeScript chat widget (API client, session, Layer-1 PII redactor) |
| `shared/` | TypeScript shared contract and knowledge-base schema types |
| `infra/` | AWS CDK (TypeScript) stack and deploy-time knowledge-base schema validator |
| `.kiro/specs/vera-virtual-assistant/` | Requirements, design, and task specs |
| `DEPLOYMENT.md` | Deployment notes (not required for local development or tests) |

`widget`, `shared`, and `infra` are npm workspaces defined in the root `package.json`. The backend is C# only; there is no TypeScript backend.

### Request pipeline

1. Layer-2 PII redaction on the incoming message (`PiiRedactor`).
2. Logging of redacted metadata only (no raw message body, no PII).
3. Routing: direct FAQ match, then clarify flow or photo-issue resolution, then retrieval gate.
4. Bedrock grounded generation, only on the free-form path.
5. Fixed fallback response if nothing approved applies.

### Bedrock grounding

Bedrock is called only for free-form questions, and only with approved, eligible knowledge-base statements retrieved for that question. The prompt instructs the model to return a `NO_ANSWER` marker when the supplied statements do not answer the question, which routes to the fallback. Bedrock is accessed through the `IBedrockClient` interface so it can be replaced in tests.

### Two-layer PII redaction

- **Layer 1 (widget):** the browser redacts likely date of birth, license number, and SSN patterns, replacing them with placeholders such as `[REDACTED_DOB]`, `[REDACTED_LICENSE_NUMBER]`, and `[REDACTED_SSN]` before sending.
- **Layer 2 (backend):** the Lambda redacts again on receipt, before any logging, routing, or model call.

### Infrastructure (Phase 1)

S3 (private, origin access control) and CloudFront (HTTPS, security headers); API Gateway REST API (public in Phase 1, throttled); Lambda with reserved concurrency and scoped Bedrock IAM permissions; CloudWatch alarms; an AWS Budgets alert; CORS controlled by an `allowedOrigins` setting. Phase 2 (authentication via Cognito) is designed but deferred.

## The knowledge base (`kb.json`) is external

The approved knowledge base is **not** part of this repository and must never be fabricated or committed. `kb.json` is git-ignored.

- Place the approved file at `backend/Vera.Backend/kb.json` before building a deployable package. The project file bundles it into the Lambda package as `/var/task/kb.json`, which is the default for `VERA_KB_PATH`.
- Schema references: `shared/src/kb.ts`, `backend/Vera.Backend/Models/KnowledgeBase.cs`, and the deploy-time validator `infra/src/kb-schema.ts`.
- Eligibility rule: an entry is used only if its status is `active` and its review state is `team-confirmed`. Direct-answer entries must have a non-null `directAnswer`.

Tests use in-code fixtures (`TestFixtures.cs` and test-local data), so the real `kb.json` is not needed to build or test.

## Local prerequisites

- .NET SDK 10 (developed with 10.0.402)
- Node.js and npm (developed with Node 24)

No AWS account, credentials, or network access to AWS is needed to build, test, or lint. CDK tests are synth assertions, and Bedrock is injected, so no live calls are made.

## Build

```powershell
# Backend (C#)
dotnet build backend/Vera.slnx

# TypeScript workspaces
npm install      # or: npm ci
npm run build    # tsc --build
```

## Test and lint

```powershell
# C# tests (xUnit + FsCheck)
dotnet test backend/Vera.slnx

# TypeScript tests (Vitest), including CDK synth assertions
npm test

# Lint and type-check
npm run lint
npm run typecheck
```

Windows note: run `npm test` from the canonical-case path (for example `D:\...`, not `d:\...`). A lowercase drive letter in the working directory can cause Vitest to report "No test suite found". `vitest.config.ts` pins `root` to mitigate this.

## Phases

- **Phase 1:** public API with throttling, concurrency limits, scoped IAM, and budget/alarm guardrails. Implemented and verified locally.
- **Phase 2:** authenticated access (Cognito) and related hardening. Designed, not implemented.

## Known gaps

- Live AWS integration tests (tasks 13.1 and 13.2) have not been run. Nothing in this repo has been deployed as part of its development.
- The widget has no bundler or `index.html`. `widget/dist` is plain `tsc` output, so hosting the widget needs a bundling step that is not included here.
- The optional accessibility scan (task 11.5) is deferred.
- No license has been chosen.

## Security and privacy

Do not commit `kb.json`, `.env` files, credentials, AWS account identifiers, real applicant data, or internal documents. Logs must never contain raw user messages or PII. See `.gitignore` for the protected patterns.
