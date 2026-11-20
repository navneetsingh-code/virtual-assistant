# Vera — Deployment Guide (Phase 1 POC)

Final architecture:

- **Backend_API**: C# / .NET 10 AWS Lambda (`backend/Vera.Backend`) on the managed `dotnet10` runtime.
- **Chat_Widget**: TypeScript (`widget/`), compiled to browser JS, hosted on S3 + CloudFront.
- **Shared contract**: TypeScript (`shared/`) for the widget; mirrored as C# records in the backend. Identical wire JSON.
- **Infrastructure**: AWS CDK in TypeScript (`infra/`).
- **Tests**: backend xUnit + FsCheck (77/77 passing); frontend Vitest + fast-check (38/38 passing).

> **Phase 1 is a POC with no authentication.** The Backend_API endpoint is **public**, protected only by compensating controls (API Gateway throttling, Lambda reserved concurrency, CloudWatch alarms, AWS Budgets alert). These are **not** authentication and do **not** guarantee a hard spending cap. Use **test data only** — no real/production PII.

---

## 1. Prerequisites

Install / confirm before deploying:

- **.NET 10 SDK** (verified installed: 10.0.4xx).
- **Node.js** + npm (the repo uses npm workspaces; Vitest 3.2.7).
- **AWS CDK CLI**: `npm i -g aws-cdk` (the `aws-cdk-lib` construct library is already a project dependency; the CLI is separate and is **not** installed in this workspace).
- **AWS credentials** for the target account/region (`aws configure` or SSO).
- **Amazon Bedrock model access ENABLED** in the target region for the chosen model (Console → Bedrock → Model access). Without this, the free-form path returns the safe fallback for every request.
- **Region support** for the managed `dotnet10` Lambda runtime (announced Jan 2026; confirm in your target region).
- The **approved, schema-valid `kb.json`** (see §4). It is external approved content and is **not** in this repo.

---

## 2. Where the approved `kb.json` must be placed

**Place the final approved file at exactly this path before building the backend:**

```
backend/Vera.Backend/kb.json
```

(relative path: `backend/Vera.Backend/kb.json`)

Why there, and what happens next:

- The backend `.csproj` has a conditional `Content Include="kb.json"` item. When the file exists at that path, `dotnet publish` copies it into the Lambda bundle, so it lands at **`/var/task/kb.json`** inside the running Lambda.
- The Lambda's `VERA_KB_PATH` env var defaults to `/var/task/kb.json`, so the handler's cold-start `KnowledgeBaseProvider` loads it from there automatically.
- The file is **git-ignored / never committed** — it is dropped in at deploy time only.
- Do **not** place it at the developer authoring path (`D:\WWW\vera\knowledge\kb.json`); the provider explicitly rejects that path at runtime.
- **Validate it first** (see §4). Do not invent, edit, or substitute FAQ content — deploy the approved file exactly as provided.

If you prefer a different in-bundle location, set `-c kbPath=/var/task/<name>.json` at deploy and name the file accordingly in the publish output.

---

## 3. Build

```sh
# 1) Backend (C#): publish the .NET 10 Lambda bundle (includes kb.json if present)
dotnet publish backend/Vera.Backend/Vera.Backend.csproj -c Release
#    → backend/Vera.Backend/bin/Release/net10.0/publish   (this folder is the CDK Lambda asset)

# 2) Frontend + infra (TypeScript)
npm install
npm run build        # tsc --build across shared / widget / infra
```

The CDK Lambda asset path defaults to `backend/Vera.Backend/bin/Release/net10.0/publish`, so publish **before** `cdk synth`/`deploy`.

---

## 4. Validate the approved `kb.json` (deploy-time gate — Property 11)

Run this before packaging. A schema-invalid file must be **rejected**; the previously deployed file remains in effect.

```ts
import { readFileSync, copyFileSync } from "node:fs";
import { validateKnowledgeBaseJson } from "@vera/infra";

const approved = "<path to the approved kb.json>";
const result = validateKnowledgeBaseJson(readFileSync(approved, "utf8"));
if (!result.valid) {
  throw new Error("kb.json rejected (not deployed): " + result.errors.join("; "));
}
copyFileSync(approved, "backend/Vera.Backend/kb.json"); // then re-run dotnet publish
```

The validator checks the top-level keys, the required `messages.*` strings, and the shape/enum values of `statements`, `faqs`, and `photoIssues`. It never echoes KB content in its error messages.

---

## 5. Configuration (CDK context)

Set these via `-c key=value` on `cdk synth`/`cdk deploy` (or `cdk.context.json`):

| Context key | Required? | Purpose |
|---|---|---|
| `bedrockModelId` | **Yes** (confirm region form) | Bedrock model / inference-profile id for the free-form path. Many regions require a cross-region **inference profile** id, e.g. `us.anthropic.claude-3-5-haiku-20241022-v1:0`, not the bare foundation-model id. |
| `allowedOrigins` | **Strongly recommended** | CORS allow-list, comma-separated, e.g. `https://idva.example.gov`. If unset it defaults to `*` and CDK emits a **warning** at synth/deploy. Set this to the IDVA origin so the public endpoint is not browser-readable from arbitrary sites. |
| `budgetNotifyEmail` | Recommended | Email for the AWS Budgets threshold alert. |
| `monthlyBudgetUsd` | Optional | Budget amount (default `50`). |
| `bedrockModelArns` | Optional | Explicit Bedrock ARNs for IAM; otherwise derived from `bedrockModelId` (never `*`). |
| `kbPath` | Optional | Override the in-bundle KB path (default `/var/task/kb.json`). |

