# Requirements Document

## Introduction

Vera is a lightweight virtual assistant (chatbot) widget embedded in the Identity Document Verification Application (IDVA) web application. IDVA enables users to upload and verify identity documents. Vera answers general questions about document uploads, verification steps, what verification statuses mean, common errors, and troubleshooting, using an approved FAQ knowledge base. Vera does not look up any individual User's personal application or verification status.

Vera is implemented as a JavaScript chat widget hosted in Amazon S3 and delivered through Amazon CloudFront. The widget calls a backend API exposed through Amazon API Gateway and implemented with AWS Lambda functions. The Backend_API Lambda is implemented in C# on .NET 10 (the AWS Lambda managed `dotnet10` runtime); the Chat_Widget and the AWS CDK infrastructure remain TypeScript. This is an implementation-language choice only and does not change any functional, security, privacy, or architectural requirement in this document. The backend answers questions that match entries in an approved JSON FAQ file directly from that content, and, for free-form or custom questions that do not directly match an FAQ entry, uses an Amazon Bedrock foundation model to interpret the question and compose an answer supported by the approved FAQ content. When the FAQ content does not support an answer, Vera returns a Fallback_Response rather than generating unsupported content.

Chat history exists only for the current client Session and is not persisted server-side. Security is proportionate to a lightweight serverless chatbot: data is protected in transit (HTTPS/TLS 1.2 or higher), and identity documents and raw PII are not stored. Vera applies defense-in-depth, redact-and-continue PII protection at two independent layers: the Chat_Widget redacts Sensitive_Value content before it appears in the visible chat history or is transmitted, and the Backend_API independently re-screens and redacts every incoming message before logging, FAQ processing, or any Amazon Bedrock request. After redaction, Vera continues to answer the remaining non-sensitive question from the approved FAQ content whenever possible, and returns a privacy-safe Fallback_Response when sensitive content cannot be safely removed. Across every phase, Vera performs no per-user application or verification data lookup.

Access to the Backend_API follows a two-phase authentication architecture. In Phase 1 (the current POC), IDVA has no authentication configured: Amazon Cognito is not yet in place, so Vera operates without an authentication token and the Backend_API endpoint is treated as publicly callable unless separately restricted. Phase 1 uses test data only (no real or production PII) and relies on compensating controls — API Gateway stage/route throttling (rate and burst limits), AWS Lambda reserved concurrency, Amazon CloudWatch monitoring and alarms, and AWS Budgets alerts. These compensating controls are not authentication and do not guarantee a hard spending cap. In Phase 2 (the future authenticated deployment), IDVA adds Amazon Cognito and Vera reuses IDVA's existing Cognito login and tokens — there is no second login and no separate Cognito user pool for Vera — and Amazon API Gateway validates the token before invoking Lambda.

Some areas depend on decisions still to be confirmed (the Phase 2 token-sharing mechanism, the Phase 2 API Gateway authorizer type, the relevance-matching approach and threshold, latency and availability targets, and support channel). Requirements covering those areas use explicit placeholder values marked `[CONFIRM: ...]` so they remain testable while flagging the open decision. Please review and replace each placeholder with the confirmed value.

## Glossary

