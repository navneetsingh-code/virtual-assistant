/**
 * Deploy-time knowledge-base schema validation (tasks.md 12.4).
 *
 * The Deployment_Process validates `kb.json` against its expected shape BEFORE
 * publishing it to the runtime location. A schema-invalid file is REJECTED at
 * deploy time and the previously deployed file remains in effect (design.md ->
 * Deployment_Process; Property 11). The runtime (C# backend) consumes the
 * already-validated file without re-running schema validation (Req 9.4).
 *
 * This is a lightweight structural validator (no external deps): it checks the
 * top-level keys and the shape of each collection element enough to guarantee
 * the backend's deserializer and eligibility logic can consume the file. It is
 * intentionally conservative — unknown extra fields are allowed (forward
 * compatible), but required fields and types are enforced.
 *
 * Requirements: 9.1 (validate at build/deploy), 9.2 (reject invalid; keep prior),
 * 9.3/9.4 (publish to configured runtime location; runtime trusts it).
 */

/** The outcome of a validation pass. */
export interface KbValidationResult {
  valid: boolean;
  /** Human-readable errors (empty when valid). Never includes KB content values. */
  errors: string[];
}

const STATUSES = new Set(["active", "draft", "pending"]);
const REVIEWS = new Set(["team-confirmed", "pending", "unconfirmed"]);

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === "object" && v !== null && !Array.isArray(v);
}

function isStringArray(v: unknown): boolean {
  return Array.isArray(v) && v.every((x) => typeof x === "string");
}

/**
 * Validate a parsed `kb.json` object structurally. Returns a result; callers
 * (the deploy step) reject the file when `valid` is false.
 */
export function validateKnowledgeBase(kb: unknown): KbValidationResult {
  const errors: string[] = [];

  if (!isObject(kb)) {
    return { valid: false, errors: ["kb.json root must be an object"] };
  }

  // Top-level required keys.
  if (typeof kb.kbVersion !== "string" || kb.kbVersion.length === 0) {
    errors.push("kbVersion must be a non-empty string");
  }
  if (typeof kb.lastUpdated !== "string") errors.push("lastUpdated must be a string");
  if (!isStringArray(kb.uiLabels)) errors.push("uiLabels must be a string[]");
  if (!isObject(kb.messages)) errors.push("messages must be an object");
  if (!isObject(kb.clarifyFlows)) errors.push("clarifyFlows must be an object");
  if (!isObject(kb.photoIssues)) errors.push("photoIssues must be an object");
  if (!Array.isArray(kb.statements)) errors.push("statements must be an array");
  if (!Array.isArray(kb.faqs)) errors.push("faqs must be an array");

  // messages: a minimal set of required canonical strings the backend relies on.
  if (isObject(kb.messages)) {
    for (const key of ["welcome", "fallback", "piiRephrase", "networkError", "busy"]) {
      if (typeof kb.messages[key] !== "string") {
        errors.push(`messages.${key} must be a string`);
      }
    }
  }

  // statements[]
  if (Array.isArray(kb.statements)) {
    kb.statements.forEach((s, i) => {
      if (!isObject(s)) {
        errors.push(`statements[${i}] must be an object`);
        return;
      }
      if (typeof s.id !== "string") errors.push(`statements[${i}].id must be a string`);
      if (typeof s.topic !== "string") errors.push(`statements[${i}].topic must be a string`);
      if (typeof s.text !== "string") errors.push(`statements[${i}].text must be a string`);
      if (typeof s.status !== "string" || !STATUSES.has(s.status)) {
        errors.push(`statements[${i}].status must be one of active|draft|pending`);
      }
      if (typeof s.review !== "string" || !REVIEWS.has(s.review)) {
        errors.push(`statements[${i}].review must be one of team-confirmed|pending|unconfirmed`);
      }
    });
  }

  // faqs[]
  if (Array.isArray(kb.faqs)) {
    kb.faqs.forEach((f, i) => {
      if (!isObject(f)) {
        errors.push(`faqs[${i}] must be an object`);
        return;
      }
      if (typeof f.id !== "string") errors.push(`faqs[${i}].id must be a string`);
      if (typeof f.topic !== "string") errors.push(`faqs[${i}].topic must be a string`);
      if (!isStringArray(f.questions)) errors.push(`faqs[${i}].questions must be a string[]`);
      if (!isStringArray(f.statementIds)) errors.push(`faqs[${i}].statementIds must be a string[]`);
      if (!(f.directAnswer === null || typeof f.directAnswer === "string")) {
        errors.push(`faqs[${i}].directAnswer must be a string or null`);
      }
      if (typeof f.status !== "string" || !STATUSES.has(f.status)) {
        errors.push(`faqs[${i}].status must be one of active|draft|pending`);
      }
      if (typeof f.review !== "string" || !REVIEWS.has(f.review)) {
        errors.push(`faqs[${i}].review must be one of team-confirmed|pending|unconfirmed`);
      }
    });
  }

  // photoIssues{} values
  if (isObject(kb.photoIssues)) {
    for (const [key, issue] of Object.entries(kb.photoIssues)) {
      if (!isObject(issue)) {
        errors.push(`photoIssues.${key} must be an object`);
        continue;
      }
      if (typeof issue.answer !== "string") errors.push(`photoIssues.${key}.answer must be a string`);
      if (!isStringArray(issue.statementIds)) {
        errors.push(`photoIssues.${key}.statementIds must be a string[]`);
      }
      if (typeof issue.status !== "string" || !STATUSES.has(issue.status as string)) {
        errors.push(`photoIssues.${key}.status must be one of active|draft|pending`);
      }
      if (typeof issue.review !== "string" || !REVIEWS.has(issue.review as string)) {
        errors.push(`photoIssues.${key}.review must be one of team-confirmed|pending|unconfirmed`);
      }
    }
  }

  return { valid: errors.length === 0, errors };
}

/**
 * Parse + validate a raw `kb.json` string. Returns the validation result; a
 * JSON parse error is itself a validation failure (invalid file → do not
 * deploy). Content values are never echoed in error messages.
 */
export function validateKnowledgeBaseJson(raw: string): KbValidationResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return { valid: false, errors: ["kb.json is not valid JSON"] };
  }
  return validateKnowledgeBase(parsed);
}
