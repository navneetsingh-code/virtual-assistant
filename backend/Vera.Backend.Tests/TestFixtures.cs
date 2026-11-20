using Vera.Backend.Models;

namespace Vera.Backend.Tests;

/// <summary>
/// Shared test fixtures: a small, representative knowledge base mirroring the
/// real kb.json shape (ported from backend-ts-reference/src/kb/test-fixtures.ts).
/// Includes a mix of eligible and ineligible entries so eligibility filtering,
/// reference pruning, and the runtime guard can be exercised.
/// </summary>
public static class TestFixtures
{
    public static readonly string[] ExcludedFaqIds =
        ["FAQ-UPL-06", "FAQ-UPL-07", "FAQ-AIQ-04", "FAQ-RES-01", "FAQ-RES-02", "FAQ-REF-01"];

    public static readonly string[] ExcludedStatementIds =
        ["ST-UPL-06", "ST-RES-01", "ST-RES-02", "ST-RES-03"];

    public static readonly string[] EligibleFaqIds = ["FAQ-UPL-02", "FAQ-LQC-01"];

    public static KbMessages Messages() => new()
    {
        Welcome = "Hi, I'm Vera.",
        PiiNotice = "I removed some personal details.",
        MaskingNotice = "Masked sensitive values.",
        Fallback = "I'm sorry, I don't have enough information to answer that. Please contact DDS support for further assistance.",
        TryAgainLater = "Please try again later.",
        Clarify = "Can you clarify?",
        PiiRephrase = "Please rephrase without personal details.",
        Busy = "I'm busy right now.",
        NetworkError = "A network error occurred.",
        Unresolved = "I couldn't resolve that.",
        PiiUploadGuidance = "Do not upload personal identifiers.",
        Decline = new DeclineMessages
        {
            EligibilityDecision = "I can't make eligibility decisions.",
            LegalAdvice = "I can't give legal advice.",
            UnconfirmedDetail = "I can't confirm that detail.",
        },
        PiiReminder = new PiiReminderMessages
        {
            Dl = "Avoid sharing your license number.",
            Ssn = "Avoid sharing your SSN.",
            Dob = "Avoid sharing your date of birth.",
            General = "Avoid sharing personal identifiers.",
        },
        SmallTalk = new SmallTalkMessages
        {
            Greeting = "Hello!",
            Thanks = "You're welcome.",
            Appreciation = "Happy to help.",
            Acknowledgment = "Got it.",
            Farewell = "Goodbye.",
        },
        ClarifyRetry = "Let's try that again.",
    };

    private static Statement St(string id, string topic, string text, KbStatus status, KbReview review) => new()
    {
        Id = id, Topic = topic, Text = text, Status = status, Origin = "ui-screenshot",
        Review = review, Updated = "2026-10-16",
    };

