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
    public async Task ExecutorReportsPerTargetStateTransitions()
    {
        using var script = TemporaryScript.Create("Install.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        using var provider = BuildProvider(outputRoot.Path, runner);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var observer = new RecordingExecutionObserver();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, observer, CancellationToken.None);

        Assert.Equal(TargetExecutionState.Succeeded, Assert.Single(result.Targets).State);
        Assert.Equal(
            [
                TargetExecutionState.Probing,
                TargetExecutionState.PreparingScript,
                TargetExecutionState.Executing,
                TargetExecutionState.CollectingArtifacts,
                TargetExecutionState.Succeeded
            ],
            observer.Progress.Select(static progress => progress.State));
        Assert.All(observer.Progress, static progress => Assert.Equal("PC001", progress.Target));
    }

    [Fact]
    public async Task ExecutorSkipsScriptTransferWhenDnsProbeFails()
    {
        using var script = TemporaryScript.Create("Fix-token=payload-secret.ps1");
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
    public async Task ExecutorRespectsThrottleAndWritesDurableResults()
    {
        using var script = TemporaryScript.Create("Batch.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new ConcurrentPsExecProcessRunner(command =>
        {
            var target = command.Arguments[0].TrimStart('\\');
            var exitCode = target.Equals("PC003", StringComparison.OrdinalIgnoreCase) ? 5 : 0;
            return new PsExecProcessResult(
                exitCode,
                $"stdout from {target}",
                $"stderr from {target}",
                new DateTimeOffset(2026, 06, 13, 12, 0, 1, TimeSpan.Zero),
                new DateTimeOffset(2026, 06, 13, 12, 0, 2, TimeSpan.Zero));
        });
        using var provider = BuildProvider(outputRoot.Path, runner);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets:
            [
                new TargetSpec("PC001"),
                new TargetSpec("PC002"),
                new TargetSpec("PC003"),
                new TargetSpec("PC004")
            ],
            transport: TransportKind.PsExec,
            throttle: 2,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        Assert.Equal(2, runner.MaxConcurrency);
        Assert.Equal(["PC001", "PC002", "PC003", "PC004"], result.Targets.Select(static target => target.Target));
        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(FailureCategory.UnexpectedExitCode, result.Targets.Single(static target => target.Target == "PC003").FailureCategory);

        Assert.True(File.Exists(plan.LocalResultsJsonPath));
        Assert.True(File.Exists(plan.LocalEventsNdjsonPath));
        Assert.False(File.Exists(plan.LocalResultsCsvPath));
        Assert.False(File.Exists(Path.Combine(plan.LocalAdminRoot, "dispatch.log")));

        var firstTarget = result.Targets.Single(static target => target.Target == "PC001");
        Assert.Equal(string.Empty, firstTarget.ResultPath);
        Assert.True(File.Exists(firstTarget.StdoutPath));
        Assert.True(File.Exists(firstTarget.StderrPath));
        Assert.Equal("stdout from PC001", await File.ReadAllTextAsync(firstTarget.StdoutPath));
        Assert.Equal("stderr from PC001", await File.ReadAllTextAsync(firstTarget.StderrPath));

        var resultsJson = await File.ReadAllTextAsync(plan.LocalResultsJsonPath);
        Assert.Contains("\"targetCount\": 4", resultsJson);
        Assert.Contains("\"failedCount\": 1", resultsJson);
        Assert.Contains("\"resultPath\": \"\"", resultsJson);

        var eventsNdjson = await File.ReadAllLinesAsync(plan.LocalEventsNdjsonPath);
        Assert.Contains(eventsNdjson, static line => line.Contains("\"type\":\"run.started\"", StringComparison.Ordinal));
        Assert.Contains(eventsNdjson, static line => line.Contains("\"type\":\"plan\"", StringComparison.Ordinal));
        Assert.Contains(eventsNdjson, static line => line.Contains("\"type\":\"execution.started\"", StringComparison.Ordinal));
        Assert.Contains(eventsNdjson, static line => line.Contains("\"type\":\"target.result\"", StringComparison.Ordinal));
        Assert.Contains(eventsNdjson, static line => line.Contains("\"type\":\"result\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecutorCopiesDefaultArtifactFoldersWhenPresent()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem();
        endpointFileSystem.AddRemoteDirectory(
            @"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\logs",
            ["install.log"]);
        endpointFileSystem.AddRemoteDirectory(
            @"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\artifacts",
            [@"reports\summary.json"]);
        using var provider = BuildProvider(outputRoot.Path, runner, endpointFileSystem: endpointFileSystem);
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
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal("collected", target.ArtifactCollectionStatus);
        Assert.Null(target.ArtifactCollectionFailureMessage);
        Assert.Equal(
            [
                @"logs\install.log",
                @"artifacts\reports\summary.json"
            ],
            target.Artifacts);
        Assert.True(File.Exists(Path.Combine(plan.Targets[0].PlannedLocalTargetRoot!, "logs", "install.log")));
        Assert.True(File.Exists(Path.Combine(plan.Targets[0].PlannedLocalTargetRoot!, "artifacts", "reports", "summary.json")));

        Assert.Equal(string.Empty, target.ResultPath);

        var resultsJson = await File.ReadAllTextAsync(plan.LocalResultsJsonPath);
        Assert.Contains("\"artifactCollectionStatus\": \"collected\"", resultsJson);
        Assert.Contains(@"logs\\install.log", resultsJson);
        Assert.Contains(@"artifacts\\reports\\summary.json", resultsJson);
    }

    [Fact]
    public async Task ExecutorWritesOptionalSummaryAndTargetFilesWhenEnabled()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        using var provider = BuildProvider(outputRoot.Path, runner);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var planWithOptionalFiles = plan with
        {
            Job = plan.Job with
            {
                ResultPolicy = new ResultPolicy(
                    outputRoot.Path,
                    WriteJson: true,
                    WriteCsv: true,
                    WritePerTargetJson: true,
                    WriteTextLog: true,
                    WriteEventStream: true)
            }
        };

        var result = await executor.ExecuteAsync(planWithOptionalFiles, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.True(File.Exists(planWithOptionalFiles.LocalResultsCsvPath));
        Assert.True(File.Exists(Path.Combine(planWithOptionalFiles.LocalAdminRoot, "dispatch.log")));
        Assert.True(File.Exists(target.ResultPath));
    }

    [Fact]
    public async Task ExecutorRedactsSecretLookingValuesFromDispatchOwnedDurableOutputs()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(
            exitCode: null,
            stdout: "raw stdout token=raw-stdout-token",
            stderr: "raw stderr password=raw-stderr-password",
            failureCategory: FailureCategory.AuthenticationFailed,
            failureMessage: "Transport failed with password=message-secret sig=message-signature SharedAccessSignature=message-shared");
        using var provider = BuildProvider(outputRoot.Path, runner);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var planWithOptionalFiles = plan with
        {
            Job = plan.Job with
            {
                ResultPolicy = new ResultPolicy(
                    outputRoot.Path,
                    WriteJson: true,
                    WriteCsv: true,
                    WritePerTargetJson: true,
                    WriteTextLog: true,
                    WriteEventStream: true)
            }
        };

        var result = await executor.ExecuteAsync(planWithOptionalFiles, CancellationToken.None);
        var target = Assert.Single(result.Targets);

        var dispatchOwnedOutputs = string.Join(
            Environment.NewLine,
            await File.ReadAllTextAsync(planWithOptionalFiles.LocalResultsJsonPath),
            await File.ReadAllTextAsync(planWithOptionalFiles.LocalResultsCsvPath),
            await File.ReadAllTextAsync(Path.Combine(planWithOptionalFiles.LocalAdminRoot, "dispatch.log")),
            await File.ReadAllTextAsync(planWithOptionalFiles.LocalEventsNdjsonPath),
            await File.ReadAllTextAsync(target.ResultPath));

        Assert.DoesNotContain("message-secret", dispatchOwnedOutputs);
        Assert.DoesNotContain("message-signature", dispatchOwnedOutputs);
        Assert.DoesNotContain("message-shared", dispatchOwnedOutputs);
        Assert.DoesNotContain("payload-secret", dispatchOwnedOutputs);
        Assert.Contains("password=[redacted]", dispatchOwnedOutputs);
        Assert.Contains("sig=[redacted]", dispatchOwnedOutputs);
        Assert.Contains("SharedAccessSignature=[redacted]", dispatchOwnedOutputs);
    }

    [Fact]
    public async Task ExecutorCopiesDeclaredArtifactFoldersWhenPresent()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem();
        endpointFileSystem.AddRemoteDirectory(
            @"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\custom\reports",
            ["summary.json"]);
        using var provider = BuildProvider(outputRoot.Path, runner, endpointFileSystem: endpointFileSystem);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path,
            artifactPaths: [@"custom\reports"]);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal("collected", target.ArtifactCollectionStatus);
        var artifact = Assert.Single(target.Artifacts ?? []);
        Assert.Equal(@"custom\reports\summary.json", artifact);
        Assert.True(File.Exists(Path.Combine(plan.Targets[0].PlannedLocalTargetRoot!, "custom", "reports", "summary.json")));
    }

    [Fact]
    public async Task ExecutorTreatsMissingArtifactFoldersAsNonFailure()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        using var provider = BuildProvider(outputRoot.Path, runner);
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
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Equal("not-found", target.ArtifactCollectionStatus);
        Assert.Empty(target.Artifacts ?? []);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task ExecutorTracksArtifactCopyFailureSeparatelyFromScriptResult()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runner = new RecordingPsExecProcessRunner(0);
        var endpointFileSystem = new RecordingEndpointFileSystem
        {
            ThrowOnCopyDirectoryContaining = @"\logs"
        };
        endpointFileSystem.AddRemoteDirectory(
            @"\\PC001\C$\ProgramData\Dispatch\Runs\run-001\logs",
            ["install.log"]);
        using var provider = BuildProvider(outputRoot.Path, runner, endpointFileSystem: endpointFileSystem);
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
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Equal("failed", target.ArtifactCollectionStatus);
        Assert.Contains("Artifact copy-back failed", target.ArtifactCollectionFailureMessage);
        Assert.Equal(0, result.FailedCount);
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
        IPsExecProcessRunner runner,
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

        private readonly Dictionary<string, IReadOnlyList<string>> remoteDirectories = new(StringComparer.OrdinalIgnoreCase);

        public string? ThrowOnCopyDirectoryContaining { get; init; }

        public void AddRemoteDirectory(string path, IReadOnlyList<string> relativeFiles)
        {
            remoteDirectories[path] = relativeFiles;
        }

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
        {
            Copies.Add((sourcePath, destinationPath));
            return Task.CompletedTask;
        }

        public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(remoteDirectories.ContainsKey(path));

        public Task<IReadOnlyList<string>> CopyDirectoryAsync(
            string sourcePath,
            string destinationPath,
            bool overwrite,
            CancellationToken cancellationToken)
        {
            if (ThrowOnCopyDirectoryContaining is not null
                && sourcePath.Contains(ThrowOnCopyDirectoryContaining, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Simulated artifact copy failure.");
            }

            if (!remoteDirectories.TryGetValue(sourcePath, out var relativeFiles))
            {
                return Task.FromResult<IReadOnlyList<string>>([]);
            }

            Directory.CreateDirectory(destinationPath);
            var copied = new List<string>();
            foreach (var relativeFile in relativeFiles)
            {
                var destinationFile = Path.Combine(destinationPath, relativeFile);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
                File.WriteAllText(destinationFile, $"copied from {sourcePath}");
                copied.Add(destinationFile);
            }

            return Task.FromResult<IReadOnlyList<string>>(copied);
        }
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

    private sealed class RecordingPsExecProcessRunner(
        int? exitCode,
        string stdout = "",
        string stderr = "",
        FailureCategory failureCategory = FailureCategory.None,
        string? failureMessage = null) : IPsExecProcessRunner
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
                new DateTimeOffset(2026, 06, 13, 12, 0, 2, TimeSpan.Zero),
                failureCategory,
                failureMessage));
        }
    }

    private sealed class ConcurrentPsExecProcessRunner(Func<PsExecCommand, PsExecProcessResult> resultFactory) : IPsExecProcessRunner
    {
        private int currentConcurrency;

        public int MaxConcurrency { get; private set; }

        public async Task<PsExecProcessResult> RunAsync(PsExecCommand command, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref currentConcurrency);
            MaxConcurrency = Math.Max(MaxConcurrency, current);
            try
            {
                await Task.Delay(50, cancellationToken);
                return resultFactory(command);
            }
            finally
            {
                Interlocked.Decrement(ref currentConcurrency);
            }
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
