using Dispatch.Cli;
using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Microsoft.Extensions.Options;

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
        Assert.Contains("Windows-native script orchestration", output);
        Assert.Contains("run", output);
        Assert.Contains("doctor", output);
        Assert.Null(planner.LastRequest);
    }

    [Fact]
    public async Task VersionPrintsDispatchProductVersion()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["--version"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains(DispatchProduct.Version, output);
    }

    [Fact]
    public async Task DoctorRouteIsAvailableForOperatorDiagnosticsSlice()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["doctor"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("dispatch doctor command surface is available", output);
        Assert.Contains("6.1 Operator Diagnostics", output);
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
            Assert.Contains("\"dryRun\": true", output);
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

    private static DispatchCliApplication CreateApplication(CapturingPlanner planner) =>
        new(
            Options.Create(new DispatchOptions { ExpectedExitCodes = [0] }),
            planner,
            new ThrowingExecutor());

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

        public Task<ExecutionPlan> CreatePlanAsync(DispatchRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
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

            return Task.FromResult(new ExecutionPlan(
                RunId: "run-test",
                CreatedAt: DateTimeOffset.UnixEpoch,
                Job: job,
                Targets: [],
                DryRun: request.DryRun,
                ThrottleLimit: request.Throttle ?? 0));
        }
    }

    private sealed class ThrowingExecutor : IDispatchExecutor
    {
        public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The CLI surface test must not execute endpoint work.");
    }
}