    public static KnowledgeBase MakeKnowledgeBase() => new()
    {
        KbVersion = "2026.10.1",
        LastUpdated = "2026-10-18",
        UiLabels = ["Ask Vera"],
        Messages = Messages(),
        ClarifyFlows = new Dictionary<string, ClarifyFlow>
        {
            ["upload.checks"] = new ClarifyFlow
            {
                Prompt = "Which check failed?",
                Options =
                [
                    new ClarifyFlowOption
                    {
                        Id = "glare", Label = "No glare on the card",
                        Match = ["glare", "reflection"], Issue = "glare",
                    },
                    new ClarifyFlowOption
                    {
                        Id = "unsure", Label = "I'm not sure",
                        Match = ["unsure", "not sure"], Faq = "FAQ-LQC-03",
                    },
                ],
            },
        },
        PhotoIssues = new Dictionary<string, PhotoIssue>
        {
            ["glare"] = new PhotoIssue
            {
                Answer = "If there's glare on the card, try taking another picture.",
                StatementIds = ["ST-LQC-01"], Status = KbStatus.Active, Origin = "team-decision",
                Review = KbReview.TeamConfirmed, Updated = "2026-10-18",
            },
            ["focus"] = new PhotoIssue
            {
                Answer = "Focus remediation (not yet approved).",
                StatementIds = ["ST-LQC-01"], Status = KbStatus.Pending, Origin = "team-decision",
                Review = KbReview.Pending, Updated = "2026-10-18",
            },
        },
        Statements =
        [
            St("ST-UPL-03", "formats", "IDVA accepts JPEG and PNG images.", KbStatus.Active, KbReview.TeamConfirmed),
            St("ST-LQC-01", "photo-checks", "Before anything is sent, IDVA runs four photo checks.", KbStatus.Active, KbReview.TeamConfirmed),
            St("ST-UPL-06", "formats", "Pending statement, not yet confirmed.", KbStatus.Pending, KbReview.Pending),
            St("ST-DRAFT-01", "misc", "Draft statement.", KbStatus.Draft, KbReview.TeamConfirmed),
            St("ST-UNCONF-01", "misc", "Active but review not confirmed.", KbStatus.Active, KbReview.Unconfirmed),
            St("ST-LQC-02", "photo-checks", "The checks confirm the photo is clear and readable.", KbStatus.Active, KbReview.TeamConfirmed),
            St("ST-PRV-01", "privacy", "Photos are handled according to the privacy policy.", KbStatus.Active, KbReview.TeamConfirmed),
            St("ST-RES-01", "results", "Excluded results statement (pending review).", KbStatus.Pending, KbReview.Pending),
            St("ST-RES-02", "results", "Excluded results statement (draft).", KbStatus.Draft, KbReview.TeamConfirmed),
            St("ST-RES-03", "results", "Excluded results statement (active but review unconfirmed).", KbStatus.Active, KbReview.Unconfirmed),
        ],
        Faqs =
        [
            new Faq
            {
                Id = "FAQ-UPL-02", Topic = "formats", Suggested = true,
                Questions = ["What image formats are supported?"], StatementIds = ["ST-UPL-03"],
                DirectAnswer = "Please upload a JPEG or PNG image.",
                Status = KbStatus.Active, Review = KbReview.TeamConfirmed, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-LQC-00", Topic = "photo-checks", Suggested = false,
                Questions = ["What are the photo checks?"], StatementIds = ["ST-LQC-01"],
                DirectAnswer = null, // eligible but NOT directly answerable
                Status = KbStatus.Active, Review = KbReview.TeamConfirmed, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-UPL-06", Topic = "formats", Suggested = false,
                Questions = ["Pending question?"], StatementIds = ["ST-UPL-06"],
                DirectAnswer = "Pending answer.",
                Status = KbStatus.Pending, Review = KbReview.Pending, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-REF-01", Topic = "misc", Suggested = false,
                Questions = ["Draft question?"], StatementIds = [],
                DirectAnswer = "Draft answer.",
                Status = KbStatus.Draft, Review = KbReview.TeamConfirmed, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-LQC-01", Topic = "photo-checks", Suggested = true,
                Questions = ["What do the photo checks confirm?"],
                StatementIds = ["ST-LQC-01", "ST-LQC-02", "ST-PRV-01"],
                DirectAnswer = "The checks confirm your photo is clear, readable, and ready to submit.",
                Status = KbStatus.Active, Review = KbReview.TeamConfirmed, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-UPL-07", Topic = "formats", Suggested = false,
                Questions = ["Excluded upload question?"], StatementIds = ["ST-UPL-06"],
                DirectAnswer = "Excluded (draft).",
                Status = KbStatus.Draft, Review = KbReview.TeamConfirmed, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-AIQ-04", Topic = "ai-questions", Suggested = false,
                Questions = ["Excluded AI question?"], StatementIds = [],
                DirectAnswer = "Excluded (pending review).",
                Status = KbStatus.Active, Review = KbReview.Pending, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-RES-01", Topic = "results", Suggested = false,
                Questions = ["Excluded results question 1?"], StatementIds = ["ST-RES-01"],
                DirectAnswer = "Excluded (pending).",
                Status = KbStatus.Pending, Review = KbReview.Pending, Updated = "2026-10-16",
            },
            new Faq
            {
                Id = "FAQ-RES-02", Topic = "results", Suggested = false,
                Questions = ["Excluded results question 2?"], StatementIds = ["ST-RES-02"],
                DirectAnswer = "Excluded (active but review unconfirmed).",
                Status = KbStatus.Active, Review = KbReview.Unconfirmed, Updated = "2026-10-16",
            },
        ],
    };
}
