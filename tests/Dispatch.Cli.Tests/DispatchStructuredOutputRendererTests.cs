using Dispatch.Core.Models;
using Dispatch.Core.Targeting;

namespace Dispatch.Cli.Tests;

public sealed class DispatchStructuredOutputRendererTests
{
    [Fact]
    public void RenderRunResultRedactsSecretLookingValuesFromJson() =>
        RenderRunResultRedactsSecretLookingValues(DispatchOutputMode.Json);

    [Fact]
    public void RenderRunResultRedactsSecretLookingValuesFromNdjson() =>
        RenderRunResultRedactsSecretLookingValues(DispatchOutputMode.Ndjson);

    [Fact]
    public void RenderRunResultRedactsSecretLookingValuesFromYaml() =>
        RenderRunResultRedactsSecretLookingValues(DispatchOutputMode.Yaml);

    [Fact]
    public void RenderRunResultRedactsSecretLookingValuesFromRich() =>
        RenderRunResultRedactsSecretLookingValues(DispatchOutputMode.Rich);

    [Fact]
    public void RenderRunResultRedactsSecretLookingValuesFromTable() =>
        RenderRunResultRedactsSecretLookingValues(DispatchOutputMode.Table);

    [Fact]
    public void RenderApplyPlanRedactsSecretLookingValuesFromRichOutput()
    {
        var plan = new DispatchApplyPlan(
            Mode: "plan",
            Tasks:
            [
                new DispatchApplyPlannedTask(
                    Index: 1,
                    Type: "copy",
                    ScriptPath: null,
                    CommandLine: null,
                    SourcePath: @"C:\Payloads\agent-token=apply-source-secret.msi",
                    DestinationPath: @"C:\Temp\agent.msi?sig=apply-destination-secret",
                    Overwrite: true,
                    Transport: TransportKind.WinRm,
                    Targets: ["PC001"],
                    Tags: [],
                    Plan: null)
            ]);
        using var writer = new StringWriter();

        DispatchStructuredOutputRenderer.RenderApplyPlan(writer, plan, DispatchOutputMode.Rich);

        var output = writer.ToString();
        Assert.DoesNotContain("apply-source-secret", output);
        Assert.DoesNotContain("apply-destination-secret", output);
        Assert.Contains("[redacted]", output);
    }

    [Fact]
    public void RenderPushPlanRedactsSecretLookingValuesFromRichOutput()
    {
        var plan = new DispatchPushPlan(
            Mode: "plan",
            SourcePath: @"C:\Payloads\agent-token=push-source-secret.msi",
            DestinationPath: @"C:\Temp\agent.msi?sig=push-destination-secret",
            SourceBytes: 100,
            Transport: TransportKind.Psrp,
            Targets: [new TargetSpec("PC001")],
            Overwrite: true,
            Checksum: true,
            Backup: false,
            Execute: false,
            Cleanup: false,
            Concurrency: 1,
            OutputMode: DispatchOutputMode.Rich);
        using var writer = new StringWriter();

        DispatchStructuredOutputRenderer.RenderPushPlan(writer, plan, DispatchOutputMode.Rich);

        var output = writer.ToString();
        Assert.DoesNotContain("push-source-secret", output);
        Assert.DoesNotContain("push-destination-secret", output);
        Assert.Contains("[redacted]", output);
    }

    private static void RenderRunResultRedactsSecretLookingValues(DispatchOutputMode mode)
    {
        var result = new DispatchRunResult(
            RunId: "run-001",
            StartedAt: DateTimeOffset.UnixEpoch,
            EndedAt: DateTimeOffset.UnixEpoch.AddSeconds(1),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "run-001",
                    Target: "PC001",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Script,
                    PayloadName: "Fix.ps1",
                    State: TargetExecutionState.Failed,
                    ExitCode: null,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.UnixEpoch,
                    EndedAt: DateTimeOffset.UnixEpoch.AddSeconds(1),
                    FailureCategory: FailureCategory.AuthenticationFailed,
                    FailureMessage: "transport failed token=visible-token sig=visible-signature",
                    TransportMetadata: new Dictionary<string, string>
                    {
                        ["authorization"] = "password=visible-password"
                    },
                    StreamRecords:
                    [
                        new PowerShellStreamRecord(PowerShellStreamKind.Error, "SharedAccessSignature=visible-shared")
                    ])
            ],
            ResultPath: @"C:\Dispatch\Runs\run-001\Admin\results.json");
        using var writer = new StringWriter();

        DispatchStructuredOutputRenderer.RenderRunResult(writer, result, mode);

        var output = writer.ToString();
        Assert.DoesNotContain("visible-token", output);
        Assert.DoesNotContain("visible-signature", output);
        Assert.DoesNotContain("visible-password", output);
        Assert.DoesNotContain("visible-shared", output);
        Assert.Contains("[redacted]", output);
    }
}
