using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Dispatch.Transports.WinRm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Core.Tests;

public sealed class WinRmExecutionTests
{
    [Fact]
    public async Task WinRmProbeReturnsProbeFailedWhenDnsResolutionFails()
    {
        var probe = new WinRmEndpointProbe(
            new RecordingDnsResolver(WinRmProbeResult.Failed("DNS failure.")),
            new RecordingPortProbe((_, _) => WinRmProbeResult.Success));

        var result = await probe.ProbeAsync(CreateProbeRequest("MissingHost"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.ProbeFailed, result.FailureCategory);
        Assert.Contains("DNS failure", result.FailureMessage);
        Assert.Equal("dns", result.Metadata?["stage"]);
    }

    [Fact]
    public async Task WinRmProbeReturnsTransportUnavailableWhenDefaultPortsAreUnreachable()
    {
        var probe = new WinRmEndpointProbe(
            new RecordingDnsResolver(WinRmProbeResult.Success),
            new RecordingPortProbe((target, port) => WinRmProbeResult.Failed($"Port {port} closed on {target}.")));

        var result = await probe.ProbeAsync(CreateProbeRequest("PC001"), CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.TransportUnavailable, result.FailureCategory);
        Assert.Contains("5985", result.FailureMessage);
        Assert.Contains("5986", result.FailureMessage);
        Assert.Equal("port", result.Metadata?["stage"]);
        Assert.Equal("5985,5986", result.Metadata?["attemptedPorts"]);
    }

    [Fact]
    public async Task WinRmProbeSucceedsWhenHttpsPortIsReachable()
    {
        var probe = new WinRmEndpointProbe(
            new RecordingDnsResolver(WinRmProbeResult.Success),
            new RecordingPortProbe((_, port) => port == 5986
                ? WinRmProbeResult.Success
                : WinRmProbeResult.Failed("HTTP blocked.")));

        var result = await probe.ProbeAsync(CreateProbeRequest("PC001"), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("5986", result.Metadata?["port"]);
        Assert.Equal("https", result.Metadata?["scheme"]);
    }

    [Fact]
    public async Task ExecutorReturnsExplicitNotImplementedFailureAfterSuccessfulWinRmProbe()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            dnsResolver: new RecordingDnsResolver(WinRmProbeResult.Success),
            portProbe: new RecordingPortProbe((_, port) => port == 5986
                ? WinRmProbeResult.Success
                : WinRmProbeResult.Failed("HTTP blocked.")));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var observer = new RecordingExecutionObserver();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-Mode", "Audit"]),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, observer, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.ExecutionFailed, target.FailureCategory);
        Assert.Contains("not implemented", target.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("skipped", target.ArtifactCollectionStatus);
        Assert.Contains("not implemented", target.ArtifactCollectionFailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(endpointFileSystem.CreatedDirectories);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Equal("winrm", target.TransportMetadata?["transport"]);
        Assert.Equal("probe-only", target.TransportMetadata?["mode"]);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", target.TransportMetadata?["plannedRemoteScriptPath"]);
        Assert.Equal(
            [
                TargetExecutionState.Probing,
                TargetExecutionState.PreparingScript,
                TargetExecutionState.Executing,
                TargetExecutionState.CollectingArtifacts,
                TargetExecutionState.Failed
            ],
            observer.Progress.Select(static progress => progress.State));
    }

    [Fact]
    public async Task ExecutorSkipsPreparationAndExecutionWhenWinRmProbeFails()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            dnsResolver: new RecordingDnsResolver(WinRmProbeResult.Failed("DNS failure.")));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("MissingHost")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(FailureCategory.ProbeFailed, target.FailureCategory);
        Assert.Empty(endpointFileSystem.CreatedDirectories);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Equal("dns", target.TransportMetadata?["stage"]);
    }

    private static TransportEndpointProbeRequest CreateProbeRequest(string targetName)
    {
        var target = new TargetExecution(
            RunId: "run-001",
            Target: new TargetSpec(targetName),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: null,
            PlannedLocalResultPath: null,
            PlannedRemoteScriptPath: @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1");
        var plan = new ExecutionPlan(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec(targetName)],
                Payload: new ScriptPayload(@"C:\Scripts\Fix.ps1", []),
                Transport: TransportKind.WinRm,
                ExecutionContext: new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", RequiresEndpointLocalScriptPath: true),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy(),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Runs\run-001")),
            Targets: [target],
            DryRun: true);

        return new TransportEndpointProbeRequest(plan, target);
    }

    private static ServiceProvider BuildProvider(
        string localRunRoot,
        RecordingEndpointFileSystem endpointFileSystem,
        IWinRmDnsResolver? dnsResolver = null,
        IWinRmPortProbe? portProbe = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:LocalRunRoot"] = localRunRoot,
                ["Dispatch:RemoteRunRoot"] = @"C:\ProgramData\Dispatch\Runs",
                ["Dispatch:ExpectedExitCodes:0"] = "0"
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .AddDispatchWinRmTransport()
            .AddSingleton<IRunIdGenerator>(new FixedRunIdGenerator("run-001"))
            .AddSingleton<ISystemClock>(new FixedSystemClock(new DateTimeOffset(2026, 06, 13, 12, 0, 0, TimeSpan.Zero)))
            .AddSingleton<IEndpointFileSystem>(endpointFileSystem)
            .AddSingleton<IWinRmDnsResolver>(dnsResolver ?? new RecordingDnsResolver(WinRmProbeResult.Success))
            .AddSingleton<IWinRmPortProbe>(portProbe ?? new RecordingPortProbe((_, _) => WinRmProbeResult.Success))
            .BuildServiceProvider(validateScopes: true);
    }

    private sealed class FixedRunIdGenerator(string runId) : IRunIdGenerator
    {
        public string CreateRunId() => runId;
    }

    private sealed class FixedSystemClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }

    private sealed class RecordingEndpointFileSystem : IEndpointFileSystem
    {
        public List<string> CreatedDirectories { get; } = [];

        public List<(string SourcePath, string DestinationPath)> Copies { get; } = [];

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            CreatedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            Copies.Add((sourcePath, destinationPath));
            return Task.CompletedTask;
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<IReadOnlyList<string>> CopyDirectoryAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private sealed class RecordingExecutionObserver : IDispatchExecutionObserver
    {
        public List<DispatchExecutionProgress> Progress { get; } = [];

        public Task OnProgressAsync(DispatchExecutionProgress progress, CancellationToken cancellationToken)
        {
            Progress.Add(progress);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingDnsResolver(WinRmProbeResult result) : IWinRmDnsResolver
    {
        public Task<WinRmProbeResult> ResolveAsync(string target, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingPortProbe(Func<string, int, WinRmProbeResult> resultFactory) : IWinRmPortProbe
    {
        public Task<WinRmProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken) =>
            Task.FromResult(resultFactory(target, port));
    }
}
