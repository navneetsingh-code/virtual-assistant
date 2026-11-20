/**
 * TypeScript types mirroring the approved knowledge base (`kb.json`) as-is.
 *
 * These types describe the file exactly as authored; `kb.json` is consumed
 * without modification. Eligibility filtering, reference pruning, and all
 * runtime guards are applied by the backend against these shapes (see
 * design.md -> Data Models). The types here add NO derived or computed fields.
 *
 * Top-level keys (design.md -> Research Summary / Data Models):
 *   kbVersion, lastUpdated, uiLabels, messages, clarifyFlows, photoIssues,
 *   statements, faqs.
 *
 * Requirements: 9.5 (version identifier + stable entry identifiers for
 * source traceability).
 */

/**
 * Lifecycle status of a knowledge-base entry. An entry is eligible to be
 * served only when `status === "active"` (combined with a confirmed review).
 * Draft/pending values are retained so the loader can exclude them.
 */
export type KbStatus = "active" | "draft" | "pending";

/**
 * Team review state of a knowledge-base entry. An entry is eligible only when
 * `review === "team-confirmed"`.
 */
export type KbReview = "team-confirmed" | "pending" | "unconfirmed";

/**
 * A grounding-corpus statement. Statements are referenced by FAQs, clarify-flow
 * inline answers, and photo issues via their `id`.
 *
 * Example: ST-LQC-01.
 */
export interface Statement {
  /** Stable identifier, e.g. "ST-LQC-01". */
  id: string;
  /** Topic grouping, e.g. "photo-checks". */
  topic: string;
  /** The approved statement text. */
  text: string;
  /** Lifecycle status; eligible only when "active". */
  status: KbStatus;
  /** Provenance of the statement, e.g. "ui-screenshot". */
  origin: string;
  /** Review state; eligible only when "team-confirmed". */
  review: KbReview;
  /** ISO-like last-updated date string, e.g. "2026-10-16". */
  updated: string;
}

/**
 * A matchable FAQ entry. Directly answerable only when `directAnswer` is a
 * non-null string (in addition to being eligible).
 *
 * Example: FAQ-UPL-02.
 */
export interface Faq {
  /** Stable identifier, e.g. "FAQ-UPL-02". */
  id: string;
  /** Topic grouping, e.g. "formats". */
  topic: string;
  /** Whether this FAQ is surfaced as a suggested question. */
  suggested: boolean;
  /** Question variants that should match this entry. */
  questions: string[];
  /** Statement ids this FAQ is grounded on. */
  statementIds: string[];
  /**
   * The approved direct answer, or null when this FAQ has no direct answer
   * (such entries are not directly answerable).
   */
  directAnswer: string | null;
  /** Lifecycle status; eligible only when "active". */
  status: KbStatus;
  /** Review state; eligible only when "team-confirmed". */
  review: KbReview;
  /** ISO-like last-updated date string. */
  updated: string;
}

/**
 * A single option within a clarify flow. Exactly one of the following target
 * fields is present per option: `next`, `faq`, `answer` (+ `statementIds`), or
 * `issue`. `freeText` options (e.g. in `upload.error`) accept free-form input.
 */
export interface ClarifyFlowOption {
  /** Option identifier, e.g. "glare". */
  id: string;
  /** User-facing label for the option. */
  label: string;
  /** Normalized keywords/synonyms this option matches against. */
  match: string[];
  /** Follow-on clarify-flow id, when the option branches to another flow. */
  next?: string;
  /** Target FAQ id, when the option resolves to a FAQ. */
  faq?: string;
  /** Inline approved answer text, when the option answers directly. */
  answer?: string;
  /** Statement ids grounding an inline `answer` option. */
  statementIds?: string[];
  /** Target `photoIssues` key, when the option resolves to a photo issue. */
  issue?: string;
  /** Whether this option accepts free-form text input. */
  freeText?: boolean;
}

/**
 * A deterministic clarify-flow decision node, keyed by flow id in
 * {@link KnowledgeBase.clarifyFlows} (e.g. "upload.checks").
 */
export interface ClarifyFlow {
  /** Prompt shown to the user for this flow. */
  prompt: string;
  /** Selectable options for this flow. */
  options: ClarifyFlowOption[];
}

/**
 * Per-issue remediation content, keyed by issue name in
 * {@link KnowledgeBase.photoIssues} (`focus` / `glare` / `lighting` / `size`).
 */
export interface PhotoIssue {
  /** The approved remediation answer text. */
  answer: string;
  /** Statement ids grounding this photo-issue answer. */
  statementIds: string[];
  /** Lifecycle status; eligible only when "active". */
  status: KbStatus;
  /** Provenance of the issue content, e.g. "team-decision". */
  origin: string;
  /** Review state; eligible only when "team-confirmed". */
  review: KbReview;
  /** ISO-like last-updated date string. */
  updated: string;
}

/**
 * Canned PII reminder copy, keyed by sensitive-value kind plus a general
 * variant. Sourced from `messages.piiReminder` in kb.json.
 */
export interface PiiReminderMessages {
  /** Reminder specific to driver's license numbers. */
  dl: string;
  /** Reminder specific to Social Security numbers. */
  ssn: string;
  /** Reminder specific to dates of birth. */
  dob: string;
  /** General PII reminder. */
  general: string;
}

/**
 * Canned small-talk copy. Sourced from `messages.smallTalk` in kb.json.
 */
export interface SmallTalkMessages {
  greeting: string;
  thanks: string;
  appreciation: string;
  acknowledgment: string;
  farewell: string;
}

/**
 * Canned decline copy for out-of-scope request kinds. Sourced from
 * `messages.decline` in kb.json.
 */
export interface DeclineMessages {
  eligibility_decision: string;
  legal_advice: string;
  unconfirmed_detail: string;
}

/**
 * Canonical user-facing copy. Vera sources these strings and does not invent
 * them (design.md -> Data Models -> messages).
 */
export interface KbMessages {
  welcome: string;
  piiNotice: string;
  maskingNotice: string;
  fallback: string;
  tryAgainLater: string;
  clarify: string;
  piiRephrase: string;
  busy: string;
  networkError: string;
  unresolved: string;
  piiUploadGuidance: string;
  decline: DeclineMessages;
  piiReminder: PiiReminderMessages;
  smallTalk: SmallTalkMessages;
  clarifyRetry: string;
}

/**
 * The full knowledge base as authored in `kb.json`, consumed as-is.
 */
export interface KnowledgeBase {
  /** Version identifier used wherever a KB version is referenced. */
  kbVersion: string;
  /** Last-updated date string for the whole KB. */
  lastUpdated: string;
  /** UI label strings. */
  uiLabels: string[];
  /** Canonical user-facing copy. */
  messages: KbMessages;
  /** Deterministic clarify-flow decision trees, keyed by flow id. */
  clarifyFlows: Record<string, ClarifyFlow>;
  /** Per-issue photo remediation, keyed by issue name. */
  photoIssues: Record<string, PhotoIssue>;
  /** Grounding-corpus statements. */
  statements: Statement[];
  /** Matchable FAQ entries. */
  faqs: Faq[];
}