- **Vera**: The virtual assistant subsystem embedded in the IDVA web application, comprising the Chat_Widget, Backend_API, Retrieval_Component, Generation_Component, and Grounding_Component.
- **IDVA**: Identity Document Verification Application; the host web application in which Vera is embedded.
- **Phase 1 (POC)**: The current proof-of-concept phase in which IDVA has no authentication configured (Amazon Cognito is not yet in place). Vera operates without an authentication token, uses test data only, and relies on Compensating_Controls rather than authentication.
- **Phase 2 (Authenticated Deployment)**: The future phase in which IDVA has implemented Amazon Cognito authentication and Vera reuses IDVA's existing Cognito login and tokens, with no second login and no separate Cognito user pool for Vera.
- **Compensating_Controls**: The Phase 1 protections applied in place of authentication: API Gateway stage/route throttling (rate and burst limits), AWS Lambda reserved concurrency, Amazon CloudWatch monitoring and alarms, and AWS Budgets alerts. These controls are not authentication and do not guarantee a hard spending cap.
- **Cognito**: Amazon Cognito, the identity service that IDVA will implement in Phase 2 to authenticate IDVA Users and issue the tokens Vera reuses.
- **Chat_Widget**: The JavaScript chat UI hosted in Amazon S3 and delivered via Amazon CloudFront, rendered within the IDVA web application, through which the User interacts with Vera.
- **Backend_API**: The backend exposed through Amazon API Gateway and implemented with AWS Lambda functions that receives user messages, coordinates retrieval and generation, applies grounding and fallback behavior, and returns responses.
- **FAQ_File**: The approved JSON file containing structured question-and-answer entries that Vera is authorized to answer from. Also referred to as the knowledge base.
- **FAQ_Entry**: A single structured question-and-answer item within the FAQ_File, carrying a stable identifier.
- **Retrieval_Component**: The component that searches the FAQ_File and returns relevant FAQ content for a given query.
- **Generation_Component**: The component that uses an Amazon Bedrock foundation model to interpret custom questions and compose a natural-language response supported by retrieved FAQ content.
- **Grounding_Component**: The component that restricts responses to the approved FAQ content by constraining the Generation_Component to answer only from the FAQ content returned by the Retrieval_Component, and returns a Fallback_Response when that content does not support an answer. It is a best-effort control and does not claim to detect all unsupported content.
- **Deployment_Process**: The development and deployment pipeline that validates a FAQ_File against its JSON schema at build and deploy time and deploys only schema-valid files; it is distinct from the runtime Backend_API.
- **User**: An IDVA end user interacting with Vera.
- **Conversation**: An ordered sequence of messages exchanged between a User and Vera within a single Session, held client-side for the current Session only.
- **Session**: A bounded period of interaction between a User and Vera.
- **PII**: Personally Identifiable Information, including identity document contents and associated personal data processed by IDVA.
- **Sensitive_Value**: A specific piece of PII that must be removed from a message before display, transmission, logging, FAQ processing, or generation. In scope at minimum: date of birth, driver's license number, and Social Security number.
- **PII_Redaction**: The process of detecting Sensitive_Value content and replacing each occurrence with a descriptive placeholder (for example, `[REDACTED_DOB]`, `[REDACTED_LICENSE_NUMBER]`, `[REDACTED_SSN]`) so that the original Sensitive_Value is not displayed, transmitted, logged, processed, or sent to Amazon Bedrock.
- **Fallback_Response**: A predefined response Vera returns when the FAQ content does not support an answer, when no relevant FAQ content is retrieved, when a Sensitive_Value cannot be safely removed, or when a dependency is unavailable or times out.

## Requirements

### Requirement 1: Embedded Chat Widget

**User Story:** As an IDVA user, I want a chat widget embedded in the application, so that I can ask questions without leaving my verification workflow.

#### Acceptance Criteria

1. WHILE a User is viewing an IDVA web application page, THE Chat_Widget SHALL display an accessible control to open the chat widget.
2. WHEN a User opens the Chat_Widget, THE Chat_Widget SHALL display the Conversation history for the current Session.
3. WHEN a User submits a message, THE Chat_Widget SHALL display the submitted message and a visible indicator that Vera is preparing a response.
4. WHEN the Backend_API returns a response, THE Chat_Widget SHALL display the response within the Conversation.
5. THE Chat_Widget SHALL conform to WCAG 2.1 Level AA success criteria for the chat widget interface.

### Requirement 2: Widget Delivery

**User Story:** As an operator, I want the widget delivered through managed AWS content delivery, so that it loads reliably and scales globally.

#### Acceptance Criteria

1. THE Chat_Widget static assets SHALL be stored in an Amazon S3 bucket.
2. WHEN a User loads the IDVA web application, THE Chat_Widget assets SHALL be delivered through Amazon CloudFront.
3. THE Chat_Widget assets SHALL be served over HTTPS using TLS 1.2 or higher.
4. WHEN the Chat_Widget sends a message, THE Chat_Widget SHALL call the Backend_API through Amazon API Gateway.
5. IF the Chat_Widget assets fail to load, THEN THE IDVA web application SHALL surface a load error or omit the chat widget without blocking the host page.

### Requirement 3: Access Context

**User Story:** As a security owner, I want Vera's access model to follow a phased authentication architecture, so that the current POC can run without authentication under compensating controls while the future deployment reuses IDVA's Cognito login.

#### Acceptance Criteria

