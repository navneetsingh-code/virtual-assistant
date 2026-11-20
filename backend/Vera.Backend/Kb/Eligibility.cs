using Vera.Backend.Models;

namespace Vera.Backend.Kb;

/// <summary>
/// Eligibility predicates — the hard invariant deciding which knowledge-base
/// entries may ever be served (design.md -> Data Models -> Eligibility). Ported
/// from backend-ts-reference/src/kb/eligibility.ts.
///
/// The rule, stated once and reused everywhere:
///   - An entry is ELIGIBLE only when <c>status == Active</c> AND
///     <c>review == TeamConfirmed</c>.
///   - A FAQ is DIRECTLY ANSWERABLE only when it is eligible AND
///     <c>DirectAnswer</c> is a non-null, non-empty string.
///
/// Draft/pending/unconfirmed and null-answer content is excluded. These
/// predicates are structural (no I/O, no state) so they back both the
/// load-time filter and the runtime guard (defense-in-depth).
///
/// Requirements: 4.1, 6.1, 9.3, 9.4.
/// </summary>
public static class Eligibility
{
    /// <summary>Single source of truth: active AND team-confirmed.</summary>
    public static bool IsEligible(KbStatus status, KbReview review)
        => status == KbStatus.Active && review == KbReview.TeamConfirmed;

    public static bool IsEligible(Statement statement)
        => IsEligible(statement.Status, statement.Review);

    public static bool IsEligible(Faq faq)
        => IsEligible(faq.Status, faq.Review);

    public static bool IsEligible(PhotoIssue photoIssue)
        => IsEligible(photoIssue.Status, photoIssue.Review);

    /// <summary>
    /// Whether a FAQ can be served as a DIRECT answer: eligible AND a non-null,
    /// non-empty <see cref="Faq.DirectAnswer"/>.
    /// </summary>
    public static bool IsDirectlyAnswerable(Faq faq)
        => IsEligible(faq) && !string.IsNullOrEmpty(faq.DirectAnswer);
}
