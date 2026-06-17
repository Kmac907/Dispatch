using System.Text.Json;
using Dispatch.Core;
using Dispatch.Core.Defaults;
using Dispatch.Core.Models;
using Dispatch.Core.Validation;

namespace Dispatch.Core.Tests;

public sealed class JobResultModelTests
{
    [Theory]
    [InlineData(TargetExecutionState.Succeeded, FailureCategory.None)]
    [InlineData(TargetExecutionState.Failed, FailureCategory.ExecutionFailed)]
    [InlineData(TargetExecutionState.TimedOut, FailureCategory.TimedOut)]
    [InlineData(TargetExecutionState.Cancelled, FailureCategory.Cancelled)]
    public void TargetResultRepresentsTerminalStates(TargetExecutionState state, FailureCategory failureCategory)
    {
        var result = CreateTargetResult(state, failureCategory);
        var json = DispatchJson.Serialize(result);
        var roundTripped = JsonSerializer.Deserialize<TargetExecutionResult>(json, DispatchJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal(state, roundTripped.State);
        Assert.Equal(failureCategory, roundTripped.FailureCategory);
        Assert.Equal("run-001", roundTripped.RunId);
        Assert.Equal("PC001", roundTripped.Target);
        Assert.Equal("C:\\Runs\\run-001\\Targets\\PC001\\result.json", roundTripped.ResultPath);
    }

    [Fact]
    public void ExecutionPlanRoundTripsThroughJson()
    {
        var job = new DispatchJob(
            RunId: "run-001",
            Targets: [new TargetSpec("PC001", "computer-name")],
            Payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", ["-Verbose"]),
            Transport: TransportKind.PsExec,
            ExecutionContext: new ExecutionContextOptions(RunAsSystem: true),
            ScriptTransferPolicy: new ScriptTransferPolicy(DispatchDefaults.RemoteRunRoot, RequiresEndpointLocalScriptPath: true),
            TimeoutPolicy: new TimeoutPolicy(ExecutionTimeout: TimeSpan.FromMinutes(30)),
            RetryPolicy: new RetryPolicy(),
            ExpectedExitCodes: [0, 3010],
            ArtifactPolicy: new ArtifactPolicy(["logs", "artifacts"]),
            ResultPolicy: new ResultPolicy(DispatchDefaults.LocalRunRoot));

        var plan = new ExecutionPlan(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            Job: job,
            Targets:
            [
                new TargetExecution(
                    RunId: "run-001",
                    Target: new TargetSpec("PC001", "computer-name"),
                    State: TargetExecutionState.Pending,
                    PlannedLocalTargetRoot: "C:\\Runs\\run-001\\Targets\\PC001",
                    PlannedLocalResultPath: "C:\\Runs\\run-001\\Targets\\PC001\\result.json",
                    PlannedRemoteScriptPath: "C:\\ProgramData\\Dispatch\\Runs\\run-001\\script\\Fix.ps1")
            ],
            DryRun: true);

        var json = DispatchJson.Serialize(plan);
        var roundTripped = JsonSerializer.Deserialize<ExecutionPlan>(json, DispatchJson.Options);

        Assert.NotNull(roundTripped);
        Assert.Equal("run-001", roundTripped.RunId);
        Assert.True(roundTripped.DryRun);
        Assert.Equal(TransportKind.PsExec, roundTripped.Job.Transport);
        Assert.Equal(TargetExecutionState.Pending, roundTripped.Targets[0].State);
    }

    [Fact]
    public void RequestValidationAllowsCurrentSupportedTransportPayloadCombinations()
    {
        var psexecScript = DispatchRequestValidator.Validate(new DispatchRequest(
            payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec));

        var winrmScript = DispatchRequestValidator.Validate(new DispatchRequest(
            payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm));

        var winrmCommand = DispatchRequestValidator.Validate(new DispatchRequest(
            payload: new CommandPayload("whoami", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm));

        var psexecCommand = DispatchRequestValidator.Validate(new DispatchRequest(
            payload: new CommandPayload("whoami", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec));

        var psrpScript = DispatchRequestValidator.Validate(new DispatchRequest(
            payload: new ScriptPayload("C:\\Scripts\\Fix.ps1", []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.Psrp));

        Assert.True(psexecScript.IsValid);
        Assert.True(winrmScript.IsValid);
        Assert.True(winrmCommand.IsValid);
        Assert.Contains(psexecCommand.Errors, error => error.Code == "UnsupportedTransportPayload");
        Assert.Contains(psrpScript.Errors, error => error.Code == "UnsupportedTransportPayload");
    }

    [Fact]
    public void ResultCsvRowsFlattenRunAndTargetSchemas()
    {
        var startedAt = DateTimeOffset.Parse("2026-06-13T20:00:00Z");
        var endedAt = DateTimeOffset.Parse("2026-06-13T20:00:02Z");
        var target = CreateTargetResult(TargetExecutionState.Succeeded, FailureCategory.None, startedAt, endedAt);
        var run = new DispatchRunResult(
            RunId: "run-001",
            StartedAt: startedAt,
            EndedAt: endedAt,
            RequestedBy: "SCF\\Admin",
            Transport: TransportKind.PsExec,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets: [target],
            ResultPath: "C:\\Runs\\run-001\\Admin\\results.json");

        var runCsv = run.ToCsvRow();
        var targetCsv = target.ToCsvRow();

        Assert.Equal("psexec", runCsv.Transport);
        Assert.Equal("script", runCsv.PayloadType);
        Assert.Equal(1, runCsv.SuccessCount);
        Assert.Equal("psexec", targetCsv.Transport);
        Assert.Equal("0;3010", targetCsv.ExpectedExitCodes);
        Assert.Equal("logs\\install.log", targetCsv.Artifacts);
    }

    [Fact]
    public void ExpectedExitCodePolicyClassifiesSuccessCodes()
    {
        var policy = new ExpectedExitCodePolicy([0, 3010]);

        Assert.True(policy.IsSuccess(0));
        Assert.True(policy.IsSuccess(3010));
        Assert.False(policy.IsSuccess(1603));
    }

    private static TargetExecutionResult CreateTargetResult(
        TargetExecutionState state,
        FailureCategory failureCategory,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? endedAt = null) =>
        new(
            RunId: "run-001",
            Target: "PC001",
            Transport: TransportKind.PsExec,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            State: state,
            ExitCode: state == TargetExecutionState.Succeeded ? 0 : 1,
            ExpectedExitCodes: [0, 3010],
            StartedAt: startedAt ?? DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            EndedAt: endedAt ?? DateTimeOffset.Parse("2026-06-13T20:00:01Z"),
            FailureCategory: failureCategory,
            FailureMessage: failureCategory == FailureCategory.None ? null : failureCategory.ToString(),
            StdoutPath: "C:\\Runs\\run-001\\Targets\\PC001\\stdout.txt",
            StderrPath: "C:\\Runs\\run-001\\Targets\\PC001\\stderr.txt",
            ResultPath: "C:\\Runs\\run-001\\Targets\\PC001\\result.json",
            Artifacts: ["logs\\install.log"],
            ArtifactCollectionStatus: "collected",
            ArtifactCollectionFailureMessage: null,
            SecretHandoffStatus: "notSupported",
            CleanupStatus: "notStarted",
            TransportMetadata: new Dictionary<string, string>
            {
                ["transport"] = "psexec"
            });
}
