#!/usr/bin/env node
import { App } from "aws-cdk-lib";
import { VeraStack } from "./vera-stack.js";

/**
 * CDK app entry point (Phase 1 POC). Synthesizes the Vera stack. Deployment is
 * a separate, explicit step (`cdk deploy`) and is NOT performed automatically.
 *
 * Config is read from CDK context / environment so the same app synthesizes for
 * different accounts/regions without code changes.
 */
// Default the synth output dir to ./cdk.out when not launched via the CDK CLI
// (the CLI sets CDK_OUTDIR itself). This lets `node dist/app.js` synthesize the
// template locally for review without the CLI installed.
const app = new App({
  outdir: process.env.CDK_OUTDIR ?? "cdk.out",
});

/** Parse a comma-separated CDK context value into a trimmed string[] (or undefined). */
function csvContext(key: string): string[] | undefined {
  const raw = app.node.tryGetContext(key);
  if (typeof raw !== "string" || raw.trim().length === 0) return undefined;
  return raw
    .split(",")
    .map((s) => s.trim())
    .filter((s) => s.length > 0);
}

function numberContext(key: string): number | undefined {
  const raw = app.node.tryGetContext(key);
  const n = typeof raw === "string" ? Number(raw) : undefined;
  return Number.isFinite(n) ? n : undefined;
}

new VeraStack(app, "VeraPhase1Stack", {
  env: {
    account: process.env.CDK_DEFAULT_ACCOUNT,
    region: process.env.CDK_DEFAULT_REGION,
  },
  bedrockModelId: app.node.tryGetContext("bedrockModelId"),
  budgetNotifyEmail: app.node.tryGetContext("budgetNotifyEmail"),
  // CORS allow-list for the Backend_API — set to the IDVA origin(s):
  //   -c allowedOrigins=https://idva.example.gov
  allowedOrigins: csvContext("allowedOrigins"),
  // Optional explicit Bedrock ARNs; otherwise derived from the model id.
  bedrockModelArns: csvContext("bedrockModelArns"),
  // Optional runtime KB path override and budget amount.
  kbPath: app.node.tryGetContext("kbPath"),
  monthlyBudgetUsd: numberContext("monthlyBudgetUsd"),
});

app.synth();