Lambda env vars the stack sets for you: `VERA_KB_PATH`, `VERA_BEDROCK_MODEL_ID`, `VERA_CORS_ALLOW_ORIGIN`.

---

## 6. Deploy (requires your explicit approval — not run automatically)

```sh
cd infra

# First time per account/region only:
cdk bootstrap aws://<ACCOUNT_ID>/<REGION>

# Review the exact change set (safe; no resources created):
cdk synth \
  -c bedrockModelId=us.anthropic.claude-3-5-haiku-20241022-v1:0 \
  -c allowedOrigins=https://idva.example.gov \
  -c budgetNotifyEmail=ops@example.gov

# Deploy (creates AWS resources — incurs charges):
cdk deploy \
  -c bedrockModelId=us.anthropic.claude-3-5-haiku-20241022-v1:0 \
  -c allowedOrigins=https://idva.example.gov \
  -c budgetNotifyEmail=ops@example.gov
```

After deploy, use the stack outputs:

- **`WidgetBucketName`** — upload the built widget assets here (e.g. `aws s3 sync widget/dist s3://<bucket>`).
- **`WidgetUrl`** — the CloudFront URL that serves the widget.
- **`ApiUrl`** — the public Backend_API base URL; set the widget's `apiBaseUrl` to this (`https://...`).

Confirm the AWS Budgets email subscription (AWS sends a confirmation email the first time).

---

## 7. Post-deploy verification (live integration — tasks 13.1 / 13.2)

These need the deployed stack:

- Widget loads over HTTPS from CloudFront (TLS 1.2+), assets 200.
- `POST {ApiUrl}/message` with `{ "sessionId": "...", "message": "What image formats are supported?" }` returns `200` with a grounded answer and the `Access-Control-Allow-Origin` header.
- A browser request from the IDVA origin can read the response (CORS); a different origin is blocked by the browser.
- Throttling: requests beyond the configured rate return `429`.
- CloudWatch alarms exist (errors / throttles / latency); Budgets alert configured.
- PII bypass: a message with raw PII is redacted server-side; logs and the response contain no raw PII.

---

## 8. Rollback procedure

CloudFormation (via CDK) makes rollback straightforward:

- **Failed deploy**: CloudFormation auto-rolls-back to the last good template; no manual action usually needed.
- **Roll back a bad change**: re-deploy the previous commit — `git checkout <prev> -- infra backend` (and re-publish), then `cdk deploy`. CloudFormation computes the reverse change set.
- **Bad `kb.json`**: the backend only loads the file baked into the Lambda bundle. To revert content, re-publish with the previous approved `kb.json` at `backend/Vera.Backend/kb.json` and `cdk deploy` (updates the Lambda code asset only). The schema gate (§4) prevents deploying an invalid file in the first place.
- **Stop traffic fast (no teardown)**: set Lambda reserved concurrency to `0` (Console or `-c reservedConcurrency=0` + `cdk deploy`) — every request then returns the throttle path; or disable the CloudFront distribution.
- **Full teardown**: `cdk destroy`. Note the **widget S3 bucket has `RemovalPolicy.RETAIN`** (and CloudWatch LogGroup `DESTROY`), so the bucket and its contents survive `cdk destroy` and must be emptied/deleted manually if you want them gone. This is intentional to avoid accidental asset loss.
- **Cost controls**: the Budgets alert only notifies. To cap spend during an incident, lower reserved concurrency and/or disable the distribution as above.

---

## 9. Estimated monthly cost (low-traffic POC)

Rough order-of-magnitude for a low-traffic POC (test traffic only). Actual costs depend on traffic and Bedrock token usage; verify with the AWS Pricing Calculator for your region.

| Service | Assumption | Est. monthly |
|---|---|---|
| Lambda (1024 MB, ~1–3 s incl. Bedrock) | a few thousand invocations | ~$0–1 (often within free tier) |
| API Gateway (REST) | a few thousand requests | ~$0–1 |
| Amazon Bedrock (Claude 3.5 Haiku, free-form path only) | only non-deterministic queries hit the model; grounded prompts are small | **dominant variable** — typically a few $ at low volume; scales with tokens |
| S3 + CloudFront | small static widget, low egress | ~$1–2 (CloudFront has a free tier) |
| CloudWatch (logs 1-month retention + 3 alarms) | low log volume | ~$1–3 |
| AWS Budgets | first 2 budgets free | ~$0 |
| **Total** | | **~$5–10/mo typical**, Bedrock-dependent |

Cost-control guardrails already in place: Lambda reserved concurrency (default 10) bounds fan-out; API Gateway throttling (rate 20/s, burst 40) bounds request rate; the Budgets alert notifies at 80% of the monthly threshold. None is a hard spending cap.

---

## 10. What is NOT done here

- **No AWS resources are deployed** by this repo; `cdk bootstrap`/`deploy` are explicit, approved steps you run.
- **Live integration tests** (13.1 / 13.2) require a deployed stack; the infra is covered by local CDK synth assertions until then.
- **Phase 2 Cognito authentication** is deferred by design.
- **Pre-production hardening to consider later**: pin the Bedrock IAM resource to the exact model ARN(s) you use (already scoped, not `*`); consider AWS WAF / usage-plan API keys as optional additional POC restrictions; add a custom domain + ACM cert to CloudFront/API Gateway if desired.
