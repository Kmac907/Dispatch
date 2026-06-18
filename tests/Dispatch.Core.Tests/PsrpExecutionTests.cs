using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Dispatch.Transports.Psrp;

namespace Dispatch.Core.Tests;

public sealed class PsrpExecutionTests
{
    [Fact]
    public async Task PsrpEndpointProbeSucceedsWhenDefaultWsManPortIsReachable()
    {
        var probe = new PsrpEndpointProbe(
            new StubDnsResolver(PsrpProbeResult.Success),
            new StubPortProbe(PsrpProbeResult.Success, PsrpProbeResult.Failed("not used")));

        var result = await probe.ProbeAsync(CreateProbeRequest("PC001"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.NotNull(result.Metadata);
        Assert.Equal("psrp", result.Metadata["probe"]);
        Assert.Equal("psrp-over-wsman", result.Metadata["protocol"]);
        Assert.Equal("5985", result.Metadata["port"]);
        Assert.Equal("http", result.Metadata["scheme"]);
    }

    [Fact]
    public async Task PsrpEndpointProbeFailsWhenDnsResolutionFails()
    {
        var probe = new PsrpEndpointProbe(
            new StubDnsResolver(PsrpProbeResult.Failed("DNS resolution failed for 'PC001': No such host is known.")),
            new StubPortProbe(PsrpProbeResult.Success, PsrpProbeResult.Success));

        var result = await probe.ProbeAsync(CreateProbeRequest("PC001"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.ProbeFailed, result.FailureCategory);
        Assert.Contains("DNS resolution failed", result.FailureMessage);
        Assert.NotNull(result.Metadata);
        Assert.Equal("dns", result.Metadata["stage"]);
    }

    [Fact]
    public async Task PsrpEndpointProbeFailsWhenDefaultWsManPortsAreUnreachable()
    {
        var probe = new PsrpEndpointProbe(
            new StubDnsResolver(PsrpProbeResult.Success),
            new StubPortProbe(
                PsrpProbeResult.Failed("Could not connect to 'PC001' on TCP port 5985: actively refused."),
                PsrpProbeResult.Failed("Timed out connecting to 'PC001' on TCP port 5986.")));

        var result = await probe.ProbeAsync(CreateProbeRequest("PC001"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.TransportUnavailable, result.FailureCategory);
        Assert.Contains("PSRP WinRM ports are unreachable", result.FailureMessage);
        Assert.NotNull(result.Metadata);
        Assert.Equal("port", result.Metadata["stage"]);
        Assert.Equal("5985,5986", result.Metadata["attemptedPorts"]);
    }

    [Fact]
    public async Task PsrpExecutorReturnsStructuredNotImplementedFailure()
    {
        using var script = TemporaryScript.Create();
        var executor = new PsrpScriptExecutor(new StubCommandClient(PsrpCommandResult.Success(0, string.Empty, string.Empty)));
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
        Assert.Equal("PSRP script execution is not implemented in this slice.", result.FailureMessage);
        Assert.NotNull(result.Metadata);
        Assert.Equal("psrp", result.Metadata["transport"]);
        Assert.Equal("script-not-implemented", result.Metadata["mode"]);
    }

    [Fact]
    public async Task PsrpExecutorExecutesCommandPayloadThroughCommandClient()
    {
        var executor = new PsrpScriptExecutor(new StubCommandClient(
            PsrpCommandResult.Success(
                exitCode: 0,
                stdout: "scf\\kmaclachlan1125\r\n",
                stderr: string.Empty,
                metadata: new Dictionary<string, string>
                {
                    ["scheme"] = "http",
                    ["port"] = "5985"
                })));
        var request = CreateCommandExecutionRequest("whoami", "cmd");

        var result = await executor.ExecuteScriptAsync(request, CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.Null(result.FailureMessage);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("scf\\kmaclachlan1125\r\n", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.NotNull(result.Metadata);
        Assert.Equal("executed", result.Metadata["mode"]);
        Assert.Equal("completed", result.Metadata["executionStatus"]);
        Assert.Equal("cmd", result.Metadata["commandShell"]);
        Assert.Equal("cmd.exe /c whoami", result.Metadata["executionCommand"]);
        Assert.Equal("cmd.exe", result.Metadata["executable"]);
        Assert.Equal("5985", result.Metadata["port"]);
        Assert.Equal("http", result.Metadata["scheme"]);
    }

    [Fact]
    public async Task PsrpExecutorMapsUnexpectedExitCodeForCommandPayload()
    {
        var executor = new PsrpScriptExecutor(new StubCommandClient(
            PsrpCommandResult.Success(
                exitCode: 5,
                stdout: string.Empty,
                stderr: "nope")));
        var request = CreateCommandExecutionRequest("whoami", "cmd");

        var result = await executor.ExecuteScriptAsync(request, CancellationToken.None);

        Assert.Equal(FailureCategory.UnexpectedExitCode, result.FailureCategory);
        Assert.Contains("expected 0", result.FailureMessage);
        Assert.Equal(5, result.ExitCode);
    }

    [Fact]
    public void PsrpFailureClassifierMapsAccessDeniedToAuthorizationFailed()
    {
        var category = PsrpFailureClassifier.Classify(
            "Connecting to remote server 82H9704 failed with the following error message : Access is denied.");

        Assert.Equal(FailureCategory.AuthorizationFailed, category);
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

    private static TransportScriptExecutionRequest CreateCommandExecutionRequest(
        string commandLine,
        string shell,
        string? workingDirectory = null)
    {
        var target = new TargetExecution(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: @"C:\Dispatch\Tests\run-001\Targets\PC001",
            PlannedLocalResultPath: @"C:\Dispatch\Tests\run-001\Targets\PC001\result.json",
            PlannedRemoteScriptPath: null,
            PlannedCommand: new DirectExecutionCommand("cmd.exe", ["/c", commandLine]));
        var plan = new ExecutionPlan(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-17T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec("PC001")],
                Payload: new CommandPayload(commandLine, shell, workingDirectory),
                Transport: TransportKind.Psrp,
                ExecutionContext: new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", false),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy([]),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Tests\run-001")),
            Targets: [target],
            DryRun: false);
        var preparation = new TargetScriptPreparationResult(
            Target: target.Target,
            RemoteScriptPath: string.Empty,
            AdminShareScriptPath: null,
            Succeeded: true);

        return new TransportScriptExecutionRequest(plan, target, preparation);
    }

    private static TransportEndpointProbeRequest CreateProbeRequest(string targetName) =>
        new(
            CreatePlan(@"C:\Dispatch\Tests\Fix.ps1", TransportKind.Psrp),
            new TargetExecution(
                RunId: "run-001",
                Target: new TargetSpec(targetName),
                State: TargetExecutionState.Pending,
                PlannedLocalTargetRoot: $@"C:\Dispatch\Tests\run-001\Targets\{targetName}",
                PlannedLocalResultPath: $@"C:\Dispatch\Tests\run-001\Targets\{targetName}\result.json",
                PlannedRemoteScriptPath: null,
                PlannedCommand: null));

    private sealed class StubDnsResolver(PsrpProbeResult result) : IPsrpDnsResolver
    {
        public Task<PsrpProbeResult> ResolveAsync(string target, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class StubPortProbe(PsrpProbeResult httpResult, PsrpProbeResult httpsResult) : IPsrpPortProbe
    {
        public Task<PsrpProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken) =>
            Task.FromResult(port == 5985 ? httpResult : httpsResult);
    }

    private sealed class StubCommandClient(PsrpCommandResult result) : IPsrpCommandClient
    {
        public Task<PsrpCommandResult> ExecuteAsync(PsrpCommandRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }
}
