using System.Text.Json;
using Vera.Backend.Models;

namespace Vera.Backend.Kb;

/// <summary>
/// KnowledgeBaseProvider — loads the approved `kb.json` once per Lambda cold
/// start from a CONFIGURED location, filters it to the eligible set, and
/// exposes only approved content through accessors guarded by a runtime
/// eligibility re-check (defense-in-depth). Ported from
/// backend-ts-reference/src/kb/knowledge-base-provider.ts.
///
///   - Load once per cold start from a CONFIGURED location (env var / explicit
///     path), never the absolute developer authoring path.
///   - Eligible = status == Active AND review == TeamConfirmed;
///     DirectAnswer != null additionally required for direct answers.
///   - A runtime guard re-enforces eligibility on every access.
///
/// The runtime consumes the already-validated, deployed file WITHOUT re-running
/// JSON-schema validation (Req 9.4); schema validation is a deploy-time concern
/// (Req 9.1, task 12.4).
///
/// Requirements: 4.1, 6.1, 9.3, 9.4.
/// </summary>
public interface IKnowledgeBaseProvider
{
    /// <summary>The knowledge-base version identifier (Req 9.5 traceability).</summary>
    string KbVersion { get; }

    /// <summary>Canonical user-facing copy (`messages.*`), consumed as-is.</summary>
    KbMessages Messages { get; }

    /// <summary>All eligible statements (active + team-confirmed).</summary>
    IReadOnlyList<Statement> GetEligibleStatements();

    /// <summary>All eligible FAQs (active + team-confirmed), regardless of directAnswer.</summary>
    IReadOnlyList<Faq> GetEligibleFaqs();

    /// <summary>All eligible FAQs that are additionally directly answerable.</summary>
    IReadOnlyList<Faq> GetDirectlyAnswerableFaqs();

    /// <summary>All eligible photo issues, keyed by issue name.</summary>
    IReadOnlyDictionary<string, PhotoIssue> GetEligiblePhotoIssues();

    /// <summary>An eligible statement by id, or null (runtime guard).</summary>
    Statement? GetStatementById(string id);

    /// <summary>An eligible FAQ by id, or null (runtime guard).</summary>
    Faq? GetFaqById(string id);

    /// <summary>An eligible photo issue by key, or null (runtime guard).</summary>
    PhotoIssue? GetPhotoIssueByKey(string key);
}

/// <summary>
/// Default <see cref="IKnowledgeBaseProvider"/> over an already-loaded KB. The
/// eligible sets are computed once in the constructor; each accessor applies
/// the runtime guard so a mutated/unexpected entry can never slip through.
/// </summary>
public sealed class KnowledgeBaseProvider : IKnowledgeBaseProvider
{
    /// <summary>
    /// The environment variable holding the runtime path to the approved,
    /// already-validated `kb.json`. The Deployment_Process publishes the file
    /// and sets this variable. The absolute developer authoring path is never
    /// read at runtime.
    /// </summary>
    public const string KbPathEnvVar = "VERA_KB_PATH";

    /// <summary>The developer-only authoring path from design.md. NEVER a runtime
    /// source; this constant exists solely to reject it defensively.</summary>
    private const string ForbiddenDevPath = @"D:\WWW\vera\knowledge\kb.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyList<Statement> _eligibleStatements;
    private readonly IReadOnlyList<Faq> _eligibleFaqs;
    private readonly IReadOnlyList<Faq> _directlyAnswerableFaqs;
    private readonly IReadOnlyDictionary<string, PhotoIssue> _eligiblePhotoIssues;
    private readonly Dictionary<string, Statement> _statementById;
    private readonly Dictionary<string, Faq> _faqById;
    private readonly Dictionary<string, PhotoIssue> _photoIssueByKey;

    public string KbVersion { get; }
    public KbMessages Messages { get; }

    /// <summary>The raw knowledge base, exposed for the ReferenceValidator's
    /// clarify-flow navigation structure and dangling/ineligible classification.
    /// NEVER a source of served statement/FAQ/photoIssue content.</summary>
    public KnowledgeBase RawKb { get; }

