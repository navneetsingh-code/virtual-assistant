using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Vera.Backend.Generation;
using Vera.Backend.Kb;
using Vera.Backend.Models;
using Vera.Backend.Pipeline;

// Use the System.Text.Json Lambda serializer for event (de)serialization.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Vera.Backend;

/// <summary>
/// AWS Lambda entry point behind API Gateway (finishes tasks.md 9.1 for the C#
/// backend). Adapts an API Gateway proxy request to the shared contract,
/// invokes the cold-start <see cref="Pipeline.Pipeline"/>, and returns a proxy
/// response carrying the <see cref="MessageResponse"/> JSON.
///
/// Phase 1 (POC): there is NO authorizer in front of this handler; the endpoint
/// is public and protected by the compensating controls configured in infra
/// (throttling, reserved concurrency, CloudWatch, Budgets). The handler itself
/// adds no authentication (design.md -> Phase 1). It never logs the raw request
/// body — only the pipeline's PII-excluded operational log entries are emitted.
///
/// Cold-start singletons (provider, pipeline, Bedrock client) are built once per
/// container and reused across warm invocations.
/// </summary>
public sealed class LambdaHandler
{
    /// <summary>Env var naming the Bedrock model id / inference profile for the free-form path.</summary>
    public const string BedrockModelEnvVar = "VERA_BEDROCK_MODEL_ID";

    /// <summary>Env var naming the CORS allow-origin the browser may read the POST
    /// response from. The API Gateway preflight only covers OPTIONS; the proxy
    /// POST response must carry this header itself. Defaults to "*".</summary>
    public const string CorsAllowOriginEnvVar = "VERA_CORS_ALLOW_ORIGIN";

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly Pipeline.Pipeline _pipeline;
    private readonly string _corsAllowOrigin;

    /// <summary>Default constructor used by the Lambda runtime: wires the real
    /// Bedrock adapter and loads the cold-start KB provider from the configured path.</summary>
    public LambdaHandler()
        : this(BuildDefaultPipeline())
    {
    }

    /// <summary>Injectable constructor for tests (fake pipeline/Bedrock).</summary>
    public LambdaHandler(Pipeline.Pipeline pipeline, string? corsAllowOrigin = null)
    {
        _pipeline = pipeline;
        _corsAllowOrigin =
            corsAllowOrigin
            ?? Environment.GetEnvironmentVariable(CorsAllowOriginEnvVar)
            ?? "*";
    }

    private static Pipeline.Pipeline BuildDefaultPipeline()
    {
        var provider = KnowledgeBaseProvider.GetCached();
        var modelId = Environment.GetEnvironmentVariable(BedrockModelEnvVar)
            ?? throw new InvalidOperationException(
                $"LambdaHandler: {BedrockModelEnvVar} is not configured.");
        var runtime = new AmazonBedrockRuntimeClient();
        var bedrock = new BedrockClient(runtime, new BedrockOptions { ModelId = modelId });
        return Backend.Create(new Backend.Options { Bedrock = bedrock, Provider = provider });
    }

    /// <summary>
    /// API Gateway proxy integration entry point. Parses the body as the shared
    /// request, runs the pipeline (which never throws), and returns 200 with the
    /// response JSON. A malformed body yields a 400 with a generic message (no
    /// echo of the raw body).
    /// </summary>
    public async Task<APIGatewayProxyResponse> HandleAsync(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        MessageRequest? parsed = null;
        if (!string.IsNullOrWhiteSpace(request.Body))
        {
            try
            {
                parsed = JsonSerializer.Deserialize<MessageRequest>(request.Body, JsonOptions);
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        if (parsed is null || parsed.SessionId is null || parsed.Message is null)
        {
            // Do not echo the raw body. A minimal, PII-free error.
            return new APIGatewayProxyResponse
            {
                StatusCode = 400,
                Headers = ResponseHeaders(),
                Body = JsonSerializer.Serialize(new { error = "Invalid request body." }, JsonOptions),
            };
        }

        var response = await _pipeline.HandleAsync(parsed);

        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Headers = ResponseHeaders(),
            Body = JsonSerializer.Serialize(response, JsonOptions),
        };
    }

    private Dictionary<string, string> ResponseHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            // The browser can only read the POST response if the actual response
            // carries this header (the API Gateway preflight only covers OPTIONS).
            ["Access-Control-Allow-Origin"] = _corsAllowOrigin,
        };
        // When echoing a specific origin (not "*"), advertise that the response
        // varies by Origin so shared caches don't serve it to a different origin.
        if (_corsAllowOrigin != "*")
        {
            headers["Vary"] = "Origin";
        }
        return headers;
    }
}
