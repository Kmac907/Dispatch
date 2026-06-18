using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Dispatch.Transports.Psrp;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Dispatch.Core.Tests;

[SupportedOSPlatform("windows")]
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
    public async Task PsrpExecutorExecutesScriptPayloadThroughScriptClient()
    {
        using var script = TemporaryScript.Create();
        var executor = new PsrpScriptExecutor(
            new StubCommandClient(PsrpCommandResult.Success(0, string.Empty, string.Empty)),
            new StubScriptClient(
                PsrpCommandResult.Success(
                    exitCode: 0,
                    stdout: "script-ok\r\n",
                    stderr: string.Empty,
                    metadata: new Dictionary<string, string>
                    {
                        ["authentication"] = "negotiate",
                        ["connectionKind"] = "wsman",
                        ["configurationName"] = "PowerShell.7",
                        ["scheme"] = "http",
                        ["port"] = "5985"
                    })));
        var request = new TransportScriptExecutionRequest(
            CreatePlan(
                script.Path,
                TransportKind.Psrp,
                new ExecutionContextOptions(
                    PsrpConfigurationName: "PowerShell.7",
                    PsrpAuthentication: PsrpAuthenticationKind.Negotiate)),
            CreateTargetExecution(),
            new TargetScriptPreparationResult(
                Target: new TargetSpec("PC001"),
                RemoteScriptPath: @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1",
                AdminShareScriptPath: null,
                Succeeded: true));

        var result = await executor.ExecuteScriptAsync(request, CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.Null(result.FailureMessage);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("script-ok\r\n", result.Stdout);
        Assert.NotNull(result.Metadata);
        Assert.Equal("psrp", result.Metadata["transport"]);
        Assert.Equal("executed", result.Metadata["mode"]);
        Assert.Equal("powershell.exe", result.Metadata["executable"]);
        Assert.Equal("negotiate", result.Metadata["authentication"]);
        Assert.Equal("wsman", result.Metadata["connectionKind"]);
        Assert.Equal("PowerShell.7", result.Metadata["configurationName"]);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", result.Metadata["remoteScriptPath"]);
    }

    [Fact]
    public async Task PsrpExecutorExecutesCommandPayloadThroughCommandClient()
    {
        var commandClient = new StubCommandClient(
            PsrpCommandResult.Success(
                exitCode: 0,
                stdout: "scf\\kmaclachlan1125\r\n",
                stderr: string.Empty,
                metadata: new Dictionary<string, string>
                {
                    ["authentication"] = "negotiate",
                    ["connectionKind"] = "wsman",
                    ["configurationName"] = "PowerShell.7",
                    ["scheme"] = "http",
                    ["port"] = "5985"
                }));
        var executor = new PsrpScriptExecutor(commandClient,
            new StubScriptClient(PsrpCommandResult.Success(0, string.Empty, string.Empty)));
        var request = CreateCommandExecutionRequest(
            "whoami",
            "cmd",
            configurationName: "PowerShell.7",
            authenticationKind: PsrpAuthenticationKind.Negotiate);

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
        Assert.Equal("negotiate", result.Metadata["authentication"]);
        Assert.Equal("wsman", result.Metadata["connectionKind"]);
        Assert.Equal("PowerShell.7", result.Metadata["configurationName"]);
        Assert.NotNull(commandClient.LastRequest);
        Assert.Equal("PowerShell.7", commandClient.LastRequest!.ConfigurationName);
        Assert.Equal(PsrpConnectionKind.WsMan, commandClient.LastRequest.ConnectionKind);
        Assert.Equal(PsrpAuthenticationKind.Negotiate, commandClient.LastRequest.AuthenticationKind);
    }

    [Fact]
    public void PsrpCommandClientBuildsShellUriFromConfigurationName()
    {
        Assert.Equal(
            "http://schemas.microsoft.com/powershell/PowerShell.7",
            PsrpCommandClient.BuildShellUri("PowerShell.7"));
        Assert.Equal(
            "http://schemas.microsoft.com/powershell/Microsoft.PowerShell",
            PsrpCommandClient.BuildShellUri(PsrpCommandClient.DefaultConfigurationName));
    }

    [Fact]
    public void PsrpCommandClientMapsSupportedAuthenticationKinds()
    {
        Assert.Equal(AuthenticationMechanism.Default, PsrpCommandClient.MapAuthenticationMechanism(PsrpAuthenticationKind.Default));
        Assert.Equal(AuthenticationMechanism.Negotiate, PsrpCommandClient.MapAuthenticationMechanism(PsrpAuthenticationKind.Negotiate));
    }

    [Fact]
    public void PsrpPowerShellStreamMapperCapturesCurrentPowerShellStreams()
    {
        using var powerShell = PowerShell.Create();
        powerShell.Streams.Warning.Add(new WarningRecord("warn"));
        powerShell.Streams.Verbose.Add(new VerboseRecord("verbose"));
        powerShell.Streams.Debug.Add(new DebugRecord("debug"));
        powerShell.Streams.Information.Add(new InformationRecord("info", "Dispatch"));
        powerShell.Streams.Error.Add(new ErrorRecord(
            new InvalidOperationException("boom"),
            "Dispatch.StreamFailure",
            ErrorCategory.InvalidOperation,
            null));

        var streams = PsrpPowerShellStreamMapper.Capture(powerShell.Streams);

        Assert.NotNull(streams);
        Assert.Collection(
            streams!,
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Error, stream.Stream);
                Assert.Contains("boom", stream.Message);
            },
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Warning, stream.Stream);
                Assert.Equal("warn", stream.Message);
            },
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Verbose, stream.Stream);
                Assert.Equal("verbose", stream.Message);
            },
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Debug, stream.Stream);
                Assert.Equal("debug", stream.Message);
            },
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Information, stream.Stream);
                Assert.Equal("info", stream.Message);
            });
    }

    [Fact]
    public async Task PsrpExecutorMapsUnexpectedExitCodeForCommandPayload()
    {
        var executor = new PsrpScriptExecutor(new StubCommandClient(
            PsrpCommandResult.Success(
                exitCode: 5,
                stdout: string.Empty,
                stderr: "nope")),
            new StubScriptClient(PsrpCommandResult.Success(0, string.Empty, string.Empty)));
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

    [Fact]
    public async Task PsrpArtifactCollectorUsesDefaultFoldersAndCollectsArtifacts()
    {
        using var targetRoot = TemporaryDirectory.Create();
        var client = new RecordingArtifactClient(request =>
            request.RemoteFolder.EndsWith(@"\logs", StringComparison.OrdinalIgnoreCase)
                ? PsrpArtifactDownloadResult.Success(Convert.FromBase64String(CreateZipBase64(("install.log", "ok"))))
                : PsrpArtifactDownloadResult.Missing());
        var collector = new PsrpArtifactCollector(client);
        var target = CreateArtifactTargetExecution(targetRoot.Path);
        var plan = CreateArtifactPlan(targetRoot.Path, target, new ArtifactPolicy());

        var result = await collector.CollectAsync(plan, target, CancellationToken.None);

        Assert.Equal("collected", result.Status);
        Assert.Equal([Path.Combine("logs", "install.log")], result.Artifacts);
        Assert.Equal(
            [
                @"C:\ProgramData\Dispatch\Runs\run-001\logs",
                @"C:\ProgramData\Dispatch\Runs\run-001\artifacts"
            ],
            client.Requests.Select(static request => request.RemoteFolder));
        Assert.True(File.Exists(Path.Combine(targetRoot.Path, "logs", "install.log")));
    }

    [Fact]
    public async Task PsrpArtifactCollectorUsesDeclaredFoldersAndReturnsNotFound()
    {
        using var targetRoot = TemporaryDirectory.Create();
        var client = new RecordingArtifactClient(_ => PsrpArtifactDownloadResult.Missing());
        var collector = new PsrpArtifactCollector(client);
        var target = CreateArtifactTargetExecution(targetRoot.Path);
        var plan = CreateArtifactPlan(targetRoot.Path, target, new ArtifactPolicy(["custom", @"reports\daily"]));

        var result = await collector.CollectAsync(plan, target, CancellationToken.None);

        Assert.Equal("not-found", result.Status);
        Assert.Empty(result.Artifacts);
        Assert.Equal(
            [
                @"C:\ProgramData\Dispatch\Runs\run-001\custom",
                @"C:\ProgramData\Dispatch\Runs\run-001\reports\daily"
            ],
            client.Requests.Select(static request => request.RemoteFolder));
    }

    [Fact]
    public async Task PsrpArtifactCollectorReturnsFailedWhenDownloadFails()
    {
        using var targetRoot = TemporaryDirectory.Create();
        var client = new RecordingArtifactClient(_ => PsrpArtifactDownloadResult.Failed("no access"));
        var collector = new PsrpArtifactCollector(client);
        var target = CreateArtifactTargetExecution(targetRoot.Path);
        var plan = CreateArtifactPlan(targetRoot.Path, target, new ArtifactPolicy(["logs"]));

        var result = await collector.CollectAsync(plan, target, CancellationToken.None);

        Assert.Equal("failed", result.Status);
        Assert.Empty(result.Artifacts);
        Assert.Equal("no access", result.FailureMessage);
    }

    [Fact]
    public async Task PsrpArtifactCollectorPassesConfiguredConfigurationNameToArtifactClient()
    {
        using var targetRoot = TemporaryDirectory.Create();
        var client = new RecordingArtifactClient(_ => PsrpArtifactDownloadResult.Missing());
        var collector = new PsrpArtifactCollector(client);
        var target = CreateArtifactTargetExecution(targetRoot.Path);
        var plan = CreateArtifactPlan(
            targetRoot.Path,
            target,
            new ArtifactPolicy(["logs"]),
            new ExecutionContextOptions(
                PsrpConfigurationName: "PowerShell.7",
                PsrpAuthentication: PsrpAuthenticationKind.Negotiate));

        await collector.CollectAsync(plan, target, CancellationToken.None);

        var request = Assert.Single(client.Requests);
        Assert.Equal("PowerShell.7", request.ConfigurationName);
        Assert.Equal(PsrpConnectionKind.WsMan, request.ConnectionKind);
        Assert.Equal(PsrpAuthenticationKind.Negotiate, request.AuthenticationKind);
    }

    [Fact]
    public async Task PsrpArtifactCollectorReportsMeasuredDownloadProgressWhenArchiveSizeIsKnown()
    {
        using var targetRoot = TemporaryDirectory.Create();
        var zipBytes = Convert.FromBase64String(CreateZipBase64(("install.log", "ok")));
        var progress = new List<DispatchExecutionProgress>();
        var client = new RecordingArtifactClient(request =>
        {
            request.ProgressReporter?.Invoke(new PsrpArtifactProgress(zipBytes.Length / 2, zipBytes.Length));
            request.ProgressReporter?.Invoke(new PsrpArtifactProgress(zipBytes.Length, zipBytes.Length));
            return PsrpArtifactDownloadResult.Success(zipBytes);
        });
        var collector = new PsrpArtifactCollector(client);
        var target = CreateArtifactTargetExecution(targetRoot.Path);
        var plan = CreateArtifactPlan(targetRoot.Path, target, new ArtifactPolicy(["logs"]));

        var result = await collector.CollectAsync(plan, target, CancellationToken.None, progress.Add);

        Assert.Equal("collected", result.Status);
        Assert.Contains(progress, item =>
            item.Target == "PC001"
            && item.State == TargetExecutionState.CollectingArtifacts
            && item.Details is
            {
                Operation: "artifact-download",
                CompletedBytes: > 0,
                TotalBytes: > 0
            });
    }

    [Fact]
    public async Task ExecutorCollectsArtifactsOverPsrpWhenCollectorIsRegisteredInDi()
    {
        using var outputRoot = TemporaryDirectory.Create();
        var artifactZip = Convert.FromBase64String(CreateZipBase64(("summary.json", "{\"ok\":true}")));
        using var provider = BuildProvider(
            outputRoot.Path,
            commandClient: new StubCommandClient(PsrpCommandResult.Success(0, "scf\\admin\r\n", string.Empty)),
            artifactClient: new RecordingArtifactClient(request =>
            {
                request.ProgressReporter?.Invoke(new PsrpArtifactProgress(artifactZip.Length, artifactZip.Length));
                return request.RemoteFolder.EndsWith(@"\logs", StringComparison.OrdinalIgnoreCase)
                    ? PsrpArtifactDownloadResult.Missing()
                    : PsrpArtifactDownloadResult.Success(artifactZip);
            }));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var observer = new RecordingExecutionObserver();
        var request = new DispatchRequest(
            payload: new CommandPayload("whoami", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.Psrp,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, observer, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal("collected", target.ArtifactCollectionStatus);
        Assert.Equal([Path.Combine("artifacts", "summary.json")], target.Artifacts);
        Assert.Contains(observer.Progress, item =>
            item.Target == "PC001"
            && item.State == TargetExecutionState.CollectingArtifacts
            && item.Details is { Operation: "artifact-download", TotalBytes: > 0 });
        Assert.True(File.Exists(Path.Combine(outputRoot.Path, "run-001", "Targets", "PC001", "artifacts", "summary.json")));
    }

    [Fact]
    public async Task ExecutorPropagatesPsrpPowerShellStreamsIntoTargetResult()
    {
        using var outputRoot = TemporaryDirectory.Create();
        using var provider = BuildProvider(
            outputRoot.Path,
            commandClient: new StubCommandClient(
                PsrpCommandResult.Success(
                    0,
                    "ok\r\n",
                    string.Empty,
                    streamRecords:
                    [
                        new PowerShellStreamRecord(PowerShellStreamKind.Warning, "warn"),
                        new PowerShellStreamRecord(PowerShellStreamKind.Information, "info")
                    ])));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new CommandPayload("whoami", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.Psrp,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.NotNull(target.StreamRecords);
        Assert.Collection(
            target.StreamRecords!,
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Warning, stream.Stream);
                Assert.Equal("warn", stream.Message);
            },
            stream =>
            {
                Assert.Equal(PowerShellStreamKind.Information, stream.Stream);
                Assert.Equal("info", stream.Message);
            });
    }

    private static ExecutionPlan CreatePlan(
        string scriptPath,
        TransportKind transport,
        ExecutionContextOptions? executionContext = null) =>
        new(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-17T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec("PC001")],
                Payload: new ScriptPayload(scriptPath, []),
                Transport: transport,
                ExecutionContext: executionContext ?? new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", false),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy([]),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Tests\run-001")),
            Targets: [CreateTargetExecution()],
            DryRun: false);

    private static ExecutionPlan CreateArtifactPlan(
        string targetRoot,
        TargetExecution target,
        ArtifactPolicy artifactPolicy,
        ExecutionContextOptions? executionContext = null) =>
        new(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-18T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [target.Target],
                Payload: new CommandPayload("whoami", "cmd", null),
                Transport: TransportKind.Psrp,
                ExecutionContext: executionContext ?? new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", false),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: artifactPolicy,
                ResultPolicy: new ResultPolicy(targetRoot)),
            Targets: [target],
            DryRun: false,
            RemoteRunRoot: @"C:\ProgramData\Dispatch\Runs\run-001");

    private static TargetExecution CreateTargetExecution() =>
        new(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: @"C:\Dispatch\Tests\run-001\Targets\PC001",
            PlannedLocalResultPath: @"C:\Dispatch\Tests\run-001\Targets\PC001\result.json",
            PlannedRemoteScriptPath: null,
            PlannedCommand: null);

    private static TargetExecution CreateArtifactTargetExecution(string targetRoot) =>
        new(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: targetRoot,
            PlannedLocalResultPath: Path.Combine(targetRoot, "result.json"),
            PlannedRemoteScriptPath: null,
            PlannedCommand: null);

    private static TransportScriptExecutionRequest CreateCommandExecutionRequest(
        string commandLine,
        string shell,
        string? workingDirectory = null,
        string? configurationName = null,
        PsrpConnectionKind connectionKind = PsrpConnectionKind.WsMan,
        PsrpAuthenticationKind authenticationKind = PsrpAuthenticationKind.Default)
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
                ExecutionContext: new ExecutionContextOptions(
                    PsrpConfigurationName: configurationName,
                    PsrpConnectionKind: connectionKind,
                    PsrpAuthentication: authenticationKind),
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

    private static ServiceProvider BuildProvider(
        string localRunRoot,
        IPsrpCommandClient? commandClient = null,
        IPsrpScriptClient? scriptClient = null,
        IPsrpArtifactClient? artifactClient = null,
        IPsrpDnsResolver? dnsResolver = null,
        IPsrpPortProbe? portProbe = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:LocalRunRoot"] = localRunRoot,
                ["Dispatch:RemoteRunRoot"] = @"C:\ProgramData\Dispatch\Runs",
                ["Dispatch:ExpectedExitCodes:0"] = "0"
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging()
            .AddDispatchCore(configuration)
            .AddDispatchPsrpTransport();

        services.Replace(ServiceDescriptor.Singleton<IRunIdGenerator>(new FixedRunIdGenerator("run-001")));
        services.Replace(ServiceDescriptor.Singleton<ISystemClock>(new FixedSystemClock(new DateTimeOffset(2026, 06, 18, 12, 0, 0, TimeSpan.Zero))));
        services.Replace(ServiceDescriptor.Singleton<IPsrpDnsResolver>(dnsResolver ?? new StubDnsResolver(PsrpProbeResult.Success)));
        services.Replace(ServiceDescriptor.Singleton<IPsrpPortProbe>(portProbe ?? new StubPortProbe(PsrpProbeResult.Success, PsrpProbeResult.Success)));
        services.Replace(ServiceDescriptor.Singleton<IPsrpCommandClient>(commandClient ?? new StubCommandClient(PsrpCommandResult.Success(0, string.Empty, string.Empty))));
        services.Replace(ServiceDescriptor.Singleton<IPsrpScriptClient>(scriptClient ?? new StubScriptClient(PsrpCommandResult.Success(0, string.Empty, string.Empty))));
        services.Replace(ServiceDescriptor.Singleton<IPsrpArtifactClient>(artifactClient ?? new RecordingArtifactClient(_ => PsrpArtifactDownloadResult.Missing())));

        return services.BuildServiceProvider(validateScopes: true);
    }

    private static string CreateZipBase64(params (string RelativePath, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (relativePath, content) in entries)
            {
                var entry = archive.CreateEntry(relativePath.Replace('\\', '/'));
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, 1024, leaveOpen: false);
                writer.Write(content);
            }
        }

        return Convert.ToBase64String(stream.ToArray());
    }

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
        public PsrpCommandRequest? LastRequest { get; private set; }

        public Task<PsrpCommandResult> ExecuteAsync(PsrpCommandRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class StubScriptClient(PsrpCommandResult result) : IPsrpScriptClient
    {
        public Task<PsrpCommandResult> ExecuteAsync(PsrpScriptRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class RecordingArtifactClient(Func<PsrpArtifactRequest, PsrpArtifactDownloadResult> resultFactory) : IPsrpArtifactClient
    {
        public List<PsrpArtifactRequest> Requests { get; } = [];

        public Task<PsrpArtifactDownloadResult> DownloadFolderAsync(PsrpArtifactRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(resultFactory(request));
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

    private sealed class FixedRunIdGenerator(string runId) : IRunIdGenerator
    {
        public string CreateRunId() => runId;
    }

    private sealed class FixedSystemClock(DateTimeOffset utcNow) : ISystemClock
    {
        public DateTimeOffset UtcNow => utcNow;
    }
}