    public KnowledgeBaseProvider(KnowledgeBase kb)
    {
        RawKb = kb;
        KbVersion = kb.KbVersion;
        Messages = kb.Messages;

        _eligibleStatements = kb.Statements.Where(Eligibility.IsEligible).ToList();
        _eligibleFaqs = kb.Faqs.Where(Eligibility.IsEligible).ToList();
        _directlyAnswerableFaqs = kb.Faqs.Where(Eligibility.IsDirectlyAnswerable).ToList();
        _eligiblePhotoIssues = kb.PhotoIssues
            .Where(kv => Eligibility.IsEligible(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        // Index by id/key for guarded single-entry lookups. Last-wins on dup ids
        // mirrors the TS Map construction.
        _statementById = new Dictionary<string, Statement>();
        foreach (var s in _eligibleStatements) _statementById[s.Id] = s;
        _faqById = new Dictionary<string, Faq>();
        foreach (var f in _eligibleFaqs) _faqById[f.Id] = f;
        _photoIssueByKey = new Dictionary<string, PhotoIssue>(_eligiblePhotoIssues);
    }

    public IReadOnlyList<Statement> GetEligibleStatements()
        => _eligibleStatements.Where(Eligibility.IsEligible).ToList();

    public IReadOnlyList<Faq> GetEligibleFaqs()
        => _eligibleFaqs.Where(Eligibility.IsEligible).ToList();

    public IReadOnlyList<Faq> GetDirectlyAnswerableFaqs()
        => _directlyAnswerableFaqs.Where(Eligibility.IsDirectlyAnswerable).ToList();

    public IReadOnlyDictionary<string, PhotoIssue> GetEligiblePhotoIssues()
    {
        var guarded = new Dictionary<string, PhotoIssue>();
        foreach (var (key, issue) in _photoIssueByKey)
        {
            if (Eligibility.IsEligible(issue)) guarded[key] = issue;
        }
        return guarded;
    }

    public Statement? GetStatementById(string id)
        => _statementById.TryGetValue(id, out var s) && Eligibility.IsEligible(s) ? s : null;

    public Faq? GetFaqById(string id)
        => _faqById.TryGetValue(id, out var f) && Eligibility.IsEligible(f) ? f : null;

    public PhotoIssue? GetPhotoIssueByKey(string key)
        => _photoIssueByKey.TryGetValue(key, out var p) && Eligibility.IsEligible(p) ? p : null;

    /// <summary>Build a provider from an already-loaded KB (injectable for tests).</summary>
    public static KnowledgeBaseProvider FromKnowledgeBase(KnowledgeBase kb) => new(kb);

    /// <summary>
    /// Resolve the configured runtime KB path, rejecting empty values and the
    /// forbidden developer authoring path.
    /// </summary>
    public static string ResolveConfiguredPath(string? explicitPath = null)
    {
        var configured = explicitPath ?? Environment.GetEnvironmentVariable(KbPathEnvVar);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"KnowledgeBaseProvider: no knowledge-base path configured. Set {KbPathEnvVar} " +
                "to the runtime location of the approved, deployed kb.json.");
        }

        if (NormalizePath(configured) == NormalizePath(ForbiddenDevPath))
        {
            throw new InvalidOperationException(
                "KnowledgeBaseProvider: refusing to load from the developer authoring path. " +
                "Configure a runtime KB location (bundled artifact or deployed object), " +
                "not the absolute developer path.");
        }

        return configured;
    }

    /// <summary>Case-insensitive, separator-insensitive path comparison for the guard.</summary>
    private static string NormalizePath(string p)
        => p.Replace('\\', '/').ToLowerInvariant().Trim();

    /// <summary>Read and parse the knowledge base from a configured path. Loading is a
    /// pure file read + parse; schema validation is a deploy-time concern (Req 9.4).</summary>
    public static KnowledgeBase LoadFromPath(string path)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(path);
        }
        catch (Exception cause)
        {
            throw new InvalidOperationException(
                $"KnowledgeBaseProvider: failed to read knowledge base at \"{path}\".", cause);
        }

        try
        {
            var kb = JsonSerializer.Deserialize<KnowledgeBase>(raw, JsonOptions);
            return kb ?? throw new InvalidOperationException(
                $"KnowledgeBaseProvider: knowledge base at \"{path}\" deserialized to null.");
        }
        catch (JsonException cause)
        {
            throw new InvalidOperationException(
                $"KnowledgeBaseProvider: knowledge base at \"{path}\" is not valid JSON.", cause);
        }
    }

    // --- Cold-start cache (per Lambda container; reused across warm invocations) ---
    private static KnowledgeBaseProvider? _cached;
    private static readonly Lock CacheLock = new();

    /// <summary>
    /// Get the cold-start-cached provider, loading and filtering the approved
    /// `kb.json` from the CONFIGURED location on first use.
    /// </summary>
    public static KnowledgeBaseProvider GetCached(string? path = null, bool forceReload = false)
    {
        lock (CacheLock)
        {
            if (_cached is not null && !forceReload) return _cached;
            var resolved = ResolveConfiguredPath(path);
            var kb = LoadFromPath(resolved);
            _cached = new KnowledgeBaseProvider(kb);
            return _cached;
        }
    }

    /// <summary>Reset the cold-start cache (tests).</summary>
    public static void ResetCache()
    {
        lock (CacheLock) { _cached = null; }
    }
}
