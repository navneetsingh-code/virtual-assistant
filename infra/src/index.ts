/**
 * Infrastructure-as-code (Phase 1 POC) — AWS CDK (TypeScript) public surface.
 *
 * Exposes the Vera Phase 1 stack and the deploy-time kb.json schema validator.
 * The CDK app entry point is `app.ts` (invoked by `cdk.json`). Nothing is
 * deployed automatically; deployment is an explicit, separate step.
 */

export { VeraStack } from "./vera-stack.js";
export type { VeraStackProps } from "./vera-stack.js";
export {
  validateKnowledgeBase,
  validateKnowledgeBaseJson,
} from "./kb-schema.js";
export type { KbValidationResult } from "./kb-schema.js";