1. WHILE IDVA is in the Phase 1 POC, THE Backend_API SHALL NOT require an API Gateway authorizer.
2. WHILE IDVA is in the Phase 1 POC, THE Backend_API SHALL process a request that does not include an authentication token.
3. WHILE IDVA is in the Phase 1 POC, THE Backend_API endpoint SHALL be treated as publicly callable unless separately restricted.
4. WHILE IDVA is in the Phase 1 POC, THE Backend_API SHALL process test data only and SHALL NOT process real or production PII.
5. WHILE IDVA is in the Phase 1 POC, THE Backend_API SHALL be protected by Compensating_Controls comprising API Gateway stage/route throttling with configured rate and burst limits, AWS Lambda reserved concurrency, Amazon CloudWatch monitoring and alarms, and AWS Budgets alerts.
6. WHILE IDVA is in the Phase 1 POC, THE Compensating_Controls SHALL be treated as non-authentication controls that do not guarantee a hard spending cap.
7. WHILE IDVA is in the Phase 1 POC, THE Backend_API SHALL treat Cross-Origin Resource Sharing (CORS) configuration as a browser control that does not prevent direct, non-browser calls to the Backend_API endpoint.
8. WHEN IDVA has implemented Phase 2 Cognito authentication, THE IDVA application SHALL authenticate Users through Amazon Cognito.
9. WHEN IDVA has implemented Phase 2 Cognito authentication, THE Backend_API SHALL accept the Cognito token issued by IDVA's existing login without requiring a second login and without a separate Cognito user pool for Vera. `[CONFIRM: Phase 2 token-sharing mechanism — how IDVA provides the Cognito token to the embedded Vera widget]`
10. WHEN IDVA has implemented Phase 2 Cognito authentication, THE API Gateway SHALL validate the Cognito token before invoking the Lambda function. `[CONFIRM: Phase 2 API Gateway authorizer type — native Cognito authorizer vs. Lambda/JWT authorizer]`
11. WHILE IDVA is operating under Phase 2 Cognito authentication, IF a request to the Backend_API has a missing or invalid Cognito token, THEN THE Backend_API SHALL reject the request without processing the message.
12. THE Backend_API SHALL answer general FAQ questions without retrieving any individual User's personal application or verification data, in both Phase 1 and Phase 2.

### Requirement 4: Answer Predefined Questions from the FAQ File

**User Story:** As an IDVA user, I want direct answers to common questions, so that I get fast, reliable help.

#### Acceptance Criteria

1. WHEN a User submits a question that matches a predefined FAQ_Entry, THE Backend_API SHALL return the corresponding FAQ_Entry answer.
2. WHEN a User asks a question about document uploads, THE Backend_API SHALL return a response grounded in the FAQ_File.
3. WHEN a User asks a question about verification steps, THE Backend_API SHALL return a response grounded in the FAQ_File.
4. WHEN a User asks a question about what a verification status means, THE Backend_API SHALL return a general explanation grounded in the FAQ_File without retrieving any individual User's status.
5. WHEN a User asks a question about common errors or troubleshooting, THE Backend_API SHALL return a response grounded in the FAQ_File.

### Requirement 5: Generative Answers for Custom Questions

**User Story:** As an IDVA user, I want Vera to understand questions phrased in my own words, so that I am not limited to exact predefined wording.

#### Acceptance Criteria

1. WHEN a User submits a free-form question that does not directly match a predefined FAQ_Entry, THE Generation_Component SHALL use an Amazon Bedrock foundation model to interpret the question.
2. WHEN the Generation_Component composes a response, THE Generation_Component SHALL base the response only on FAQ content returned by the Retrieval_Component.
3. WHEN the Generation_Component returns a response, THE Backend_API SHALL include a reference to the FAQ_Entry identifier and FAQ_File version used to compose the response.
4. IF a free-form question is not supported by FAQ content returned by the Retrieval_Component, THEN THE Backend_API SHALL return the Fallback_Response.

### Requirement 6: Practical Grounding and Fallback

**User Story:** As a compliance owner, I want Vera to answer only from approved FAQ content and to fall back when it cannot, so that users receive approved information and unsupported answers are avoided.

#### Acceptance Criteria

1. WHEN the Retrieval_Component deems no FAQ content relevant to the question, THE Backend_API SHALL return the Fallback_Response and SHALL NOT invoke the Generation_Component. `[CONFIRM: relevance-matching approach and threshold — to be decided in design]`
2. WHEN the Backend_API invokes the Generation_Component, THE Backend_API SHALL constrain the Amazon Bedrock prompt to the FAQ content returned by the Retrieval_Component and SHALL instruct the model to return a no-answer signal when that content does not support an answer.
3. IF the Generation_Component returns a no-answer signal, THEN THE Backend_API SHALL return the Fallback_Response.
4. WHEN Vera returns a Fallback_Response, THE Chat_Widget SHALL display guidance on how the User can obtain further assistance. `[CONFIRM: support channel]`

### Requirement 7: Two-Layer PII Redaction

**User Story:** As a security owner, I want Vera to redact Sensitive_Value content at two independent layers while still answering the remaining question, so that identity information is never displayed, transmitted, logged, processed, or sent to Amazon Bedrock even if one layer is bypassed.

#### Acceptance Criteria

