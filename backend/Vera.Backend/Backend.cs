using Vera.Backend.Clarify;
using Vera.Backend.Generation;
using Vera.Backend.Kb;
using Vera.Backend.Logging;
using Vera.Backend.Models;
using Vera.Backend.Photo;
using Vera.Backend.Pii;
using Vera.Backend.Pipeline;

namespace Vera.Backend;

/// <summary>
/// Backend assembly (ported from backend-ts-reference/src/backend.ts) — composes
/// the cold-start singletons into a ready <see cref="Pipeline.Pipeline"/> for the
/// AWS Lambda handler to call.
///
/// The <see cref="IBedrockClient"/> is a required injected dependency so this
/// module (and the test suite) never performs a real AWS call. The Lambda entry
/// wires a real adapter; tests wire a fake.
///
/// Requirements: 3.12 (stateless pipeline), 8.2 (no server-side persistence).
/// </summary>
public static class Backend
{
    /// <summary>Options for assembling the backend pipeline.</summary>
    public sealed record Options
    {
        /// <summary>The injected Bedrock boundary (AWS adapter in prod; fake in tests).</summary>
        public required IBedrockClient Bedrock { get; init; }

        /// <summary>The eligibility-filtered provider. Defaults to the cold-start
        /// singleton loaded from the CONFIGURED KB path. Inject for tests.</summary>
        public KnowledgeBaseProvider? Provider { get; init; }

        /// <summary>Optional log sink (defaults to the console sink → CloudWatch Logs).</summary>
        public ILogSink? LogSink { get; init; }
    }

    /// <summary>
    /// Assemble the <see cref="Pipeline.Pipeline"/>. In production, call with only
    /// <c>Bedrock</c> set and it loads the cold-start provider from the configured
    /// path (the provider carries the raw KB used for clarify navigation and
    /// dangling/ineligible classification).
    /// </summary>
    public static Pipeline.Pipeline Create(Options options)
    {
        var provider = options.Provider ?? KnowledgeBaseProvider.GetCached();
        var validator = new ReferenceValidator(provider, provider.RawKb);

        return new Pipeline.Pipeline(new PipelineDependencies
        {
            Provider = provider,
            Redactor = new PiiRedactor(),
            Logger = new Logger(options.LogSink),
            Matcher = new Matcher.Matcher(provider, validator),
            Clarify = new ClarifyFlowEngine(validator, provider.RawKb),
            Photo = new PhotoIssueResolver(validator),
            Retrieval = new Retrieval(validator, provider),
            Generation = new Generation.Generation(options.Bedrock, provider.Messages, provider.KbVersion),
        });
    }
}
