/**
 * Public surface of the shared workspace: the Backend_API request/response
 * contract and the knowledge-base (`kb.json`) types, consumed by the widget,
 * backend, and infra workspaces.
 */

export type { MessageRequest, MessageResponse } from "./contract.js";

export type {
  KbStatus,
  KbReview,
  Statement,
  Faq,
  ClarifyFlowOption,
  ClarifyFlow,
  PhotoIssue,
  PiiReminderMessages,
  SmallTalkMessages,
  DeclineMessages,
  KbMessages,
  KnowledgeBase,
} from "./kb.js";
