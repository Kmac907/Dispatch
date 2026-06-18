using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Hosting;
using Dispatch.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dispatch.Core.Tests;

public sealed class RequestPlanningTests
{
    [Fact]
    public async Task PlannerCreatesDryRunExecutionPlanWithLocalAndRemotePaths()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();

        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-Mode", "Repair"]),
            targets: [new TargetSpec("PC001", "computer-name")],
            transport: TransportKind.PsExec,
            expectedExitCodes: [0, 3010],
            throttle: 4,
            dryRun: true,
            localRunRoot: @"D:\Dispatch\Runs",
            remoteRunRoot: @"C:\ProgramData\Dispatch\Runs",
            artifactPaths: ["logs", @"custom\reports"],
            executionContext: new ExecutionContextOptions(RunAsSystem: true));

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var target = Assert.Single(plan.Targets);

        Assert.Equal("run-001", plan.RunId);
        Assert.True(plan.DryRun);
        Assert.Equal(4, plan.ThrottleLimit);
        Assert.Equal(new DateTimeOffset(2026, 06, 13, 12, 0, 0, TimeSpan.Zero), plan.CreatedAt);
        Assert.Equal(@"D:\Dispatch\Runs\run-001", plan.LocalRunRoot);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Admin", plan.LocalAdminRoot);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Targets", plan.LocalTargetsRoot);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Admin\results.json", plan.LocalResultsJsonPath);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Admin\results.csv", plan.LocalResultsCsvPath);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Admin\events.ndjson", plan.LocalEventsNdjsonPath);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001", plan.RemoteRunRoot);
        Assert.Equal([0, 3010], plan.Job.ExpectedExitCodes);
        Assert.Equal(["logs", @"custom\reports"], plan.Job.ArtifactPolicy.Paths);
        Assert.True(plan.Job.ExecutionContext.RunAsSystem);
        Assert.True(plan.Job.ResultPolicy.WriteJson);
        Assert.False(plan.Job.ResultPolicy.WriteCsv);
        Assert.False(plan.Job.ResultPolicy.WritePerTargetJson);
        Assert.False(plan.Job.ResultPolicy.WriteTextLog);
        Assert.True(plan.Job.ResultPolicy.WriteEventStream);

        Assert.Equal("PC001", target.Target.Name);
        Assert.Equal(TargetExecutionState.Pending, target.State);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Targets\PC001", target.PlannedLocalTargetRoot);
        Assert.Equal(@"D:\Dispatch\Runs\run-001\Targets\PC001\result.json", target.PlannedLocalResultPath);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", target.PlannedRemoteScriptPath);
        Assert.NotNull(target.PlannedCommand);
        Assert.Equal("powershell.exe", target.PlannedCommand.Executable);
        Assert.Equal(
            ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", @"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", "-Mode", "Repair"],
            target.PlannedCommand.Arguments);
    }

    [Fact]
    public async Task PlannerRejectsMissingLocalScriptBeforeEndpointWork()
    {
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(@"C:\Does\Not\Exist.ps1", []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true);

        var exception = await Assert.ThrowsAsync<DispatchPlanningException>(
            () => planner.CreatePlanAsync(request, CancellationToken.None));

        Assert.Contains(exception.Errors, static error => error.Code == "ScriptNotFound");
    }

    [Fact]
    public async Task PlannerAllowsPsrpScriptPayloadForCurrentSharedTransportSlice()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.Psrp,
            dryRun: true);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.Equal(TransportKind.Psrp, plan.Job.Transport);
        Assert.False(plan.Job.ScriptTransferPolicy.RequiresEndpointLocalScriptPath);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", Assert.Single(plan.Targets).PlannedRemoteScriptPath);
    }

    [Fact]
    public async Task PlannerPreservesPsrpConfigurationNameInExecutionContext()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.Psrp,
            dryRun: true,
            executionContext: new ExecutionContextOptions(PsrpConfigurationName: "PowerShell.7"));

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.Equal("PowerShell.7", plan.Job.ExecutionContext.PsrpConfigurationName);
    }

    [Fact]
    public async Task PlannerAllowsWinRmScriptPayloadAndRequiresEndpointLocalScriptPath()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: true);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.Equal(TransportKind.WinRm, plan.Job.Transport);
        Assert.True(plan.Job.ScriptTransferPolicy.RequiresEndpointLocalScriptPath);
        Assert.Equal(@"C:\ProgramData\Dispatch\Runs\run-001\script\Fix.ps1", Assert.Single(plan.Targets).PlannedRemoteScriptPath);
    }

    [Fact]
    public async Task PlannerAllowsWinRmCommandPayloadWithoutRemoteScriptPreparation()
    {
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new CommandPayload("whoami /all", "cmd", null),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.WinRm,
            dryRun: true);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);
        var target = Assert.Single(plan.Targets);

        Assert.Equal(TransportKind.WinRm, plan.Job.Transport);
        Assert.False(plan.Job.ScriptTransferPolicy.RequiresEndpointLocalScriptPath);
        Assert.Null(target.PlannedRemoteScriptPath);
        Assert.NotNull(target.PlannedCommand);
        Assert.Equal("cmd.exe", target.PlannedCommand.Executable);
        Assert.Equal(["/c", "whoami /all"], target.PlannedCommand.Arguments);
    }

    [Fact]
    public async Task PlannerRejectsLikelySecretScriptArgumentsBeforeDryRunRendering()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, ["-SasToken", "sv=2024-01-01&se=2026-01-01&sp=r&sig=abc123"]),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true);

        var exception = await Assert.ThrowsAsync<DispatchPlanningException>(
            () => planner.CreatePlanAsync(request, CancellationToken.None));

        Assert.Contains(exception.Errors, static error => error.Code == "CommandLineSecretNotSupported");
    }

    [Fact]
    public async Task PlannerRejectsRelativeRemoteRunRoot()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true,
            remoteRunRoot: "Dispatch\\Runs");

        var exception = await Assert.ThrowsAsync<DispatchPlanningException>(
            () => planner.CreatePlanAsync(request, CancellationToken.None));

        Assert.Contains(exception.Errors, static error => error.Code == "InvalidRemoteRunRoot");
    }

    [Theory]
    [InlineData(@"C:\Temp\logs")]
    [InlineData(@"\\server\share")]
    [InlineData(@"..\logs")]
    [InlineData(@"logs\..\secret")]
    [InlineData(@"logs\*.txt")]
    public async Task PlannerRejectsUnsafeArtifactPaths(string artifactPath)
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var provider = BuildProvider();
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true,
            artifactPaths: [artifactPath]);

        var exception = await Assert.ThrowsAsync<DispatchPlanningException>(
            () => planner.CreatePlanAsync(request, CancellationToken.None));

        Assert.Contains(exception.Errors, static error => error.Code == "InvalidArtifactPath");
    }

    [Fact]
    public async Task RealRunPlanningCreatesLocalRunLayoutUnderOverrideRoot()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        using var provider = BuildProvider(outputRoot.Path);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001"), new TargetSpec("PC002")],
            transport: TransportKind.PsExec,
            dryRun: false,
            localRunRoot: outputRoot.Path);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.True(Directory.Exists(plan.LocalRunRoot));
        Assert.True(Directory.Exists(plan.LocalAdminRoot));
        Assert.True(Directory.Exists(plan.LocalTargetsRoot));
        Assert.All(plan.Targets, target => Assert.True(Directory.Exists(target.PlannedLocalTargetRoot)));
    }

    [Fact]
    public async Task DryRunPlanningUsesConfiguredDefaultRunRoot()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        using var provider = BuildProvider(outputRoot.Path);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true);

        var plan = await planner.CreatePlanAsync(request, CancellationToken.None);

        Assert.Equal(System.IO.Path.Combine(outputRoot.Path, "run-001"), plan.LocalRunRoot);
        Assert.Equal(System.IO.Path.Combine(outputRoot.Path, "run-001", "Admin"), plan.LocalAdminRoot);
        Assert.Equal(System.IO.Path.Combine(outputRoot.Path, "run-001", "Targets"), plan.LocalTargetsRoot);
    }

    [Fact]
    public async Task DryRunPlanningValidatesFileDirectoryConflicts()
    {
        using var script = TemporaryScript.Create("Fix.ps1");
        using var outputRoot = TemporaryDirectory.Create();
        var runRoot = System.IO.Path.Combine(outputRoot.Path, "run-001");
        Directory.CreateDirectory(runRoot);
        File.WriteAllText(System.IO.Path.Combine(runRoot, "Admin"), "conflict");
        using var provider = BuildProvider(outputRoot.Path);
        var planner = provider.GetRequiredService<IDispatchPlanner>();
        var request = new DispatchRequest(
            payload: new ScriptPayload(script.Path, []),
            targets: [new TargetSpec("PC001")],
            transport: TransportKind.PsExec,
            dryRun: true,
            localRunRoot: outputRoot.Path);

        var exception = await Assert.ThrowsAsync<DispatchPlanningException>(
            () => planner.CreatePlanAsync(request, CancellationToken.None));

        Assert.Contains(exception.Errors, static error => error.Code == "LocalAdminPathConflictsWithFile");
    }

    private static ServiceProvider BuildProvider(string localRunRoot = @"D:\Dispatch\Runs")
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
            .AddSingleton<IRunIdGenerator>(new FixedRunIdGenerator("run-001"))
            .AddSingleton<ISystemClock>(new FixedSystemClock(new DateTimeOffset(2026, 06, 13, 12, 0, 0, TimeSpan.Zero)))
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
}
