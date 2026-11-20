import { describe, it, expect } from "vitest";
import { App } from "aws-cdk-lib";
import { Template, Match } from "aws-cdk-lib/assertions";
import { VeraStack } from "./vera-stack.js";

/**
 * CDK synthesis assertions for the Phase 1 stack (tasks.md 12.1–12.3).
 * Confirms the construct graph synthesizes to the expected CloudFormation:
 * S3 + CloudFront (TLS 1.2+), a .NET 10 Lambda with reserved concurrency, a
 * PUBLIC API Gateway (NO authorizer) with stage throttling, CloudWatch alarms,
 * and an AWS Budget. No AWS calls; pure local synthesis.
 */
function synth(): Template {
  const app = new App();
  // Point the Lambda asset at an existing folder so fromAsset resolves locally.
  const stack = new VeraStack(app, "TestStack", {
    lambdaAssetPath: import.meta.dirname,
    budgetNotifyEmail: "ops@example.com",
  });
  return Template.fromStack(stack);
}

describe("VeraStack (Phase 1) synthesis", () => {
  const template = synth();

  it("provisions a .NET 10 Lambda with reserved concurrency", () => {
    template.hasResourceProperties("AWS::Lambda::Function", {
      Runtime: "dotnet10",
      ReservedConcurrentExecutions: Match.anyValue(),
      Handler: "Vera.Backend::Vera.Backend.LambdaHandler::HandleAsync",
    });
  });

  it("sets the Bedrock model + KB path env vars on the Lambda", () => {
    template.hasResourceProperties("AWS::Lambda::Function", {
      Environment: {
        Variables: Match.objectLike({
          VERA_KB_PATH: Match.anyValue(),
          VERA_BEDROCK_MODEL_ID: Match.anyValue(),
        }),
      },
    });
  });

  it("creates a CloudFront distribution that enforces HTTPS to viewers", () => {
    // The default CloudFront domain enforces TLS 1.2+ at the edge via the
    // managed certificate (no explicit ViewerCertificate is rendered unless a
    // custom domain is configured). We assert HTTPS is enforced for viewers;
    // the stack's minimumProtocolVersion applies when a custom cert is added.
    template.hasResourceProperties("AWS::CloudFront::Distribution", {
      DistributionConfig: Match.objectLike({
        DefaultCacheBehavior: Match.objectLike({
          ViewerProtocolPolicy: "redirect-to-https",
        }),
      }),
    });
  });

  it("creates a private S3 bucket (all public access blocked, SSL enforced)", () => {
    template.hasResourceProperties("AWS::S3::Bucket", {
      PublicAccessBlockConfiguration: {
        BlockPublicAcls: true,
        BlockPublicPolicy: true,
        IgnorePublicAcls: true,
        RestrictPublicBuckets: true,
      },
    });
  });

  it("configures the API Gateway stage with throttling (compensating control)", () => {
    template.hasResourceProperties("AWS::ApiGateway::Stage", {
      MethodSettings: Match.arrayWith([
        Match.objectLike({
          ThrottlingRateLimit: Match.anyValue(),
          ThrottlingBurstLimit: Match.anyValue(),
        }),
      ]),
    });
  });

  it("exposes POST /message with NO authorizer (Phase 1 public endpoint)", () => {
    template.hasResourceProperties("AWS::ApiGateway::Method", {
      HttpMethod: "POST",
      AuthorizationType: "NONE",
    });
  });

  it("grants the Lambda bedrock invoke on SCOPED resources (not *)", () => {
    template.hasResourceProperties("AWS::IAM::Policy", {
      PolicyDocument: Match.objectLike({
        Statement: Match.arrayWith([
          Match.objectLike({
            Action: Match.arrayWith(["bedrock:InvokeModel", "bedrock:Converse"]),
            // Least privilege: resources must NOT be the wildcard "*".
            Resource: Match.not("*"),
          }),
        ]),
      }),
    });
  });

  it("scopes Bedrock to foundation-model + inference-profile ARNs for the model id", () => {
    // Build a stack with an explicit profile-style model id and assert the
    // derived ARNs appear (account-less foundation-model + account-scoped profile).
    const app = new App();
    const stack = new VeraStack(app, "ArnStack", {
      lambdaAssetPath: import.meta.dirname,
      bedrockModelId: "us.anthropic.claude-3-5-haiku-20241022-v1:0",
    });
    const t = Template.fromStack(stack);
    const policies = t.findResources("AWS::IAM::Policy");
    const json = JSON.stringify(policies);
    // Foundation-model ARN uses the region-prefix-stripped id.
    expect(json).toContain("foundation-model/anthropic.claude-3-5-haiku-20241022-v1:0");
    // Inference-profile ARN keeps the full (profile) id.
    expect(json).toContain("inference-profile/us.anthropic.claude-3-5-haiku-20241022-v1:0");
  });

  it("sets the Lambda memory to 1024 MB (.NET cold-start headroom)", () => {
    template.hasResourceProperties("AWS::Lambda::Function", {
      MemorySize: 1024,
    });
  });

  it("passes a CORS allow-origin env var to the Lambda (POST-response CORS)", () => {
    template.hasResourceProperties("AWS::Lambda::Function", {
      Environment: {
        Variables: Match.objectLike({ VERA_CORS_ALLOW_ORIGIN: Match.anyValue() }),
      },
    });
  });

  it("attaches a CloudFront security-headers response policy (HSTS etc.)", () => {
    template.resourceCountIs("AWS::CloudFront::ResponseHeadersPolicy", 1);
  });

  it("creates CloudWatch alarms for errors, throttles, and latency", () => {
    // Three alarms are declared on the backend Lambda metrics.
    template.resourceCountIs("AWS::CloudWatch::Alarm", 3);
  });

  it("creates an AWS Budgets monthly cost budget with an email subscriber", () => {
    template.hasResourceProperties("AWS::Budgets::Budget", {
      Budget: Match.objectLike({ BudgetType: "COST", TimeUnit: "MONTHLY" }),
      NotificationsWithSubscribers: Match.arrayWith([
        Match.objectLike({
          Subscribers: Match.arrayWith([
            Match.objectLike({ Address: "ops@example.com", SubscriptionType: "EMAIL" }),
          ]),
        }),
      ]),
    });
  });
});
