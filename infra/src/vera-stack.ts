import * as path from "node:path";
import { Stack, type StackProps, Duration, RemovalPolicy, CfnOutput } from "aws-cdk-lib";
import type { Construct } from "constructs";
import * as s3 from "aws-cdk-lib/aws-s3";
import * as cloudfront from "aws-cdk-lib/aws-cloudfront";
import * as origins from "aws-cdk-lib/aws-cloudfront-origins";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as apigw from "aws-cdk-lib/aws-apigateway";
import * as cloudwatch from "aws-cdk-lib/aws-cloudwatch";
import * as budgets from "aws-cdk-lib/aws-budgets";
import * as logs from "aws-cdk-lib/aws-logs";
import * as iam from "aws-cdk-lib/aws-iam";
import { Annotations } from "aws-cdk-lib";

/**
 * Phase 1 (POC) configuration knobs for the Vera stack. All have conservative
 * defaults; production values are confirmed at deploy time.
 */
export interface VeraStackProps extends StackProps {
  /**
   * Path to the built, self-contained .NET 10 Lambda publish output directory
   * (the folder containing the Lambda bundle). Defaults to the backend publish
   * output; overridden in tests/synth.
   */
  readonly lambdaAssetPath?: string;
  /** The Lambda handler string. */
  readonly lambdaHandler?: string;
  /** Bedrock model id / inference profile for the free-form path. */
  readonly bedrockModelId?: string;
  /** Runtime KB path inside the Lambda bundle (VERA_KB_PATH). */
  readonly kbPath?: string;
  /** Reserved concurrency cap for the backend Lambda (Phase 1 blast-radius bound). */
  readonly reservedConcurrency?: number;
  /** API Gateway stage steady-state request rate (req/s). */
  readonly throttleRateLimit?: number;
  /** API Gateway stage burst limit. */
  readonly throttleBurstLimit?: number;
  /** Monthly AWS Budgets cost threshold (USD) for the alert. */
  readonly monthlyBudgetUsd?: number;
  /** Email to notify on the budget threshold (optional). */
  readonly budgetNotifyEmail?: string;
  /**
   * Allowed CORS origins for the Backend_API (browser control only, NOT
   * authorization). Defaults to the IDVA host origins when provided; falls back
   * to `*` only if left unset (Phase 1 POC convenience). Prefer setting this to
   * the IDVA origin(s) so the public endpoint is not readable from arbitrary
   * sites in a browser.
   */
  readonly allowedOrigins?: string[];
  /**
   * Explicit Bedrock model ARNs the Lambda may invoke (least privilege). When
   * omitted, the stack derives foundation-model + inference-profile ARNs for
   * the configured model id in this account/region. Provide ARNs directly to
   * pin exactly which models are permitted.
   */
  readonly bedrockModelArns?: string[];
}

/**
 * Vera Phase 1 (POC) infrastructure — AWS CDK (TypeScript).
 *
 * Provisions the design's Phase 1 topology (design.md -> Architecture -> Phase
 * 1): the Chat_Widget delivered from S3 via CloudFront over HTTPS/TLS 1.2+, and
 * the C#/.NET 10 Backend_API Lambda behind a PUBLIC API Gateway (NO authorizer
 * in Phase 1), protected by the compensating controls — stage/route throttling
 * (rate + burst), Lambda reserved concurrency, CloudWatch alarms, and an AWS
 * Budgets alert. These are NOT authentication and do NOT guarantee a hard
 * spending cap (documented in design.md Phase 1 security considerations).
 *
 * Nothing here is deployed automatically; this only declares the stack. The
 * .NET 10 Lambda uses the managed `dotnet10` runtime (verified available).
 *
 * Requirements: 2.1, 2.2, 2.3 (widget delivery); 3.1, 3.2, 3.3, 3.5, 3.6, 3.7,
 * 11.2 (public endpoint + compensating controls).
 */
