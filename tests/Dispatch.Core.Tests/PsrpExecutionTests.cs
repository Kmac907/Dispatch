using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Dispatch.Transports.Psrp;

namespace Dispatch.Core.Tests;

public sealed class PsrpExecutionTests
{
    [Fact]
    public async Task PsrpExecutorReturnsStructuredNotImplementedFailure()
    {
        using var script = TemporaryScript.Create();
        var executor = new PsrpScriptExecutor();
        var request = new TransportScriptExecutionRequest(
            CreatePlan(script.Path, TransportKind.Psrp),
            CreateTargetExecution(),
            new TargetScriptPreparationResult(
                Target: new TargetSpec("PC001"),
                RemoteScriptPath: @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1",
                AdminShareScriptPath: null,
                Succeeded: true));

        var result = await executor.ExecuteScriptAsync(request, CancellationToken.None);

        Assert.Equal(FailureCategory.TransportUnavailable, result.FailureCategory);
        Assert.Equal("PSRP execution is not implemented in this slice.", result.FailureMessage);
        Assert.NotNull(result.Metadata);
        Assert.Equal("psrp", result.Metadata["transport"]);
        Assert.Equal("not-implemented", result.Metadata["mode"]);
    }

    private static ExecutionPlan CreatePlan(string scriptPath, TransportKind transport) =>
        new(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-17T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec("PC001")],
                Payload: new ScriptPayload(scriptPath, []),
                Transport: transport,
                ExecutionContext: new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", false),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy([]),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Tests\run-001")),
            Targets: [CreateTargetExecution()],
            DryRun: false);

    private static TargetExecution CreateTargetExecution() =>
        new(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: @"C:\Dispatch\Tests\run-001\Targets\PC001",
            PlannedLocalResultPath: @"C:\Dispatch\Tests\run-001\Targets\PC001\result.json",
            PlannedRemoteScriptPath: null,
            PlannedCommand: null);
}
