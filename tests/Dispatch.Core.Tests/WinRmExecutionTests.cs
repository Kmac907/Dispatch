using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Dispatch.Core.Transports;
using Dispatch.Transports.WinRm;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;

namespace Dispatch.Core.Tests;

[SupportedOSPlatform("windows")]
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
    public async Task ExecutorExecutesUploadedScriptOverRawWinRmShell()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success(
            new Dictionary<string, string>
            {
                ["scheme"] = "https",
                ["port"] = "5986"
            }));
        var shellClient = new RecordingShellClient(
            new WinRmShellCommandResult(
                true,
                0,
                "audit complete",
                string.Empty,
                null,
                new Dictionary<string, string>
                {
                    ["shell"] = "command"
                }),
            WinRmShellCommandResult.SucceededResult(exitCode: 3),
            WinRmShellCommandResult.SucceededResult(exitCode: 3));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            shellClient,
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
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Null(target.FailureMessage);
        Assert.Equal("not-found", target.ArtifactCollectionStatus);
        Assert.Null(target.ArtifactCollectionFailureMessage);
        Assert.Empty(endpointFileSystem.CreatedDirectories);
        Assert.Empty(endpointFileSystem.Copies);
        Assert.Equal(0, target.ExitCode);
        Assert.Equal("winrm", target.TransportMetadata?["transport"]);
        Assert.Equal("executed", target.TransportMetadata?["mode"]);
        Assert.Equal("completed", target.TransportMetadata?["preparation"]);
        Assert.Equal("completed", target.TransportMetadata?["uploadStatus"]);
        Assert.Equal("completed", target.TransportMetadata?["executionStatus"]);
        Assert.Equal("powershell.exe", target.TransportMetadata?["executable"]);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", target.TransportMetadata?["plannedRemoteScriptPath"]);
        var scriptBytes = await File.ReadAllBytesAsync(script.Path);
        Assert.Equal("WinRmChunkedBase64", target.TransportMetadata?["transferMode"]);
        Assert.Equal(scriptBytes.Length.ToString(), target.TransportMetadata?["scriptByteLength"]);
        Assert.Equal(ComputeSha256(scriptBytes), target.TransportMetadata?["scriptSha256"]);
        Assert.Equal("8192", target.TransportMetadata?["chunkSizeBytes"]);
        Assert.Equal("1", target.TransportMetadata?["chunkCount"]);
        Assert.Equal("https", target.TransportMetadata?["scheme"]);
        Assert.Equal("5986", target.TransportMetadata?["port"]);
        Assert.Equal("command", target.TransportMetadata?["shell"]);
        Assert.NotNull(target.StdoutPath);
        Assert.NotNull(target.StderrPath);
        Assert.Equal("audit complete", await File.ReadAllTextAsync(target.StdoutPath));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(target.StderrPath));
        var uploadRequest = Assert.Single(transferClient.Requests);
        Assert.Equal("PC001", uploadRequest.Target);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", uploadRequest.RemoteScriptPath);
        Assert.Equal(1, uploadRequest.TransferPlan.ChunkCount);
        var shellRequest = shellClient.Requests[0];
        Assert.Equal("PC001", shellRequest.Target);
        Assert.Equal("powershell.exe", shellRequest.Executable);
        Assert.Equal(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", "-Mode", "Audit"],
            shellRequest.Arguments);
        Assert.Empty(shellRequest.StandardInputFrames);
        Assert.Equal(3, shellClient.Requests.Count);
        Assert.Equal(
            [
                TargetExecutionState.Probing,
                TargetExecutionState.PreparingScript,
                TargetExecutionState.Executing,
                TargetExecutionState.CollectingArtifacts,
                TargetExecutionState.Succeeded
            ],
            observer.Progress.Select(static progress => progress.State));
    }

    [Fact]
    public async Task ExecutorExecutesWinRmCommandPayloadWithoutUpload()
    {
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            new WinRmShellCommandResult(
                true,
                0,
                "scf\\admin",
                string.Empty,
                null,
                new Dictionary<string, string>
                {
                    ["shell"] = "command"
                }),
            WinRmShellCommandResult.SucceededResult(exitCode: 3),
            WinRmShellCommandResult.SucceededResult(exitCode: 3));
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem, transferClient, shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var observer = new RecordingExecutionObserver();
        var request = new DispatchRequest(
            payload: new CommandPayload("whoami", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, observer, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Null(plan.Targets[0].PlannedRemoteScriptPath);
        Assert.Equal(0, target.ExitCode);
        Assert.Equal("command", target.TransportMetadata?["payloadType"]);
        Assert.Equal("executed", target.TransportMetadata?["mode"]);
        Assert.Equal("not-required", target.TransportMetadata?["uploadStatus"]);
        Assert.Equal("NotRequired", target.TransportMetadata?["transferMode"]);
        Assert.Equal("cmd", target.TransportMetadata?["commandShell"]);
        Assert.Equal("cmd.exe", target.TransportMetadata?["executable"]);
        Assert.Equal("command", target.TransportMetadata?["shell"]);
        Assert.NotNull(target.StdoutPath);
        Assert.NotNull(target.StderrPath);
        Assert.Equal("scf\\admin", await File.ReadAllTextAsync(target.StdoutPath));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(target.StderrPath));
        Assert.Empty(transferClient.Requests);
        Assert.Equal(3, shellClient.Requests.Count);
        var shellRequest = shellClient.Requests[0];
        Assert.Equal("PC001", shellRequest.Target);
        Assert.Equal("cmd.exe", shellRequest.Executable);
        Assert.Equal(["/c", "whoami"], shellRequest.Arguments);
        Assert.Equal(
            [
                TargetExecutionState.Probing,
                TargetExecutionState.Executing,
                TargetExecutionState.CollectingArtifacts,
                TargetExecutionState.Succeeded
            ],
            observer.Progress.Select(static progress => progress.State));
    }

    [Fact]
    public async Task ExecutorMapsUnexpectedWinRmExitCodeToUnexpectedExitCode()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            new WinRmShellCommandResult(
                true,
                5,
                "partial output",
                "stderr line",
                null),
            WinRmShellCommandResult.SucceededResult(exitCode: 3),
            WinRmShellCommandResult.SucceededResult(exitCode: 3));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.UnexpectedExitCode, target.FailureCategory);
        Assert.Contains("expected 0", target.FailureMessage);
        Assert.Equal("executed", target.TransportMetadata?["mode"]);
        Assert.Equal("completed", target.TransportMetadata?["executionStatus"]);
        Assert.NotNull(target.StdoutPath);
        Assert.NotNull(target.StderrPath);
        Assert.Equal("partial output", await File.ReadAllTextAsync(target.StdoutPath));
        Assert.Equal("stderr line", await File.ReadAllTextAsync(target.StderrPath));
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

    [Fact]
    public async Task ExecutorMapsWinRmUploadFailureToScriptTransferFailed()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Failed(
            FailureCategory.ScriptTransferFailed,
            "Upload failed.",
            new Dictionary<string, string>
            {
                ["uploadStage"] = "shell"
            }));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            dnsResolver: new RecordingDnsResolver(WinRmProbeResult.Success),
            portProbe: new RecordingPortProbe((_, _) => WinRmProbeResult.Success));
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(FailureCategory.ScriptTransferFailed, target.FailureCategory);
        Assert.Contains("Upload failed", target.FailureMessage);
        Assert.Equal("shell", target.TransportMetadata?["uploadStage"]);
        Assert.Equal("upload-failed", target.TransportMetadata?["mode"]);
        Assert.Equal("completed", target.TransportMetadata?["preparation"]);
        Assert.Equal("failed", target.TransportMetadata?["uploadStatus"]);
    }

    [Fact]
    public async Task ExecutorMapsWinRmShellFailureToExecutionFailed()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            WinRmShellCommandResult.Failed(
                "Shell failed.",
                new Dictionary<string, string>
                {
                    ["shellStage"] = "invoke"
                }),
            WinRmShellCommandResult.SucceededResult(exitCode: 3),
            WinRmShellCommandResult.SucceededResult(exitCode: 3));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.ExecutionFailed, target.FailureCategory);
        Assert.Contains("Shell failed", target.FailureMessage);
        Assert.Equal("execution-failed", target.TransportMetadata?["mode"]);
        Assert.Equal("completed", target.TransportMetadata?["uploadStatus"]);
        Assert.Equal("failed", target.TransportMetadata?["executionStatus"]);
        Assert.Equal("invoke", target.TransportMetadata?["shellStage"]);
    }

    [Fact]
    public async Task ExecutorMapsWinRmAuthorizationFailureToAuthorizationFailed()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            WinRmShellCommandResult.Failed(
                "Access is denied.",
                new Dictionary<string, string>
                {
                    ["failureKind"] = "authorization"
                },
                FailureCategory.AuthorizationFailed),
            WinRmShellCommandResult.SucceededResult(exitCode: 3),
            WinRmShellCommandResult.SucceededResult(exitCode: 3));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Failed, target.State);
        Assert.Equal(FailureCategory.AuthorizationFailed, target.FailureCategory);
        Assert.Contains("Access is denied", target.FailureMessage);
        Assert.Equal("AuthorizationFailed", target.TransportMetadata?["failureCategory"]);
        Assert.Equal("authorization", target.TransportMetadata?["failureKind"]);
    }

    [Fact]
    public async Task ExecutorMapsWinRmShellTimeoutToTimedOut()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(WinRmShellCommandResult.TimedOutResult(
            "Shell timed out.",
            metadata: new Dictionary<string, string>
            {
                ["timeoutOrigin"] = "client"
            }));
        using var provider = BuildProvider(
            outputRoot.Path,
            endpointFileSystem,
            transferClient,
            shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.TimedOut, target.State);
        Assert.Equal(FailureCategory.TimedOut, target.FailureCategory);
        Assert.Contains("timed out", target.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("execution-timed-out", target.TransportMetadata?["mode"]);
        Assert.Equal("timed-out", target.TransportMetadata?["executionStatus"]);
        Assert.Equal("client", target.TransportMetadata?["timeoutOrigin"]);
    }

    [Fact]
    public async Task ExecutorCollectsWinRmArtifactFoldersOverRawShell()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            new WinRmShellCommandResult(true, 0, "artifact complete", string.Empty, null),
            new WinRmShellCommandResult(true, 0, CreateZipBase64(("install.log", "copied log")), string.Empty, null),
            new WinRmShellCommandResult(true, 0, CreateZipBase64((@"reports\summary.json", "{\"ok\":true}")), string.Empty, null));
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem, transferClient, shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal("collected", target.ArtifactCollectionStatus);
        Assert.Null(target.ArtifactCollectionFailureMessage);
        Assert.Equal(
            [@"logs\install.log", @"artifacts\reports\summary.json"],
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
    public async Task ExecutorTracksWinRmArtifactFailureSeparatelyFromScriptResult()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var endpointFileSystem = new RecordingEndpointFileSystem();
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(
            new WinRmShellCommandResult(true, 0, "artifact complete", string.Empty, null),
            WinRmShellCommandResult.Failed("artifact shell failed"));
        using var provider = BuildProvider(outputRoot.Path, endpointFileSystem, transferClient, shellClient);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var executor = provider.GetRequiredService<IDispatchExecutor>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: false,
            localRunRoot: outputRoot.Path,
            artifactPaths: ["logs"]);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var result = await executor.ExecuteAsync(plan, CancellationToken.None);

        var target = Assert.Single(result.Targets);
        Assert.Equal(TargetExecutionState.Succeeded, target.State);
        Assert.Equal(FailureCategory.None, target.FailureCategory);
        Assert.Equal("failed", target.ArtifactCollectionStatus);
        Assert.Contains("artifact", target.ArtifactCollectionFailureMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScriptExecutorReturnsSuccessTransportResultAfterUpload()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success(
            new Dictionary<string, string>
            {
                ["scheme"] = "https",
                ["port"] = "5986"
            }));
        var shellClient = new RecordingShellClient(new WinRmShellCommandResult(
            true,
            0,
            "audit complete",
            string.Empty,
            null));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(["-Mode", "Audit"]),
            CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.Null(result.FailureMessage);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("audit complete", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal("executed", result.Metadata?["mode"]);
        Assert.Equal("completed", result.Metadata?["uploadStatus"]);
        Assert.Equal("completed", result.Metadata?["executionStatus"]);
        Assert.Equal("https", result.Metadata?["scheme"]);
        Assert.Equal("5986", result.Metadata?["port"]);
        Assert.Equal(
            @"powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1 -Mode Audit",
            result.Metadata?["executionCommand"]);

        var uploadRequest = Assert.Single(transferClient.Requests);
        Assert.Equal("PC001", uploadRequest.Target);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", uploadRequest.RemoteScriptPath);

        var shellRequest = Assert.Single(shellClient.Requests);
        Assert.Equal("powershell.exe", shellRequest.Executable);
        Assert.Equal(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", "-Mode", "Audit"],
            shellRequest.Arguments);
        Assert.Empty(shellRequest.StandardInputFrames);
    }

    [Fact]
    public async Task ScriptExecutorExecutesCommandPayloadWithoutUploadPlan()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(new WinRmShellCommandResult(
            true,
            0,
            "scf\\admin",
            string.Empty,
            null));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateCommandExecutionRequest("whoami", "cmd"),
            CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("scf\\admin", result.Stdout);
        Assert.Equal("executed", result.Metadata?["mode"]);
        Assert.Equal("not-required", result.Metadata?["uploadStatus"]);
        Assert.Equal("NotRequired", result.Metadata?["transferMode"]);
        Assert.Equal("cmd", result.Metadata?["commandShell"]);
        Assert.Empty(transferClient.Requests);

        var shellRequest = Assert.Single(shellClient.Requests);
        Assert.Equal("cmd.exe", shellRequest.Executable);
        Assert.Equal(["/c", "whoami"], shellRequest.Arguments);
    }

    [Fact]
    public async Task ScriptExecutorMapsNonZeroExitCodeAtTransportResultLevel()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(new WinRmShellCommandResult(
            true,
            5,
            "partial output",
            "stderr line",
            null));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(),
            CancellationToken.None);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("partial output", result.Stdout);
        Assert.Equal("stderr line", result.Stderr);
        Assert.Equal(FailureCategory.UnexpectedExitCode, result.FailureCategory);
        Assert.Contains("expected 0", result.FailureMessage);
        Assert.Equal("executed", result.Metadata?["mode"]);
        Assert.Equal("completed", result.Metadata?["executionStatus"]);
    }

    [Fact]
    public async Task ScriptExecutorMapsShellFailureAtTransportResultLevel()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(WinRmShellCommandResult.Failed(
            "Shell failed.",
            new Dictionary<string, string>
            {
                ["shellStage"] = "invoke"
            }));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(),
            CancellationToken.None);

        Assert.Null(result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(FailureCategory.ExecutionFailed, result.FailureCategory);
        Assert.Equal("Shell failed.", result.FailureMessage);
        Assert.Equal("execution-failed", result.Metadata?["mode"]);
        Assert.Equal("completed", result.Metadata?["uploadStatus"]);
        Assert.Equal("failed", result.Metadata?["executionStatus"]);
        Assert.Equal("invoke", result.Metadata?["shellStage"]);
    }

    [Fact]
    public async Task ScriptExecutorMapsAuthorizationFailureAtTransportResultLevel()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(WinRmShellCommandResult.Failed(
            "Access is denied.",
            new Dictionary<string, string>
            {
                ["failureKind"] = "authorization"
            },
            FailureCategory.AuthorizationFailed));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(),
            CancellationToken.None);

        Assert.Equal(FailureCategory.AuthorizationFailed, result.FailureCategory);
        Assert.Equal("Access is denied.", result.FailureMessage);
        Assert.Equal("execution-failed", result.Metadata?["mode"]);
        Assert.Equal("AuthorizationFailed", result.Metadata?["failureCategory"]);
        Assert.Equal("authorization", result.Metadata?["failureKind"]);
    }

    [Fact]
    public async Task ScriptExecutorMapsShellTimeoutAtTransportResultLevel()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(WinRmShellCommandResult.TimedOutResult(
            "Shell timed out.",
            stdout: "partial output",
            metadata: new Dictionary<string, string>
            {
                ["timeoutOrigin"] = "client"
            }));
        var executor = new WinRmScriptExecutor(transferClient, shellClient);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(),
            CancellationToken.None);

        Assert.Null(result.ExitCode);
        Assert.Equal("partial output", result.Stdout);
        Assert.Equal(string.Empty, result.Stderr);
        Assert.Equal(FailureCategory.TimedOut, result.FailureCategory);
        Assert.Equal("Shell timed out.", result.FailureMessage);
        Assert.Equal("execution-timed-out", result.Metadata?["mode"]);
        Assert.Equal("timed-out", result.Metadata?["executionStatus"]);
        Assert.Equal("client", result.Metadata?["timeoutOrigin"]);
    }

    [Fact]
    public async Task ScriptExecutorPassesExecutionTimeoutToShellClient()
    {
        var transferClient = new RecordingScriptTransferClient(WinRmScriptTransferResult.Success());
        var shellClient = new RecordingShellClient(WinRmShellCommandResult.SucceededResult());
        var executor = new WinRmScriptExecutor(transferClient, shellClient);
        var expectedTimeout = TimeSpan.FromSeconds(12);

        var result = await executor.ExecuteScriptAsync(
            CreateScriptExecutionRequest(executionTimeout: expectedTimeout),
            CancellationToken.None);

        Assert.Equal(FailureCategory.None, result.FailureCategory);
        var shellRequest = Assert.Single(shellClient.Requests);
        Assert.Equal(expectedTimeout, shellRequest.ExecutionTimeout);
    }

    [Theory]
    [InlineData("Access is denied.", FailureCategory.AuthorizationFailed, "authorization")]
    [InlineData("The user name or password is incorrect.", FailureCategory.AuthenticationFailed, "authentication")]
    [InlineData("The client cannot connect to the destination specified in the request.", FailureCategory.TransportUnavailable, "transport")]
    public void FailureClassifierMapsKnownWinRmMessages(
        string message,
        FailureCategory expectedCategory,
        string expectedKind)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var category = WinRmFailureClassifier.Classify(message, metadata);

        Assert.Equal(expectedCategory, category);
        Assert.Equal(expectedKind, metadata["failureKind"]);
    }

    [Fact]
    public void FailureClassifierPrefersAuthorizationWhenMultipleAttemptFailuresExist()
    {
        var category = WinRmFailureClassifier.Choose(
            [FailureCategory.TransportUnavailable, FailureCategory.AuthorizationFailed]);

        Assert.Equal(FailureCategory.AuthorizationFailed, category);
    }

    [Fact]
    public void ShellResponseParserExtractsShellIdFromSelectorShape()
    {
        const string response = """
<s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"
            xmlns:w="http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd"
            xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing">
  <s:Body>
    <x:ResourceCreated xmlns:x="http://schemas.xmlsoap.org/ws/2004/09/transfer">
      <a:ReferenceParameters>
        <w:SelectorSet>
          <w:Selector Name="ShellId">00000000-0000-0000-0000-000000000123</w:Selector>
        </w:SelectorSet>
      </a:ReferenceParameters>
    </x:ResourceCreated>
  </s:Body>
</s:Envelope>
""";

        var extracted = WinRmShellResponseParser.TryExtractShellId(response, out var shellId, out var source);

        Assert.True(extracted);
        Assert.Equal("00000000-0000-0000-0000-000000000123", shellId);
        Assert.Equal("selector", source);
    }

    [Fact]
    public async Task ScriptTransferClientUploadsPreparedChunksAndValidatesReportedHash()
    {
        var shellClient = new RecordingShellClient(new WinRmShellCommandResult(
            true,
            0,
            "abcd1234\n",
            string.Empty,
            null,
            new Dictionary<string, string>
            {
                ["scheme"] = "https",
                ["port"] = "5986"
            }));
        var client = new WinRmScriptTransferClient(shellClient);
        var transferPlan = new ScriptTransferPlan(
            ScriptTransferMode.WinRmChunkedBase64,
            TotalBytes: 6,
            ContentSha256: "abcd1234",
            ChunkSizeBytes: 4,
            Chunks:
            [
                new ScriptTransferChunk(0, 0, 4, "hash-1", "QUJDRA=="),
                new ScriptTransferChunk(1, 4, 2, "hash-2", "RUY=")
            ]);

        var result = await client.UploadAsync(
            new WinRmScriptTransferRequest("PC001", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", transferPlan),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var request = Assert.Single(shellClient.Requests);
        Assert.Equal("PC001", request.Target);
        Assert.Equal("powershell.exe", request.Executable);
        Assert.Contains("-EncodedCommand", request.Arguments);
        Assert.Equal(2, request.StandardInputFrames.Count);
        Assert.Equal("QUJDRA==\n", Encoding.ASCII.GetString(request.StandardInputFrames[0]));
        Assert.Equal("RUY=\n", Encoding.ASCII.GetString(request.StandardInputFrames[1]));
        var encodedCommandIndex = request.Arguments.ToList().IndexOf("-EncodedCommand");
        var encodedCommand = request.Arguments[encodedCommandIndex + 1];
        var uploaderScript = Encoding.Unicode.GetString(Convert.FromBase64String(encodedCommand));
        Assert.Contains(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", uploaderScript, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", uploaderScript, StringComparison.Ordinal);
        Assert.Equal("completed", result.Metadata?["uploadStage"]);
        Assert.Equal("abcd1234", result.Metadata?["uploadExpectedSha256"]);
        Assert.Equal("abcd1234", result.Metadata?["uploadReportedSha256"]);
    }

    [Fact]
    public async Task ScriptTransferClientFailsWhenReportedHashDoesNotMatch()
    {
        var shellClient = new RecordingShellClient(new WinRmShellCommandResult(
            true,
            0,
            "wronghash\n",
            string.Empty,
            null));
        var client = new WinRmScriptTransferClient(shellClient);
        var transferPlan = new ScriptTransferPlan(
            ScriptTransferMode.WinRmChunkedBase64,
            TotalBytes: 4,
            ContentSha256: "expectedhash",
            ChunkSizeBytes: 4,
            Chunks:
            [
                new ScriptTransferChunk(0, 0, 4, "hash-1", "QUJDRA==")
            ]);

        var result = await client.UploadAsync(
            new WinRmScriptTransferRequest("PC001", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", transferPlan),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(FailureCategory.ScriptTransferFailed, result.FailureCategory);
        Assert.Equal("hash-verify", result.Metadata?["uploadStage"]);
        Assert.Equal("wronghash", result.Metadata?["uploadReportedSha256"]);
        Assert.Equal("expectedhash", result.Metadata?["uploadExpectedSha256"]);
    }

    [Fact]
    public async Task ScriptTransferClientReportsMeasuredChunkProgress()
    {
        var uploads = new List<WinRmUploadProgress>();
        var shellClient = new RecordingShellClient(
            request =>
            {
                request.ProgressReporter?.Invoke(new WinRmShellTransferProgress(
                    WinRmShellTransferKind.Input,
                    BytesTransferred: request.StandardInputFrames[0].Length,
                    TotalBytes: request.StandardInputFrames.Sum(static frame => (long)frame.Length),
                    FramesTransferred: 1,
                    TotalFrames: request.StandardInputFrames.Count));
                request.ProgressReporter?.Invoke(new WinRmShellTransferProgress(
                    WinRmShellTransferKind.Input,
                    BytesTransferred: request.StandardInputFrames.Sum(static frame => (long)frame.Length),
                    TotalBytes: request.StandardInputFrames.Sum(static frame => (long)frame.Length),
                    FramesTransferred: request.StandardInputFrames.Count,
                    TotalFrames: request.StandardInputFrames.Count));
            },
            new WinRmShellCommandResult(true, 0, "abcd1234\n", string.Empty, null));
        var client = new WinRmScriptTransferClient(shellClient);
        var transferPlan = new ScriptTransferPlan(
            ScriptTransferMode.WinRmChunkedBase64,
            TotalBytes: 6,
            ContentSha256: "abcd1234",
            ChunkSizeBytes: 4,
            Chunks:
            [
                new ScriptTransferChunk(0, 0, 4, "hash-1", "QUJDRA=="),
                new ScriptTransferChunk(1, 4, 2, "hash-2", "RUY=")
            ]);

        var result = await client.UploadAsync(
            new WinRmScriptTransferRequest(
                "PC001",
                @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1",
                transferPlan,
                uploads.Add),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Collection(
            uploads,
            progress =>
            {
                Assert.Equal(1, progress.ChunksUploaded);
                Assert.Equal(2, progress.TotalChunks);
                Assert.True(progress.BytesUploaded > 0);
                Assert.True(progress.TotalBytes >= progress.BytesUploaded);
            },
            progress =>
            {
                Assert.Equal(2, progress.ChunksUploaded);
                Assert.Equal(2, progress.TotalChunks);
                Assert.Equal(progress.TotalBytes, progress.BytesUploaded);
            });
    }

    [Fact]
    public async Task WinRmArtifactCollectorReportsMeasuredDownloadProgressWhenArchiveSizeIsKnown()
    {
        var remoteFolder = @"C:\ProgramData\Dispatch\Runs\run-001\logs";
        var zipBytes = Convert.FromBase64String(CreateZipBase64(("install.log", "ok")));
        var progress = new List<DispatchExecutionProgress>();
        var shellClient = new RecordingShellClient(
            request =>
            {
                request.ProgressReporter?.Invoke(new WinRmShellTransferProgress(
                    WinRmShellTransferKind.Error,
                    0,
                    TextChunk: $"DISPATCH_ARTIFACT_PROGRESS={zipBytes.Length / 2}/{zipBytes.Length}\n"));
                request.ProgressReporter?.Invoke(new WinRmShellTransferProgress(
                    WinRmShellTransferKind.Error,
                    0,
                    TextChunk: $"DISPATCH_ARTIFACT_PROGRESS={zipBytes.Length}/{zipBytes.Length}\n"));
            },
            WinRmShellCommandResult.SucceededResult(stdout: CreateZipBase64(("install.log", "ok"))));
        var collector = new WinRmArtifactCollector(shellClient);
        var targetRoot = Path.Combine(Path.GetTempPath(), $"dispatch-artifacts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetRoot);
        var target = new TargetExecution(
            "run-001",
            new TargetSpec("PC001"),
            TargetExecutionState.Pending,
            targetRoot,
            null,
            remoteFolder);
            var plan = new ExecutionPlan(
                RunId: "run-001",
                CreatedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
                Job: new DispatchJob(
                "run-001",
                [new TargetSpec("PC001")],
                new ScriptPayload(@"C:\Scripts\Fix.ps1", []),
                TransportKind.WinRm,
                new ExecutionContextOptions(),
                new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", true),
                new TimeoutPolicy(),
                new RetryPolicy(),
                [0],
                 new ArtifactPolicy(["logs"]),
                 new ResultPolicy(targetRoot)),
                Targets: [target],
                DryRun: false,
                RemoteRunRoot: @"C:\ProgramData\Dispatch\Runs\run-001");

        try
        {
            var result = await collector.CollectAsync(plan, target, CancellationToken.None, progress.Add);

            Assert.Equal("collected", result.Status);
            Assert.Contains(progress, item =>
                item.Details is
                {
                    Operation: "artifact-download",
                    CompletedBytes: > 0,
                    TotalBytes: > 0
                }
                && item.Target == "PC001"
                && item.State == TargetExecutionState.CollectingArtifacts);
        }
        finally
        {
            Directory.Delete(targetRoot, recursive: true);
        }
    }

    private static TransportScriptExecutionRequest CreateScriptExecutionRequest(
        IReadOnlyList<string>? scriptArguments = null,
        TimeSpan? executionTimeout = null)
    {
        var arguments = scriptArguments ?? [];
        var target = new TargetExecution(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: null,
            PlannedLocalResultPath: null,
            PlannedRemoteScriptPath: @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1",
            PlannedCommand: new DirectExecutionCommand(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", .. arguments]));
        var transferPlan = new ScriptTransferPlan(
            ScriptTransferMode.WinRmChunkedBase64,
            TotalBytes: 4,
            ContentSha256: "expectedhash",
            ChunkSizeBytes: 8192,
            Chunks:
            [
                new ScriptTransferChunk(0, 0, 4, "hash-1", "QUJDRA==")
            ]);
        var plan = new ExecutionPlan(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec("PC001")],
                Payload: new ScriptPayload(@"C:\Scripts\Fix.ps1", arguments),
                Transport: TransportKind.WinRm,
                ExecutionContext: new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", RequiresEndpointLocalScriptPath: true),
                TimeoutPolicy: new TimeoutPolicy(ExecutionTimeout: executionTimeout),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy(),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Runs\run-001")),
            Targets: [target],
            DryRun: false);
        var preparation = new TargetScriptPreparationResult(
            Target: target.Target,
            RemoteScriptPath: @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1",
            AdminShareScriptPath: null,
            Succeeded: true,
            TransferPlan: transferPlan);

        return new TransportScriptExecutionRequest(plan, target, preparation);
    }

    private static TransportScriptExecutionRequest CreateCommandExecutionRequest(
        string commandLine,
        string shell,
        string? workingDirectory = null)
    {
        var target = new TargetExecution(
            RunId: "run-001",
            Target: new TargetSpec("PC001"),
            State: TargetExecutionState.Pending,
            PlannedLocalTargetRoot: null,
            PlannedLocalResultPath: null,
            PlannedRemoteScriptPath: null,
            PlannedCommand: new DirectExecutionCommand(
                "cmd.exe",
                ["/c", workingDirectory is null ? commandLine : $"cd /d \"{workingDirectory}\" && {commandLine}"]));
        var plan = new ExecutionPlan(
            RunId: "run-001",
            CreatedAt: DateTimeOffset.Parse("2026-06-13T20:00:00Z"),
            Job: new DispatchJob(
                RunId: "run-001",
                Targets: [new TargetSpec("PC001")],
                Payload: new CommandPayload(commandLine, shell, workingDirectory),
                Transport: TransportKind.WinRm,
                ExecutionContext: new ExecutionContextOptions(),
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-001", RequiresEndpointLocalScriptPath: false),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: [0],
                ArtifactPolicy: new ArtifactPolicy(),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Runs\run-001")),
            Targets: [target],
            DryRun: false);
        var preparation = new TargetScriptPreparationResult(
            Target: target.Target,
            RemoteScriptPath: string.Empty,
            AdminShareScriptPath: null,
            Succeeded: true);

        return new TransportScriptExecutionRequest(plan, target, preparation);
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
        RecordingScriptTransferClient? transferClient = null,
        RecordingShellClient? shellClient = null,
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
            .AddSingleton<IWinRmScriptTransferClient>(transferClient ?? new RecordingScriptTransferClient(WinRmScriptTransferResult.Success()))
            .AddSingleton<IWinRmShellClient>(shellClient ?? new RecordingShellClient(new WinRmShellCommandResult(true, 0, string.Empty, string.Empty, null)))
            .BuildServiceProvider(validateScopes: true);
    }

    private static string ComputeSha256(byte[] content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

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

    private sealed class RecordingScriptTransferClient(WinRmScriptTransferResult result) : IWinRmScriptTransferClient
    {
        public List<WinRmScriptTransferRequest> Requests { get; } = [];

        public Task<WinRmScriptTransferResult> UploadAsync(WinRmScriptTransferRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(result);
        }
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

    private sealed class RecordingShellClient : IWinRmShellClient
    {
        public List<WinRmShellCommandRequest> Requests { get; } = [];
        private readonly Queue<WinRmShellCommandResult> results;
        private readonly Action<WinRmShellCommandRequest>? onExecute;

        public RecordingShellClient(params WinRmShellCommandResult[] results)
            : this(null, results)
        {
        }

        public RecordingShellClient(Action<WinRmShellCommandRequest>? onExecute, params WinRmShellCommandResult[] results)
        {
            this.onExecute = onExecute;
            this.results = new Queue<WinRmShellCommandResult>(results);
        }

        public Task<WinRmShellCommandResult> ExecuteAsync(WinRmShellCommandRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            onExecute?.Invoke(request);
            var result = results.Count > 0
                ? results.Dequeue()
                : WinRmShellCommandResult.SucceededResult(exitCode: 3);
            return Task.FromResult(result);
        }
    }
}