1. WHEN a User message contains a date of birth, a driver's license number, or a Social Security number, THE Chat_Widget SHALL replace each such Sensitive_Value with a descriptive placeholder (`[REDACTED_DOB]`, `[REDACTED_LICENSE_NUMBER]`, or `[REDACTED_SSN]`) before the message is added to the visible Conversation history.
2. WHEN a User message contains a Sensitive_Value, THE Chat_Widget SHALL replace each Sensitive_Value with its descriptive placeholder before transmitting the message to the Backend_API.
3. WHEN the Backend_API receives a message, THE Backend_API SHALL independently screen the message for Sensitive_Value content and replace each detected Sensitive_Value with a descriptive placeholder before writing any log entry, before FAQ retrieval or processing, and before composing any Amazon Bedrock request.
4. IF a message received by the Backend_API contains an un-redacted Sensitive_Value, THEN THE Backend_API SHALL redact the Sensitive_Value server-side before any logging, FAQ retrieval or processing, or Amazon Bedrock request, regardless of whether the Chat_Widget claims to have redacted the message.
5. WHEN one or more Sensitive_Value occurrences are redacted from a message, THE Backend_API SHALL continue to attempt to answer the remaining non-sensitive question from the FAQ_File.
6. IF a Sensitive_Value cannot be safely removed from a message, THEN THE Backend_API SHALL return a privacy-safe Fallback_Response and SHALL NOT log, process, or transmit the unsafe content to Amazon Bedrock.
7. THE Backend_API SHALL ensure that descriptive placeholders, and not the original Sensitive_Value content, appear in operational log entries and in any Amazon Bedrock request.
8. THE Backend_API SHALL exclude raw identity document contents and raw PII from any stored data.

### Requirement 8: Session-Only Conversation Handling

**User Story:** As a compliance owner, I want chat history kept only for the current session, so that no conversational data is persisted server-side.

#### Acceptance Criteria

1. THE Chat_Widget SHALL retain Conversation history on the client for the current Session only.
2. THE Backend_API SHALL NOT persist Conversation history server-side.
3. THE Backend_API SHALL transmit Conversation data in transit using HTTPS with TLS 1.2 or higher.

### Requirement 9: FAQ File Validation and Deployment

**User Story:** As an operator, I want the approved FAQ file validated during the development and deployment pipeline, so that only a schema-valid FAQ file is ever deployed and the runtime Backend_API simply consumes an already-validated file.

#### Acceptance Criteria

1. WHEN a FAQ_File is submitted to the development and deployment pipeline, THE Deployment_Process SHALL validate the FAQ_File against a defined JSON schema at build and deploy time.
2. IF a submitted FAQ_File fails schema validation, THEN THE Deployment_Process SHALL reject the FAQ_File at deploy time and SHALL NOT deploy it, and the previously deployed FAQ_File SHALL remain in effect for the runtime Backend_API.
3. WHEN an updated FAQ_File passes schema validation and is deployed, THE Retrieval_Component SHALL use the deployed FAQ content for subsequent queries.
4. AT runtime, THE Backend_API SHALL consume the already-validated, deployed FAQ_File without performing schema validation.
5. THE FAQ_File SHALL carry a version identifier, and each FAQ_Entry SHALL carry a stable identifier, so that returned answers can be traced to a source.

### Requirement 10: Operational Logging

**User Story:** As an operator, I want lightweight operational logging, so that I can audit and troubleshoot the assistant without capturing PII.

#### Acceptance Criteria

1. WHEN the Backend_API processes a message, THE Backend_API SHALL record a log entry containing the Session identifier, timestamp, and FAQ_File version used.
2. WHEN a Fallback_Response is returned, THE Backend_API SHALL record the reason for the Fallback_Response in the log entry.
3. THE Backend_API SHALL exclude PII from log entries.

### Requirement 11: Performance

**User Story:** As a user, I want timely responses, so that Vera remains useful.

#### Acceptance Criteria

1. WHEN a User submits a message, THE Backend_API SHALL return a response within `[CONFIRM: latency target]`.
2. WHEN request volume changes, THE serverless Backend_API SHALL scale automatically through AWS Lambda without manual intervention.

### Requirement 12: Error Handling and Availability

**User Story:** As a user, I want Vera to degrade gracefully, so that errors do not block my verification workflow.

#### Acceptance Criteria

1. IF the Retrieval_Component is unavailable, THEN THE Backend_API SHALL return the Fallback_Response.
2. IF the Generation_Component is unavailable or does not respond within `[CONFIRM: generation timeout]`, THEN THE Backend_API SHALL return the Fallback_Response.
3. IF an unexpected error occurs during message processing, THEN THE Backend_API SHALL return the Fallback_Response and record the error in the operational log.
4. THE Backend_API SHALL maintain a monthly availability of at least `[CONFIRM: availability target]`.