export class VeraStack extends Stack {
  constructor(scope: Construct, id: string, props: VeraStackProps = {}) {
    super(scope, id, props);

    const reservedConcurrency = props.reservedConcurrency ?? 10;
    const throttleRateLimit = props.throttleRateLimit ?? 20;
    const throttleBurstLimit = props.throttleBurstLimit ?? 40;
    const hasExplicitOrigins =
      props.allowedOrigins !== undefined && props.allowedOrigins.length > 0;
    const allowedOrigins = hasExplicitOrigins ? props.allowedOrigins! : ["*"];
    // The value the Lambda echoes in Access-Control-Allow-Origin on the POST
    // response. A single configured origin is echoed directly; otherwise `*`.
    const corsAllowOrigin =
      allowedOrigins.length === 1 ? (allowedOrigins[0] ?? "*") : "*";

    // Surface a loud warning at synth/deploy if CORS was left wide open. The
    // public Phase 1 endpoint should be restricted to the IDVA origin(s) so it
    // is not readable from arbitrary sites in a browser (CORS is a browser
    // control, not authorization — a direct non-browser call still reaches it).
    if (!hasExplicitOrigins) {
      Annotations.of(this).addWarning(
        "CORS allowedOrigins is not set — defaulting to '*'. Set " +
          "'-c allowedOrigins=https://<idva-origin>' before a real deployment " +
          "so the public Backend_API is not browser-readable from any site.",
      );
    }
    // NOTE: confirm the correct model-id FORM for the target region before
    // deploy. Many regions require a cross-region INFERENCE PROFILE id (e.g.
    // "us.anthropic.claude-3-5-haiku-20241022-v1:0") rather than the bare
    // foundation-model id below, and Bedrock model access must be enabled in
    // the account/region. The IAM policy derives ARNs for either form.
    const bedrockModelId =
      props.bedrockModelId ?? "anthropic.claude-3-5-haiku-20241022-v1:0";
    const kbPath = props.kbPath ?? "/var/task/kb.json";
    const lambdaAssetPath =
      props.lambdaAssetPath ??
      path.resolve(import.meta.dirname, "../../backend/Vera.Backend/bin/Release/net10.0/publish");
    const lambdaHandler =
      props.lambdaHandler ?? "Vera.Backend::Vera.Backend.LambdaHandler::HandleAsync";

    // --- Widget delivery: private S3 bucket behind CloudFront (Req 2.1-2.3) ---
    const widgetBucket = new s3.Bucket(this, "WidgetBucket", {
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      enforceSSL: true,
      removalPolicy: RemovalPolicy.RETAIN,
    });

    // Security response headers (HSTS, nosniff, frame + referrer policy) for the
    // widget assets served by CloudFront.
    const securityHeaders = new cloudfront.ResponseHeadersPolicy(
      this,
      "WidgetSecurityHeaders",
      {
        securityHeadersBehavior: {
          strictTransportSecurity: {
            accessControlMaxAge: Duration.days(365),
            includeSubdomains: true,
            override: true,
          },
          contentTypeOptions: { override: true },
          frameOptions: {
            frameOption: cloudfront.HeadersFrameOption.SAMEORIGIN,
            override: true,
          },
          referrerPolicy: {
            referrerPolicy: cloudfront.HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN,
            override: true,
          },
        },
      },
    );

    const distribution = new cloudfront.Distribution(this, "WidgetDistribution", {
      defaultRootObject: "index.html",
      // HTTPS only; enforce modern TLS at the viewer (TLS 1.2+).
      minimumProtocolVersion: cloudfront.SecurityPolicyProtocol.TLS_V1_2_2021,
      defaultBehavior: {
        origin: origins.S3BucketOrigin.withOriginAccessControl(widgetBucket),
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        responseHeadersPolicy: securityHeaders,
      },
    });

    // --- Backend_API: C#/.NET 10 Lambda (managed dotnet10 runtime) ---
    const backendLogGroup = new logs.LogGroup(this, "BackendLogGroup", {
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: RemovalPolicy.DESTROY,
    });

    const backendFn = new lambda.Function(this, "BackendFunction", {
      runtime: lambda.Runtime.DOTNET_10,
      handler: lambdaHandler,
      code: lambda.Code.fromAsset(lambdaAssetPath),
      // .NET cold starts benefit from more memory (more CPU); 1024 MB keeps
      // p95 latency and cost-per-request reasonable for a low-traffic POC.
      memorySize: 1024,
      timeout: Duration.seconds(30),
      // Compensating control: cap concurrent executions (blast radius / cost).
      reservedConcurrentExecutions: reservedConcurrency,
      logGroup: backendLogGroup,
      environment: {
        VERA_KB_PATH: kbPath,
        VERA_BEDROCK_MODEL_ID: bedrockModelId,
        // The handler echoes this on the POST response so the browser can read
        // it (the preflight CORS below only covers OPTIONS). A single origin is
        // echoed directly; "*" allows any origin (Phase 1 POC default).
        VERA_CORS_ALLOW_ORIGIN: corsAllowOrigin,
      },
    });

    // The Lambda invokes Bedrock on the free-form path only. Least privilege:
    // scope to the specific model + inference-profile ARNs rather than "*".
    // When explicit ARNs are not supplied, derive them for the configured
    // model id in this account/region. Cross-region inference profiles (ids
    // prefixed "us."/"eu."/etc.) also need the underlying foundation-model ARN,
    // which is account-less and region-scoped.
    const bedrockResources = props.bedrockModelArns ?? [
      // Foundation model (account-less, region-scoped).
      Stack.of(this).formatArn({
        service: "bedrock",
        account: "",
        resource: "foundation-model",
        resourceName: bedrockModelId.replace(/^(us|eu|apac|global)\./, ""),
      }),
      // Inference profile (account-scoped), if the model id is a profile.
      Stack.of(this).formatArn({
        service: "bedrock",
        resource: "inference-profile",
        resourceName: bedrockModelId,
      }),
    ];
    backendFn.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["bedrock:InvokeModel", "bedrock:Converse"],
        resources: bedrockResources,
      }),
    );

    // --- API Gateway: PUBLIC REST API, NO authorizer (Phase 1) ---
    // CORS is a browser control only (NOT authorization). Access logging is
    // configured WITHOUT the request body so no raw body / PII is logged.
    const api = new apigw.RestApi(this, "VeraApi", {
      restApiName: "vera-backend",
      description: "Vera Backend_API (Phase 1 POC, public — no authorizer).",
      deployOptions: {
        stageName: "prod",
        // Compensating control: stage-level throttling (rate + burst).
        throttlingRateLimit: throttleRateLimit,
        throttlingBurstLimit: throttleBurstLimit,
        // Do NOT enable data trace / full request logging (would risk body/PII).
        dataTraceEnabled: false,
        loggingLevel: apigw.MethodLoggingLevel.ERROR,
      },
      defaultCorsPreflightOptions: {
        allowOrigins: allowedOrigins,
        allowMethods: ["POST", "OPTIONS"],
        allowHeaders: ["Content-Type"],
      },
    });

    // POST /message → Lambda (no authorizer; Phase 1 public endpoint).
    const message = api.root.addResource("message");
    message.addMethod("POST", new apigw.LambdaIntegration(backendFn), {
      authorizationType: apigw.AuthorizationType.NONE,
    });

    // --- CloudWatch alarms (compensating control: observability) (Req 3.5) ---
    backendFn
      .metricErrors({ period: Duration.minutes(5) })
      .createAlarm(this, "BackendErrorsAlarm", {
        threshold: 5,
        evaluationPeriods: 1,
        alarmDescription: "Vera backend Lambda errors in a 5-minute window.",
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });

    backendFn
      .metricThrottles({ period: Duration.minutes(5) })
      .createAlarm(this, "BackendThrottlesAlarm", {
        threshold: 1,
        evaluationPeriods: 1,
        alarmDescription: "Vera backend Lambda throttles (reserved concurrency hit).",
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });

    backendFn
      .metricDuration({ period: Duration.minutes(5), statistic: "p95" })
      .createAlarm(this, "BackendLatencyAlarm", {
        threshold: Duration.seconds(10).toMilliseconds(),
        evaluationPeriods: 1,
        alarmDescription: "Vera backend Lambda p95 latency over 10s.",
        treatMissingData: cloudwatch.TreatMissingData.NOT_BREACHING,
      });

    // --- AWS Budgets alert (compensating control: cost) (Req 3.6) ---
    // NOTE: a budget ALERTS; it is NOT a hard spending cap.
    new budgets.CfnBudget(this, "VeraMonthlyBudget", {
      budget: {
        budgetType: "COST",
        timeUnit: "MONTHLY",
        budgetLimit: { amount: props.monthlyBudgetUsd ?? 50, unit: "USD" },
      },
      notificationsWithSubscribers: props.budgetNotifyEmail
        ? [
            {
              notification: {
                notificationType: "ACTUAL",
                comparisonOperator: "GREATER_THAN",
                threshold: 80,
                thresholdType: "PERCENTAGE",
              },
              subscribers: [
                { subscriptionType: "EMAIL", address: props.budgetNotifyEmail },
              ],
            },
          ]
        : [],
    });

    // --- Outputs ---
    new CfnOutput(this, "WidgetUrl", {
      value: `https://${distribution.distributionDomainName}`,
      description: "CloudFront URL for the Vera widget assets.",
    });
    new CfnOutput(this, "ApiUrl", {
      value: api.url,
      description: "Public Backend_API base URL (Phase 1: no authorizer).",
    });
    new CfnOutput(this, "WidgetBucketName", { value: widgetBucket.bucketName });
  }
}
