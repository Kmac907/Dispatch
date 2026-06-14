using Dispatch.Cli;
using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Dispatch.Cli.Tests;

public sealed class DispatchCliApplicationTests
{
    [Fact]
    public async Task NoArgumentsWithRedirectedInputPrintsRootHelp()
    {
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync([], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatch Command Center", output);
        Assert.Contains("dispatch run", output);
        Assert.Contains("dispatch doctor", output);
        Assert.DoesNotContain("Usage:", output);
        Assert.Null(planner.LastRequest);
    }

    [Fact]
    public async Task VersionPrintsDispatchProductVersion()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["--version"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatch Version", output);
        Assert.Contains(DispatchProduct.Version, output);
    }

    [Fact]
    public async Task DoctorRouteIsAvailableForOperatorDiagnosticsSlice()
    {
        var application = CreateApplication(
            new CapturingPlanner(),
            new StaticDoctor(new DispatchDoctorReport(
            [
                DispatchDoctorCheck.Pass("Operating system", "Windows host detected."),
                DispatchDoctorCheck.Warning("Admin context", "Current process is not elevated.")
            ])));

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["doctor"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatch Doctor Passed", output);
        Assert.Contains("PASS", output);
        Assert.Contains("Operating system", output);
        Assert.Contains("WARN", output);
        Assert.Contains("Admin context", output);
    }

    [Fact]
    public async Task DoctorRouteFailsWhenPrerequisiteFails()
    {
        var application = CreateApplication(
            new CapturingPlanner(),
            new StaticDoctor(new DispatchDoctorReport(
            [
                DispatchDoctorCheck.Fail("PsExec", "PsExec was not found.", "Set Dispatch:PsExecPath.")
            ])));

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["doctor"], CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Contains("Dispatch Doctor Failed", output);
        Assert.Contains("FAIL", output);
        Assert.Contains("PsExec", output);
        Assert.Contains("Set Dispatch:PsExecPath", output);
    }

    [Fact]
    public void DoctorChecksConfiguredPsExecAndWritableOutputPath()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-doctor-{Guid.NewGuid():N}");
        var psexecPath = Path.Combine(root, "PsExec.exe");
        Directory.CreateDirectory(root);
        File.WriteAllText(psexecPath, "fake psexec");
        var doctor = new DispatchDoctor(Options.Create(new DispatchOptions
        {
            LocalRunRoot = root,
            PsExecPath = psexecPath
        }));

        try
        {
            var report = doctor.Run();

            Assert.DoesNotContain(report.Checks, static check => check.Status == DispatchDoctorStatus.Fail);
            Assert.Contains(report.Checks, static check => check is { Name: "PsExec", Status: DispatchDoctorStatus.Pass });
            Assert.Contains(report.Checks, static check => check is { Name: "Output path", Status: DispatchDoctorStatus.Pass });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DoctorReportsMissingPsExecAsFailure()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-doctor-{Guid.NewGuid():N}");
        var doctor = new DispatchDoctor(Options.Create(new DispatchOptions
        {
            LocalRunRoot = root,
            PsExecPath = Path.Combine(root, "missing-psexec.exe")
        }));

        try
        {
            var report = doctor.Run();

            var check = Assert.Single(report.Checks, static item => item.Name == "PsExec");
            Assert.Equal(DispatchDoctorStatus.Fail, check.Status);
            Assert.Contains("PsExec was not found", check.Message);
            Assert.Contains("%TEMP%", check.Message);
            Assert.False(report.Succeeded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunDryRunCreatesSharedDispatchRequest()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "param([string]$Name) Write-Output $Name");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "--dry-run",
                    "--script",
                    scriptPath,
                    "--computer-name",
                    "PC001,PC002",
                    "--transport",
                    "psexec",
                    "--expected-exit-code",
                    "0,3010",
                    "--throttle",
                    "2",
                    "--artifact-path",
                    "logs,artifacts",
                    "--",
                    "--Name",
                    "Dispatch"
                ],
                CancellationToken.None));

            Assert.Equal(0, exitCode);
            Assert.Contains("Dispatch Dry Run", output);
            Assert.Contains("Execution Plan", output);
            Assert.Contains("PC001", output);
            Assert.Contains("PC002", output);
            Assert.DoesNotContain("\"dryRun\": true", output);
            Assert.NotNull(planner.LastRequest);
            var request = planner.LastRequest!;
            Assert.True(request.DryRun);
            Assert.Equal(TransportKind.PsExec, request.Transport);
            Assert.Equal(2, request.Throttle);
            Assert.Equal([0, 3010], request.ExpectedExitCodes);
            Assert.Equal(["logs", "artifacts"], request.ArtifactPaths);
            Assert.Equal(["PC001", "PC002"], request.Targets.Select(static target => target.Name));
            var payload = Assert.IsType<ScriptPayload>(request.Payload);
            Assert.Equal(scriptPath, payload.ScriptPath);
            Assert.Equal(["--Name", "Dispatch"], payload.ScriptArguments);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RedirectedCompactModeDoesNotReprintProgressPanels()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var executor = new SucceedingExecutor();
        var application = CreateApplication(planner, executor: executor, displayMode: DispatchRunDisplayMode.AppendOnly);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "--script",
                    scriptPath,
                    "--computer-name",
                    "PC001",
                    "--transport",
                    "psexec"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch Run Complete", output);
            Assert.Contains("run-test", output);
            Assert.Contains("PC001", output);
            Assert.DoesNotContain("Target Progress", error);
            Assert.DoesNotContain("Probing", error);
            Assert.DoesNotContain("Succeeded", error);
            Assert.DoesNotContain("\"runId\": \"run-test\"", output);
            Assert.DoesNotContain("PC001: probing", error);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task NoDashboardUsesCompactLiveProgressWhenConsoleIsAvailable()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var executor = new SucceedingExecutor();
        using var progressWriter = new StringWriter();
        var statusConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new DispatchAnsiConsoleOutput(progressWriter, isTerminal: true),
            Interactive = InteractionSupport.Yes
        });
        var application = CreateApplication(
            planner,
            executor: executor,
            displayMode: DispatchRunDisplayMode.Auto,
            statusConsole: statusConsole);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "--script",
                    scriptPath,
                    "--computer-name",
                    "PC001",
                    "--transport",
                    "psexec",
                    "--no-dashboard"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch Run Complete", output);
            var progress = progressWriter.ToString();
            Assert.Contains("PC001", progress);
            Assert.Contains("Complete", progress);
            Assert.Contains("100%", progress);
            Assert.DoesNotContain("\"runId\": \"run-test\"", output);
            Assert.DoesNotContain("Target Progress", progress);
            Assert.DoesNotContain("PC001: probing", progress);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public void CommandCenterRendererShowsPersistentMenuOptions()
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new DispatchAnsiConsoleOutput(writer, isTerminal: false),
            Interactive = InteractionSupport.No
        });

        DispatchConsoleRenderer.RenderInteractiveStart(console);
        var output = writer.ToString();

        Assert.Contains("Dispatch Interactive Command Center", output);
        Assert.Contains("Start Script Run", output);
        Assert.Contains("Doctor Diagnostics", output);
        Assert.Contains("Command Help", output);
        Assert.Contains("Exit", output);
    }

    [Fact]
    public async Task LiveDashboardRendererShowsRunStatusTargetPhaseAndFailures()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();

        try
        {
            var application = CreateApplication(planner);
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "--dry-run",
                    "--script",
                    scriptPath,
                    "--computer-name",
                    "PC001,PC002",
                    "--transport",
                    "psexec",
                    "--throttle",
                    "2"
                ],
                CancellationToken.None));
            Assert.True(exitCode == 0, $"Dry-run planning failed. {error}");

            var plan = Assert.IsType<ExecutionPlan>(planner.LastPlan);
            var dashboard = new SpectreDispatchRunDashboard(plan, DateTimeOffset.UnixEpoch);
            dashboard.Update(new DispatchExecutionProgress(
                plan.RunId,
                "PC001",
                TargetExecutionState.Executing,
                DateTimeOffset.UnixEpoch.AddSeconds(1)));
            dashboard.Update(new DispatchExecutionProgress(
                plan.RunId,
                "PC002",
                TargetExecutionState.Failed,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                FailureCategory.ExecutionFailed,
                "Installer returned 1603."));

            using var writer = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Out = new DispatchAnsiConsoleOutput(writer, isTerminal: false),
                Interactive = InteractionSupport.No
            });

            console.Write(dashboard.Render());
            var output = writer.ToString();

            Assert.Contains("Dispatch Run", output);
            Assert.Contains("Run ID", output);
            Assert.Contains("Outcome", output);
            Assert.Contains("run-test", output);
            Assert.Contains("PsExec", output);
            Assert.Contains("PC001", output);
            Assert.Contains("Executing", output);
            Assert.Contains("PC002", output);
            Assert.Contains("ExecutionFailed", output);
            Assert.Contains("Installer returned 1603", output);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunHelpUsesDispatchSpectreHelp()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["run", "--help"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Run Command", output);
        Assert.Contains("--script", output);
        Assert.Contains("--computer-name", output);
        Assert.DoesNotContain("Usage:", output);
    }

    [Fact]
    public async Task InvalidCommandUsesDispatchSpectreError()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(["bogus"], CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown Dispatch Command", error);
        Assert.DoesNotContain("Usage:", error);
    }

    [Fact]
    public async Task RunValidationFailureUsesDispatchSpectreError()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
            ["run", "--computer-name", "PC001"],
            CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid Dispatch Command", error);
        Assert.Contains("--script", error);
        Assert.DoesNotContain("Usage:", error);
    }

    private static DispatchCliApplication CreateApplication(
        CapturingPlanner planner,
        IDispatchDoctor? doctor = null,
        IDispatchExecutor? executor = null,
        DispatchRunDisplayMode displayMode = DispatchRunDisplayMode.Auto,
        IAnsiConsole? statusConsole = null) =>
        new(
            Options.Create(new DispatchOptions { ExpectedExitCodes = [0] }),
            planner,
            executor ?? new ThrowingExecutor(),
            doctor ?? new StaticDoctor(new DispatchDoctorReport([])),
            displayMode,
            statusConsole);

    private static async Task<(int ExitCode, string Output, string Error)> CaptureConsoleAsync(Func<Task<int>> action)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);

        try
        {
            var exitCode = await action();
            return (exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed class CapturingPlanner : IDispatchPlanner
    {
        public DispatchRequest? LastRequest { get; private set; }
        public ExecutionPlan? LastPlan { get; private set; }

        public Task<ExecutionPlan> CreatePlanAsync(DispatchRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var targets = request.Targets
                .Select(target => new TargetExecution(
                    "run-test",
                    target,
                    TargetExecutionState.Pending,
                    Path.Combine(@"C:\Dispatch\Tests\run-test\Targets", target.Name),
                    Path.Combine(@"C:\Dispatch\Tests\run-test\Targets", target.Name, "result.json"),
                    Path.Combine(@"C:\ProgramData\Dispatch\Runs\run-test\script", Path.GetFileName(((ScriptPayload)request.Payload).ScriptPath))))
                .ToArray();
            var job = new DispatchJob(
                RunId: "run-test",
                Targets: request.Targets,
                Payload: request.Payload,
                Transport: request.Transport,
                ExecutionContext: request.ExecutionContext,
                ScriptTransferPolicy: new ScriptTransferPolicy(@"C:\ProgramData\Dispatch\Runs\run-test", true),
                TimeoutPolicy: new TimeoutPolicy(),
                RetryPolicy: new RetryPolicy(),
                ExpectedExitCodes: request.ExpectedExitCodes,
                ArtifactPolicy: new ArtifactPolicy(request.ArtifactPaths),
                ResultPolicy: new ResultPolicy(@"C:\Dispatch\Tests\run-test"));

            LastPlan = new ExecutionPlan(
                RunId: "run-test",
                CreatedAt: DateTimeOffset.UnixEpoch,
                Job: job,
                Targets: targets,
                DryRun: request.DryRun,
                ThrottleLimit: request.Throttle ?? 0,
                LocalResultsJsonPath: @"C:\Dispatch\Tests\run-test\Admin\results.json");
            return Task.FromResult(LastPlan);
        }
    }

    private sealed class ThrowingExecutor : IDispatchExecutor
    {
        public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CLI surface test must not execute endpoint work.");

        public Task<DispatchRunResult> ExecuteAsync(
            ExecutionPlan plan,
            IDispatchExecutionObserver observer,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CLI surface test must not execute endpoint work.");
    }

    private sealed class SucceedingExecutor : IDispatchExecutor
    {
        public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken) =>
            ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken);

        public async Task<DispatchRunResult> ExecuteAsync(
            ExecutionPlan plan,
            IDispatchExecutionObserver observer,
            CancellationToken cancellationToken)
        {
            var target = Assert.Single(plan.Targets);
            await observer.OnProgressAsync(
                new DispatchExecutionProgress(plan.RunId, target.Target.Name, TargetExecutionState.Probing, DateTimeOffset.UnixEpoch),
                cancellationToken);
            await observer.OnProgressAsync(
                new DispatchExecutionProgress(plan.RunId, target.Target.Name, TargetExecutionState.Succeeded, DateTimeOffset.UnixEpoch),
                cancellationToken);

            return new DispatchRunResult(
                RunId: plan.RunId,
                StartedAt: DateTimeOffset.UnixEpoch,
                EndedAt: DateTimeOffset.UnixEpoch,
                RequestedBy: "test",
                Transport: plan.Job.Transport,
                PayloadType: plan.Job.Payload.PayloadType,
                PayloadName: plan.Job.Payload.DisplayName,
                Targets:
                [
                    new TargetExecutionResult(
                        RunId: plan.RunId,
                        Target: target.Target.Name,
                        Transport: plan.Job.Transport,
                        PayloadType: plan.Job.Payload.PayloadType,
                        PayloadName: plan.Job.Payload.DisplayName,
                        State: TargetExecutionState.Succeeded,
                        ExitCode: 0,
                        ExpectedExitCodes: plan.Job.ExpectedExitCodes,
                        StartedAt: DateTimeOffset.UnixEpoch,
                        EndedAt: DateTimeOffset.UnixEpoch,
                        FailureCategory: FailureCategory.None,
                        FailureMessage: null,
                        ResultPath: target.PlannedLocalResultPath ?? string.Empty)
                ],
                ResultPath: plan.LocalResultsJsonPath);
        }
    }

    private sealed class StaticDoctor(DispatchDoctorReport report) : IDispatchDoctor
    {
        public DispatchDoctorReport Run() => report;
    }
}
