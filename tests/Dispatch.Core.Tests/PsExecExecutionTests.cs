using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Dispatch.Transports.PsExec;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Core.Tests;

public sealed class PsExecExecutionTests
{
    [Fact]
    public async Task CommandBuilderBuildsRunAsSystemArgumentArrayWithoutCredentialFlags()
    {
        using var script = TemporaryScript.Create("Fix With Space.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        using var provider = BuildProvider(outputRoot.Path, runner, psexecPath: @"C:\Tools\PsExec.exe");
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var builder = provider.GetRequiredService<PsExecCommandBuilder>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-Mode", "Repair Now"]),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path,
            executionContext: new ExecutionContextOptions(RunAsSystem: true));

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var target = Assert.Single(plan.Targets);
        var preparation = await provider.GetRequiredService<IScriptPreparationService>().PrepareAsync(plan, CancellationToken.None);
        var preparedTarget = Assert.Single(preparation.Targets);
        var command = builder.Build(new(plan, target, preparedTarget));

        Assert.Equal(@"C:\Tools\PsExec.exe", command.Executable);
        Assert.Equal(
            [@"\\PC001", "-s", "-h", "powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix With Space.ps1", "-Mode", "Repair Now"],
            command.Arguments);
        Assert.DoesNotContain("-u", command.Arguments);
        Assert.DoesNotContain("-p", command.Arguments);
        Assert.Contains(@"C:\Tools\PsExec.exe", command.RenderedCommand);
        Assert.Contains("\"Repair Now\"", command.RenderedCommand);
    }

    [Fact]
    public async Task ExecutorRunsPreparedScriptThroughPsExecAndClassifiesExpectedExitCode()
    {
        using var script = TemporaryScript.Create("Install.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(3010, stdout: "reboot required");
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(outputRoot.Path, runner, endpointFileSystem: endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-Mode", "Install"]),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            expectedExitCodes: [0, 3010],
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(3010, target.ExitCode);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Equal("not-supported", target.SecretHandoffStatus);
        Assert.Equal("not-started", target.CleanupStatus);
        var command = Assert.Single(runner.Commands);
        Assert.DoesNotContain("-u", command.Arguments);
        Assert.DoesNotContain("-p", command.Arguments);
        Assert.Contains(@"\\PC001", command.Arguments);
        Assert.Single(endpointFileSystem.Copies);
    }

    [Fact]
    public async Task ExecutorSkipsScriptTransferWhenDnsProbeFails()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(
            outputRoot.Path,
            runner,
            endpointFileSystem: endpointFileSystem,
            dnsResolver: new RecordingDnsResolver(PsExecProbeResult.Failed("DNS failure.")));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("MissingHost")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.ProbeFailed, target.FailureCategory);
        Assert.Contains("DNS failure", target.FailureMessage);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Empty(runner.Commands);
        Assert.Equal("dns", target.TransportMetadata?["stage"]);
    }

    [Fact]
    public async Task ExecutorSkipsScriptTransferWhenSmbProbeFails()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(
            outputRoot.Path,
            runner,
            endpointFileSystem: endpointFileSystem,
            portProbe: new RecordingPortProbe(PsExecProbeResult.Failed("Port closed.")));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.TransportUnavailable, target.FailureCategory);
        Assert.Contains("Port closed", target.FailureMessage);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Empty(runner.Commands);
        Assert.Equal("smb", target.TransportMetadata?["stage"]);
    }

    [Fact]
    public async Task ExecutorSkipsScriptTransferWhenAdminShareAuthorizationFails()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem();
        using var provider = BuildProvider(
            outputRoot.Path,
            runner,
            endpointFileSystem: endpointFileSystem,
            adminShareProbe: new RecordingAdminShareProbe(PsExecAdminShareProbeResult.Failed("Access denied.", PsExecAdminShareFailureKind.Authorization)));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.AuthorizationFailed, target.FailureCategory);
        Assert.Contains("Access denied", target.FailureMessage);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Empty(runner.Commands);
        Assert.Equal("admin-share", target.TransportMetadata?["stage"]);
    }

    [Fact]
    public async Task ExecutorClassifiesUnexpectedPsExecExitCode()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(5, stderr: "access denied");
        using var provider = BuildProvider(outputRoot.Path, runner);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            expectedExitCodes: [0],
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(5, target.ExitCode);
        Assert.Equal(FailureCategory.UnexpectedExitCode, target.FailureCategory);
        Assert.Equal(1, result.FailedCount);
        Assert.Contains("expected 0", target.FailureMessage);
    }

    [Fact]
    public async Task PsExecPortProbePropagatesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var probe = new PsExecPortProbe();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => probe.CanConnectAsync("127.0.0.1", 445, cancellation.Token));
    }

    private static ServiceProvider BuildProvider(
        string localRunRoot,
        RecordingPsExecProcessRunner runner,
        RecordingEndpointFileSystem? endpointFileSystem = null,
        string psexecPath = "psexec.exe",
        IPsExecDnsResolver? dnsResolver = null,
        IPsExecPortProbe? portProbe = null,
        IPsExecAdminShareProbe? adminShareProbe = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:LocalRunRoot"] = localRunRoot,
                ["Dispatch:RemoteRunRoot"] = @"C:\ProgramData\Dispatch\Runs",
                ["Dispatch:ExpectedExitCodes:0"] = "0",
                ["Dispatch:PsExecPath"] = psexecPath
            })
            .Build();

        return new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .AddDispatchPsExecTransport()
            .AddSingleton<IRunIdGenerator>(new FixedRunIdGenerator("run-001"))
            .AddSingleton<ISystemClock>(new FixedSystemClock(new DateTimeOffset(2026, 06, 13, 12, 0, 0, TimeSpan.Zero)))
            .AddSingleton<IEndpointFileSystem>(endpointFileSystem ?? new RecordingEndpointFileSystem())
            .AddSingleton<IPsExecDnsResolver>(dnsResolver ?? new RecordingDnsResolver(PsExecProbeResult.Success))
            .AddSingleton<IPsExecPortProbe>(portProbe ?? new RecordingPortProbe(PsExecProbeResult.Success))
            .AddSingleton<IPsExecAdminShareProbe>(adminShareProbe ?? new RecordingAdminShareProbe(PsExecAdminShareProbeResult.Success))
            .AddSingleton<IPsExecProcessRunner>(runner)
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
        public List<(string SourcePath, string DestinationPath)> Copies { get; } = [];

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            Copies.Add((sourcePath, destinationPath));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingPsExecProcessRunner(
        int exitCode,
        string stdout = "",
        string stderr = "") : IPsExecProcessRunner
    {
        public List<PsExecCommand> Commands { get; } = [];

        public Task<PsExecProcessResult> RunAsync(PsExecCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Task.FromResult(new PsExecProcessResult(
                exitCode,
                stdout,
                stderr,
                new DateTimeOffset(2026, 06, 13, 12, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 06, 13, 12, 0, 2, TimeSpan.Zero)));
        }
    }

    private sealed class RecordingDnsResolver(PsExecProbeResult result) : IPsExecDnsResolver
    {
        public Task<PsExecProbeResult> ResolveAsync(string target, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingPortProbe(PsExecProbeResult result) : IPsExecPortProbe
    {
        public Task<PsExecProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class RecordingAdminShareProbe(PsExecAdminShareProbeResult result) : IPsExecAdminShareProbe
    {
        public Task<PsExecAdminShareProbeResult> ProbeDirectoryAsync(string path, CancellationToken cancellationToken) => Task.FromResult(result);
    }
}
