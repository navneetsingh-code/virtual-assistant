using Vera.Backend.Kb;
using Vera.Backend.Models;

namespace Vera.Backend.Photo;

/// <summary>Why a photo-issue resolution fell back.</summary>
public enum PhotoIssueFallbackReason { InvalidKey, UnresolvedReference }

/// <summary>The discriminated result of a photo-issue resolution.</summary>
public abstract record PhotoIssueResult
{
    /// <summary>A successfully resolved photo-issue answer.</summary>
    public sealed record Resolved(
        string IssueKey,
        string Answer,
        IReadOnlyList<Statement> Statements,
        string KbVersion) : PhotoIssueResult;

    /// <summary>A fallback result: invalid, dangling, or ineligible key.</summary>
    public sealed record Fallback(
        string Reply,
        PhotoIssueFallbackReason Reason,
        string KbVersion) : PhotoIssueResult;
}

/// <summary>
/// PhotoIssueResolver (ported from
/// backend-ts-reference/src/photo/photo-issue-resolver.ts) — deterministic
/// resolution of photo-issue keys to their approved remediation answers. No
/// Bedrock. Reference integrity / eligibility is delegated entirely to the
/// <see cref="ReferenceValidator"/>; keys outside focus/glare/lighting/size are
/// validated before resolution and deterministically fall back.
///
/// Requirements: 4.5.
/// </summary>
public sealed class PhotoIssueResolver
{
    /// <summary>The canonical photo-issue keys, exactly as keyed in kb.json.</summary>
    public static readonly IReadOnlyList<string> PhotoIssueKeys = ["focus", "glare", "lighting", "size"];

    private static readonly HashSet<string> KeySet = [.. PhotoIssueKeys];

    private readonly ReferenceValidator _validator;

    public PhotoIssueResolver(ReferenceValidator validator) => _validator = validator;

    public bool IsPhotoIssueKey(string value) => KeySet.Contains(value);

    public PhotoIssueResult Resolve(string rawKey)
    {
        var key = rawKey.Trim().ToLowerInvariant();

        // Validate the id up front: anything outside the canonical set is not a
        // photo-issue path and deterministically falls back.
        if (!IsPhotoIssueKey(key))
        {
            return new PhotoIssueResult.Fallback(
                _validator.FallbackMessage, PhotoIssueFallbackReason.InvalidKey, _validator.KbVersion);
        }

        // Delegate reference integrity + eligibility to the validator.
        var validated = _validator.ResolvePhotoIssue(key);
        if (validated is null)
        {
            return new PhotoIssueResult.Fallback(
                _validator.FallbackMessage, PhotoIssueFallbackReason.UnresolvedReference, _validator.KbVersion);
        }

        return new PhotoIssueResult.Resolved(
            key, validated.Issue.Answer, validated.Statements, _validator.KbVersion);
    }
}
