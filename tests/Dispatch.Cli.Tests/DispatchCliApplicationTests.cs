using Dispatch.Cli;
using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Security;
using System.Text.Json;
using System.Threading.Channels;

namespace Dispatch.Cli.Tests;

public sealed class DispatchCliApplicationTests
{
    [Fact]
    public async Task NoArgumentsWithRedirectedInputPrintsRootHelp()
    {
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync([], CancellationToken.None));
        var normalized = NormalizeWhitespace(output);

        Assert.Equal(0, exitCode);
        Assert.Contains("Windows-native automation runner", output);
        Assert.Contains("PowerShell scripts and commands", normalized);
        Assert.Contains("PsExec, raw WinRM, and PSRP execute today", normalized);
        Assert.Contains("raw WinRM and PSRP live-validated", normalized);
        Assert.Contains("apply", output);
        Assert.Contains("dispatch run", output);
        Assert.Contains("dispatch doctor", output);
        Assert.Contains("Usage:", output);
        Assert.Null(planner.LastRequest);
    }

    [Fact]
    public async Task VersionPrintsDispatchProductVersion()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["--version"], CancellationToken.None));

        Assert.Equal(0, exitCode);
        Assert.Contains("Dispatch", output);
        Assert.Contains("Spectre.Console.Cli design", output);
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
        Assert.Contains("Dispatch doctor passed", output);
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
        Assert.Contains("Dispatch doctor failed", output);
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
            Assert.Contains("Dry run planning", output);
            Assert.Contains("Validate request", output);
            Assert.Contains("Build execution plan", output);
            Assert.Contains("Dispatch plan", output);
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
            Assert.Contains("Dispatch run complete", output);
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
    public async Task NoProgressUsesAppendOnlySpectreProgressWhenConsoleIsAvailable()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var executor = new SucceedingExecutor();
        using var progressWriter = new StringWriter();
        var application = CreateApplication(
            planner,
            executor: executor,
            displayMode: DispatchRunDisplayMode.Auto,
            statusWriter: progressWriter);

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
                    "--no-progress"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch run complete", output);
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
    public void RenderRunResultShowsResultEventAndTargetPaths()
    {
        var result = new DispatchRunResult(
            RunId: "run-test",
            StartedAt: DateTimeOffset.Parse("2026-06-17T20:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-17T20:01:00Z"),
            RequestedBy: "tester",
            Transport: TransportKind.PsExec,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "run-test",
                    Target: "PC001",
                    Transport: TransportKind.PsExec,
                    PayloadType: PayloadKind.Script,
                    PayloadName: "Fix.ps1",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-17T20:00:05Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-17T20:00:45Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\run-test\Targets\PC001\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\run-test\Targets\PC001\stderr.txt")
            ],
            ResultPath: @"C:\Dispatch\Tests\run-test\Admin\results.json");

        using var writer = new StringWriter();
        SpectreConsoleRenderer.RenderRunResult(writer, result);
        var output = writer.ToString();

        Assert.Contains("Outputs", output);
        Assert.Contains(@"Results: C:\Dispatch\Tests\run-test\Admin\results.json", output);
        Assert.Contains(@"Events: C:\Dispatch\Tests\run-test\Admin\events.ndjson", output);
        Assert.Contains(@"Target Root: C:\Dispatch\Tests\run-test\Targets\<target>", output);
        Assert.Contains("Stdout:", output);
        Assert.Contains(@"C:\Dispatch\Tests\run-test\Targets\<target>\stdout.txt", output);
        Assert.Contains("Stderr:", output);
        Assert.Contains(@"C:\Dispatch\Tests\run-test\Targets\<target>\stderr.txt", output);
    }

    [Fact]
    public async Task RunPowerShellRouteUsesNewCommandShapeAndSharedRequest()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001,PC002",
                    "--transport",
                    "psexec",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch plan", output);
            Assert.NotNull(planner.LastRequest);
            Assert.True(planner.LastRequest!.DryRun);
            Assert.Equal(["PC001", "PC002"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteMapsAutoTransportToConfiguredDefault()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "auto",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.PsExec, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesInventoryTransportWhenTransportIsNotSpecified()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesInlineInventoryTransportMapsWhenTransportIsNotSpecified()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults: { transport: winrm }
            groups:
              web:
                vars: { transport: psrp }
                hosts: [WEB01, WEB02]
            hosts:
              WEB01:
                vars: { transport: psexec }
              APP01:
                tags: [prod]
            """);

        async Task<TransportKind> ResolveTransportAsync(string target)
        {
            var planner = new CapturingPlanner();
            var application = CreateApplication(planner);

            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    target,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            return planner.LastRequest!.Transport;
        }

        try
        {
            Assert.Equal(TransportKind.PsExec, await ResolveTransportAsync("WEB01"));
            Assert.Equal(TransportKind.Psrp, await ResolveTransportAsync("WEB02"));
            Assert.Equal(TransportKind.WinRm, await ResolveTransportAsync("APP01"));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteSupportsInlineListTopLevelHostInventory()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            hosts: [WEB01, WEB02]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(["WEB01", "WEB02"], planner.LastRequest!.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteSupportsTopLevelInlineMapHostEntries()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults: { transport: winrm }
            hosts:
              WEB01: { tags: [prod], vars: { transport: psexec } }
              WEB02: { tags: [test] }
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "tag:prod",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(["WEB01"], planner.LastRequest!.Targets.Select(static target => target.Name));
            Assert.Equal(TransportKind.PsExec, planner.LastRequest.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteAutoTransportUsesInventoryTransportBeforeConfiguredDefault()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01",
                    "--transport",
                    "auto",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteExplicitTransportOverridesInventoryTransport()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01",
                    "--transport",
                    "psexec",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.PsExec, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsConflictingInventoryTransportsBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            hosts:
              WEB01:
                transport: winrm
              WEB02:
                transport: psrp
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01,WEB02",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("InventoryTransportConflict", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsMixedInventoryAndDefaultTransportsBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "PC001,WEB01",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("InventoryTransportConflict", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsDefaultsOnlyInventoryWhenNoTargetsResolve()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("TargetsRequired", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsDefaultsOnlyInventoryAllSelectorBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "all",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("TargetSelectorMatchedNoTargets", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesAmbientConfigForInventoryTargetExcludeAndTransportDefault()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(
            planner,
            options: new DispatchOptions
            {
                Inventory = inventoryPath,
                Target = "web",
                Exclude = "WEB02",
                DefaultTransport = TransportKind.WinRm,
                ExpectedExitCodes = [0]
            });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
            Assert.Equal(["WEB01"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteInventoryTransportBeatsAmbientConfigTransport()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(
            planner,
            options: new DispatchOptions
            {
                Inventory = inventoryPath,
                Target = "WEB01",
                DefaultTransport = TransportKind.PsExec,
                ExpectedExitCodes = [0]
            });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteExplicitConfigOverridesAmbientConfigWhenProvided()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var ambientInventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var explicitInventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(ambientInventoryPath, """
            hosts:
              APP01:
                tags: [ambient]
            """);
        await File.WriteAllTextAsync(explicitInventoryPath, """
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            Dispatch = new
            {
                Inventory = explicitInventoryPath,
                Target = "web",
                Exclude = "WEB02",
                DefaultTransport = "winrm"
            }
        }));
        var planner = new CapturingPlanner();
        var application = CreateApplication(
            planner,
            options: new DispatchOptions
            {
                Inventory = ambientInventoryPath,
                Target = "APP01",
                DefaultTransport = TransportKind.PsExec,
                ExpectedExitCodes = [0]
            });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
            Assert.Equal(["WEB01"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(ambientInventoryPath);
            File.Delete(explicitInventoryPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesExplicitConfigForInventoryTargetExcludeAndTransportDefault()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            Dispatch = new
            {
                Inventory = inventoryPath,
                Target = "web",
                Exclude = "WEB02",
                DefaultTransport = "psexec"
            }
        }));
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.PsExec, planner.LastRequest!.Transport);
            Assert.Equal(["WEB01"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesExplicitYamlConfigForInventoryTargetExcludeAndTransportDefault()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        await File.WriteAllTextAsync(configPath, $"""
            dispatch:
              inventory: {inventoryPath}
              target: web
              exclude: WEB02
              default_transport: winrm
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
            Assert.Equal(["WEB01"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsPlaintextSecretFieldsInYamlConfig()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, """
            dispatch:
              password: not-allowed
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--target",
                    "PC001",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("plaintext secret field", output + error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteCliFlagsOverrideExplicitConfig()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            hosts:
              WEB01:
                tags: [prod]
              WEB02:
                tags: [prod]
            """);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            Dispatch = new
            {
                Inventory = inventoryPath,
                Target = "WEB01",
                Exclude = "WEB02",
                DefaultTransport = "winrm"
            }
        }));
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--target",
                    "WEB02",
                    "--exclude",
                    "WEB01",
                    "--transport",
                    "psexec",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.PsExec, planner.LastRequest!.Transport);
            Assert.Equal(["WEB02"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteInventoryTransportBeatsExplicitConfigTransport()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            defaults:
              transport: winrm
            hosts:
              WEB01:
                tags: [prod]
            """);
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(new
        {
            Dispatch = new
            {
                Inventory = inventoryPath,
                Target = "WEB01",
                DefaultTransport = "psexec"
            }
        }));
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.WinRm, planner.LastRequest!.Transport);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteMissingExplicitConfigFailsBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Config file", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteInvalidExplicitConfigFailsBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, "{");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--config",
                    configPath,
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Config file", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteUsesInventoryTargetSelectorAndExclude()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "web",
                    "--exclude",
                    "WEB02",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(["WEB01"], planner.LastRequest!.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteSupportsMappingFormGroupMembers()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                children:
                  prod:
              prod:
                hosts:
                  WEB01:
                  WEB02:
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "web",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(["WEB01", "WEB02"], planner.LastRequest!.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteSupportsInlineListFormGroupMembers()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                children: [prod]
              prod:
                hosts: [WEB01, WEB02]
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "web",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(["WEB01", "WEB02"], planner.LastRequest!.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteResolvesNestedInventoryGroupsAndInheritedTransport()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              prod:
                vars:
                  transport: psrp
                children:
                  - web
              web:
                hosts:
                  - WEB01
                  - WEB02
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "prod",
                    "--exclude",
                    "WEB02",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.NotNull(planner.LastRequest);
            Assert.Equal(TransportKind.Psrp, planner.LastRequest!.Transport);
            Assert.Equal(["WEB01"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsInventoryGroupCyclesBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            groups:
              web:
                children:
                  - prod
              prod:
                children:
                  - web
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "web",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("InventoryGroupCycle", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsUnsupportedSelectorBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "web:&prod",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("TargetSelectorUnsupported", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunPowerShellRouteRejectsUnsupportedInventoryFieldBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
            hosts:
              WEB01:
                owner: ops
            """);
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("InventoryFieldUnsupported", output + error, StringComparison.Ordinal);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task QuietRunPowerShellPlanSuppressesRichOutputAndAcceptsDiagnosticFlags()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--plan",
                    "--quiet",
                    "--no-color",
                    "--verbose",
                    "--trace"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Equal(string.Empty, output);
            Assert.Equal(string.Empty, error);
            Assert.NotNull(planner.LastRequest);
            Assert.True(planner.LastRequest!.DryRun);
            Assert.Equal(["PC001"], planner.LastRequest.Targets.Select(static target => target.Name));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task JsonOutputEmitsPlanDocumentWithoutDecorativeProgress()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--plan",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            Assert.Equal("run-test", document.RootElement.GetProperty("runId").GetString());
            Assert.False(output.Contains("Dispatch plan", StringComparison.Ordinal));
            Assert.False(output.Contains("Dry run planning", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task QuietDoesNotSuppressStructuredOutput()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--plan",
                    "--quiet",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            Assert.Equal("run-test", document.RootElement.GetProperty("runId").GetString());
            Assert.Equal(string.Empty, error);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task NdjsonOutputStreamsPlanningProgressAndResultEvents()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var executor = new SucceedingExecutor();
        var application = CreateApplication(planner, executor: executor);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--output",
                    "ndjson"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            var events = ParseNdjson(output);
            Assert.Equal(
                ["planning.started", "plan", "execution.started", "progress", "progress", "result"],
                events.Select(static document => document.RootElement.GetProperty("type").GetString()).ToArray());
            Assert.Equal("run-test", events.Last().RootElement.GetProperty("result").GetProperty("runId").GetString());
            Assert.Contains(events, static document =>
                document.RootElement.GetProperty("type").GetString() == "progress"
                && document.RootElement.GetProperty("state").GetString() == "probing");
            Assert.DoesNotContain("Target Progress", output);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task NdjsonTraceOutputIncludesEventDetails()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var executor = new SucceedingExecutor();
        var application = CreateApplication(planner, executor: executor);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--output",
                    "ndjson",
                    "--trace"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            var events = ParseNdjson(output);
            var plan = Assert.Single(events, static document => document.RootElement.GetProperty("type").GetString() == "plan");
            Assert.Equal("trace", plan.RootElement.GetProperty("details").GetProperty("verbosity").GetString());
            Assert.Equal(@"C:\Dispatch\Tests\run-test\Admin\results.json", plan.RootElement.GetProperty("details").GetProperty("resultsJsonPath").GetString());
            Assert.Equal(@"C:\Dispatch\Tests\run-test\Admin\events.ndjson", plan.RootElement.GetProperty("details").GetProperty("eventsNdjsonPath").GetString());
            var progress = events.First(static document => document.RootElement.GetProperty("type").GetString() == "progress");
            Assert.Equal("trace", progress.RootElement.GetProperty("details").GetProperty("verbosity").GetString());
            Assert.False(progress.RootElement.GetProperty("details").GetProperty("terminal").GetBoolean());
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task YamlOutputEmitsStablePlanWithoutSpectreTables()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--plan",
                    "--output",
                    "yaml"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("runId: run-test", output);
            Assert.Contains("targets:", output);
            Assert.DoesNotContain("╭", output);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task InvalidOutputModeFailsBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--output",
                    "xml"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Unsupported output mode", error);
            Assert.Null(planner.LastRequest);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunRouteCredentialOverrideBeatsInventoryCredentialReference()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var inventoryPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(inventoryPath, """
defaults:
  credential: inventory-admin
hosts:
  WEB01:
""");
        var planner = new CapturingPlanner();
        var provider = new CapturingCredentialProvider(available: true);
        var application = CreateApplication(planner, credentialProvider: provider);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--inventory",
                    inventoryPath,
                    "--target",
                    "WEB01",
                    "--transport",
                    "psrp",
                    "--credential",
                    "breakglass-admin",
                    "--dry-run",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Equal("test", provider.LastOperation);
            Assert.Equal(new CredentialReferenceRequest("breakglass-admin"), provider.LastReferenceRequest);
            var target = Assert.Single(planner.LastRequest!.Targets);
            Assert.Equal("breakglass-admin", target.CredentialReference);
            Assert.DoesNotContain("inventory-admin", output + error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(inventoryPath);
        }
    }

    [Fact]
    public async Task RunRouteRejectsUnknownCredentialReferenceBeforePlanning()
    {
        var planner = new CapturingPlanner();
        var provider = new CapturingCredentialProvider(available: true, succeeds: false);
        var application = CreateApplication(planner, credentialProvider: provider);

        var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
            [
                "run",
                "cmd",
                "whoami",
                "--target",
                "PC001",
                "--transport",
                "psrp",
                "--credential",
                "missing-admin",
                "--dry-run",
                "--output",
                "json"
            ],
            CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Null(planner.LastRequest);
        Assert.Equal("test", provider.LastOperation);
        Assert.Equal(new CredentialReferenceRequest("missing-admin"), provider.LastReferenceRequest);
        Assert.Contains("Dispatch Credential Reference Invalid", error);
        Assert.Contains("missing-admin", error);
    }

    [Fact]
    public async Task RunRouteDoesNotResolveRuntimeCredentialForPlan()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var provider = new CapturingCredentialProvider(available: true);
        var runtimeResolver = new RecordingRuntimeCredentialResolver();
        var planner = new CapturingPlanner();
        var application = CreateApplication(
            planner,
            credentialProvider: provider,
            runtimeCredentialResolver: runtimeResolver);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "prod-admin",
                    "--plan"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Empty(runtimeResolver.Requests);
            Assert.NotNull(planner.LastRequest);
            Assert.Equal("prod-admin", Assert.Single(planner.LastRequest!.Targets).CredentialReference);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunRouteResolvesRuntimeCredentialBeforePsrpExecution()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var provider = new CapturingCredentialProvider(available: true);
        using var credential = CreateResolvedCredential("prod-admin");
        var runtimeResolver = new RecordingRuntimeCredentialResolver(
            new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase)
            {
                ["prod-admin"] = credential
            });
        var executor = new CapturingSucceedingExecutor();
        var application = CreateApplication(
            new CapturingPlanner(),
            executor: executor,
            displayMode: DispatchRunDisplayMode.AppendOnly,
            credentialProvider: provider,
            runtimeCredentialResolver: runtimeResolver);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "prod-admin",
                    "--no-progress"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Equal(["prod-admin"], Assert.Single(runtimeResolver.Requests));
            Assert.NotNull(executor.LastPlan);
            Assert.Same(credential, executor.LastPlan!.RuntimeCredentials["prod-admin"]);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunRouteRejectsRuntimeCredentialForNonPsrpTransport()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        var provider = new CapturingCredentialProvider(available: true);
        var runtimeResolver = new RecordingRuntimeCredentialResolver();
        var application = CreateApplication(
            new CapturingPlanner(),
            credentialProvider: provider,
            runtimeCredentialResolver: runtimeResolver);

        try
        {
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "winrm",
                    "--credential",
                    "prod-admin",
                    "--no-progress"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Dispatch Credential Handoff Unsupported", error);
            Assert.Empty(runtimeResolver.Requests);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task RunRouteValidatesCredentialOverrideFromExplicitYamlConfig()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, """
dispatch:
  default_credential_provider: prompt
credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
""");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "prod-admin",
                    "--config",
                    configPath,
                    "--dry-run",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Equal("prod-admin", Assert.Single(planner.LastRequest!.Targets).CredentialReference);
            Assert.DoesNotContain("password", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunRouteResolvesRuntimeCredentialFromExplicitYamlConfig()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, """
dispatch:
  default_credential_provider: prompt
credentials:
  prod-admin:
    provider: prompt
    username: CONTOSO\prod.admin
""");
        var executor = new CapturingSucceedingExecutor();
        var prompt = new RecordingRuntimeCredentialPrompt("secret-value");
        var application = CreateApplication(
            new CapturingPlanner(),
            executor: executor,
            displayMode: DispatchRunDisplayMode.AppendOnly,
            runtimeCredentialPrompt: prompt);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "prod-admin",
                    "--config",
                    configPath,
                    "--no-progress",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            var request = Assert.Single(prompt.Requests);
            Assert.Equal("prod-admin", request.ReferenceName);
            Assert.Equal(@"CONTOSO\prod.admin", request.UserName);
            Assert.NotNull(executor.LastPlan);
            var credential = Assert.Single(executor.LastPlan!.RuntimeCredentials.Values);
            Assert.Equal("prod-admin", credential.ReferenceName);
            Assert.Equal(@"CONTOSO\prod.admin", credential.UserName);
            Assert.DoesNotContain("secret-value", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task RunRouteResolvesDpapiRuntimeCredentialFromExplicitYamlConfig()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"dispatch-cli-dpapi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "test.ps1");
        var configPath = Path.Combine(root, "config.yml");
        var credentialPath = Path.Combine(root, "helpdesk-local.cred").Replace('\\', '/');
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, $$"""
dispatch:
  default_credential_provider: prompt
credentials:
  helpdesk-local:
    provider: dpapi_file
    username: .\helpdesk-admin
    path: {{credentialPath}}
""");
        var configuration = DispatchConfigFileReader.Load(configPath);
        var credentialProvider = new ConfigurationCredentialProvider(
            configuration,
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));
        var add = await credentialProvider.AddAsync(
            new CredentialAddRequest("helpdesk-local", null),
            CancellationToken.None);
        Assert.True(add.Succeeded, add.Message);

        var executor = new CapturingSucceedingExecutor();
        var prompt = new RecordingRuntimeCredentialPrompt("unused");
        var application = CreateApplication(
            new CapturingPlanner(),
            executor: executor,
            displayMode: DispatchRunDisplayMode.AppendOnly,
            runtimeCredentialPrompt: prompt);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "helpdesk-local",
                    "--config",
                    configPath,
                    "--no-progress",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Empty(prompt.Requests);
            Assert.NotNull(executor.LastPlan);
            var credential = Assert.Single(executor.LastPlan!.RuntimeCredentials.Values);
            Assert.Equal("helpdesk-local", credential.ReferenceName);
            Assert.Equal(@".\helpdesk-admin", credential.UserName);
            Assert.Equal("dpapi_file", credential.ProviderName);
            Assert.DoesNotContain("secret-value", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunRouteResolvesWindowsCredentialManagerRuntimeCredentialFromExplicitYamlConfig()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(Path.GetTempPath(), $"dispatch-cli-wcm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var scriptPath = Path.Combine(root, "test.ps1");
        var configPath = Path.Combine(root, "config.yml");
        var target = $"Dispatch/Tests/{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, $$"""
dispatch:
  default_credential_provider: prompt
credentials:
  domain-admin:
    provider: windows_credential_manager
    username: SCF\domain.admin
    target: {{target}}
""");
        var configuration = DispatchConfigFileReader.Load(configPath);
        var credentialProvider = new ConfigurationCredentialProvider(
            configuration,
            Options.Create(new DispatchOptions { CredentialProvider = "prompt" }),
            new RecordingRuntimeCredentialPrompt("secret-value", "secret-value"));
        var add = await credentialProvider.AddAsync(
            new CredentialAddRequest("domain-admin", null),
            CancellationToken.None);
        Assert.True(add.Succeeded, add.Message);

        var executor = new CapturingSucceedingExecutor();
        var prompt = new RecordingRuntimeCredentialPrompt("unused");
        var application = CreateApplication(
            new CapturingPlanner(),
            executor: executor,
            displayMode: DispatchRunDisplayMode.AppendOnly,
            runtimeCredentialPrompt: prompt);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "domain-admin",
                    "--config",
                    configPath,
                    "--no-progress",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Empty(prompt.Requests);
            Assert.NotNull(executor.LastPlan);
            var credential = Assert.Single(executor.LastPlan!.RuntimeCredentials.Values);
            Assert.Equal("domain-admin", credential.ReferenceName);
            Assert.Equal(@"SCF\domain.admin", credential.UserName);
            Assert.Equal("windows_credential_manager", credential.ProviderName);
            Assert.DoesNotContain("secret-value", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            WindowsCredentialManagerStore.Delete(target);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunRouteRejectsInvalidCredentialOverrideMetadataFromExplicitYamlConfigBeforePlanning()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.ps1");
        var configPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.yml");
        await File.WriteAllTextAsync(scriptPath, "Write-Output 'ok'");
        await File.WriteAllTextAsync(configPath, """
dispatch:
  default_credential_provider: prompt
credentials:
  kv-prod-admin:
    provider: azure_keyvault
    username: CONTOSO\prod.admin
    vault_uri: https://scf-dispatch-kv.vault.azure.net/
    secret_name: prod-admin-password
    auth: bad_auth
""");
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                [
                    "run",
                    "ps",
                    scriptPath,
                    "--target",
                    "PC001",
                    "--transport",
                    "psrp",
                    "--credential",
                    "kv-prod-admin",
                    "--config",
                    configPath,
                    "--dry-run",
                    "--output",
                    "json"
                ],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Empty(output);
            Assert.Null(planner.LastRequest);
            Assert.Contains("Dispatch Credential Reference Invalid", error);
            Assert.Contains("unsupported azure_keyvault auth", error, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password", output + error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(scriptPath);
            File.Delete(configPath);
        }
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("push")]
    public async Task PlannedTopLevelCommandsRouteThroughSpectreCli(string command)
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync([command], CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Contains("Planned Dispatch command", error);
        Assert.Contains(command, error);
    }

    [Theory]
    [InlineData("hosts", "list")]
    [InlineData("init", "job")]
    public async Task PlannedCommandGroupsRouteThroughSpectreCli(string command, string subcommand)
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync([command, subcommand], CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Contains("Planned Dispatch command", error);
        Assert.Contains($"{command} {subcommand}", error);
    }

    [Fact]
    public async Task CredsListReportsUnavailableProvider()
    {
        var provider = new CapturingCredentialProvider(available: false);
        var application = CreateApplication(new CapturingPlanner(), credentialProvider: provider);

        var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "list"],
            CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Empty(output);
        Assert.Equal("list", provider.LastOperation);
        Assert.Contains("Dispatch Credentials Unavailable", error);
        Assert.Contains("test-provider is unavailable", error);
        Assert.DoesNotContain("Planned Dispatch command", error);
    }

    [Fact]
    public async Task CredsCommandsCallAvailableProviderWithoutPlaintextSecretOptions()
    {
        var provider = new CapturingCredentialProvider(available: true);
        var application = CreateApplication(new CapturingPlanner(), credentialProvider: provider);

        var (addExit, addOutput, addError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "add", "prod-admin", "--username", @"CONTOSO\Admin", "--force"],
            CancellationToken.None));
        Assert.True(addExit == 0, $"Exit {addExit}. Stdout: {addOutput}. Stderr: {addError}");
        Assert.Equal("add", provider.LastOperation);
        Assert.Equal(new CredentialAddRequest("prod-admin", @"CONTOSO\Admin", Force: true), provider.LastAddRequest);
        Assert.Contains("Provider: test-provider", addOutput);
        Assert.DoesNotContain("password", addOutput, StringComparison.OrdinalIgnoreCase);

        var (listExit, listOutput, listError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "list", "--output", "json"],
            CancellationToken.None));
        Assert.True(listExit == 0, $"Exit {listExit}. Stdout: {listOutput}. Stderr: {listError}");
        using var listJson = JsonDocument.Parse(listOutput);
        Assert.Equal("test-provider", listJson.RootElement.GetProperty("providerName").GetString());
        Assert.True(listJson.RootElement.GetProperty("providerAvailable").GetBoolean());
        Assert.Equal("prod-admin", listJson.RootElement.GetProperty("references")[0].GetProperty("name").GetString());
        Assert.False(listOutput.Contains("password", StringComparison.OrdinalIgnoreCase));

        var (testExit, _, testError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "test", "prod-admin"],
            CancellationToken.None));
        Assert.True(testExit == 0, $"Stderr: {testError}");
        Assert.Equal("test", provider.LastOperation);
        Assert.Equal(new CredentialReferenceRequest("prod-admin"), provider.LastReferenceRequest);

        var (removeExit, _, removeError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "remove", "prod-admin"],
            CancellationToken.None));
        Assert.True(removeExit == 0, $"Stderr: {removeError}");
        Assert.Equal("remove", provider.LastOperation);
        Assert.Equal(new CredentialReferenceRequest("prod-admin"), provider.LastReferenceRequest);
    }

    [Fact]
    public async Task CredsAddRejectsPlaintextPasswordOption()
    {
        var provider = new CapturingCredentialProvider(available: true);
        var application = CreateApplication(new CapturingPlanner(), credentialProvider: provider);

        var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "add", "prod-admin", "--password", "secret"],
            CancellationToken.None));

        Assert.Equal(1, exitCode);
        Assert.Null(provider.LastOperation);
        Assert.Contains("Plaintext credential option", error);
        Assert.Contains("password", error);
    }

    [Fact]
    public async Task CredsCommandsCanUseFileBackedReferenceProvider()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-cli-creds-{Guid.NewGuid():N}");
        var storePath = Path.Combine(root, "references.json");
        var options = new DispatchOptions
        {
            CredentialProvider = "file",
            CredentialStorePath = storePath,
            ExpectedExitCodes = [0]
        };
        var provider = new FileCredentialProvider(Options.Create(options));
        var application = CreateApplication(
            new CapturingPlanner(),
            options: options,
            credentialProvider: provider);

        try
        {
            var (addExit, addOutput, addError) = await CaptureConsoleAsync(() => application.RunAsync(
                ["creds", "add", "prod-admin", "--username", @"CONTOSO\Admin"],
                CancellationToken.None));
            Assert.True(addExit == 0, $"Exit {addExit}. Stdout: {addOutput}. Stderr: {addError}");
            Assert.Contains("Provider: file", addOutput);

            var (listExit, listOutput, listError) = await CaptureConsoleAsync(() => application.RunAsync(
                ["creds", "list", "--output", "json"],
                CancellationToken.None));
            Assert.True(listExit == 0, $"Exit {listExit}. Stdout: {listOutput}. Stderr: {listError}");
            using var listJson = JsonDocument.Parse(listOutput);
            Assert.Equal("file", listJson.RootElement.GetProperty("providerName").GetString());
            Assert.True(listJson.RootElement.GetProperty("providerAvailable").GetBoolean());
            Assert.True(listJson.RootElement.GetProperty("succeeded").GetBoolean());
            Assert.Equal("prod-admin", listJson.RootElement.GetProperty("references")[0].GetProperty("name").GetString());
            Assert.False(listOutput.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.False(listOutput.Contains("secret", StringComparison.OrdinalIgnoreCase));

            var (testExit, _, testError) = await CaptureConsoleAsync(() => application.RunAsync(
                ["creds", "test", "prod-admin"],
                CancellationToken.None));
            Assert.True(testExit == 0, $"Stderr: {testError}");

            var persisted = await File.ReadAllTextAsync(storePath);
            Assert.Contains("prod-admin", persisted);
            Assert.DoesNotContain("password", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", persisted, StringComparison.OrdinalIgnoreCase);

            var (removeExit, _, removeError) = await CaptureConsoleAsync(() => application.RunAsync(
                ["creds", "remove", "prod-admin"],
                CancellationToken.None));
            Assert.True(removeExit == 0, $"Stderr: {removeError}");
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
    public async Task CredsCommandsUseConfigCredentialCatalogForPromptReferences()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Dispatch:CredentialProvider"] = "prompt",
                ["Credentials:prod-admin:Provider"] = "prompt",
                ["Credentials:prod-admin:Username"] = @"CONTOSO\prod.admin"
            })
            .Build();
        var options = new DispatchOptions
        {
            CredentialProvider = "prompt",
            ExpectedExitCodes = [0]
        };
        var provider = new ConfigurationCredentialProvider(configuration, Options.Create(options));
        var application = CreateApplication(
            new CapturingPlanner(),
            options: options,
            credentialProvider: provider);

        var (listExit, listOutput, listError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "list", "--output", "json"],
            CancellationToken.None));
        Assert.True(listExit == 0, $"Exit {listExit}. Stdout: {listOutput}. Stderr: {listError}");
        using var listJson = JsonDocument.Parse(listOutput);
        Assert.Equal("config", listJson.RootElement.GetProperty("providerName").GetString());
        Assert.Equal("prod-admin", listJson.RootElement.GetProperty("references")[0].GetProperty("name").GetString());
        Assert.Equal(@"CONTOSO\prod.admin", listJson.RootElement.GetProperty("references")[0].GetProperty("userName").GetString());
        Assert.False(listOutput.Contains("password", StringComparison.OrdinalIgnoreCase));

        var (testExit, _, testError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "test", "prod-admin"],
            CancellationToken.None));
        Assert.True(testExit == 0, $"Stderr: {testError}");

        var (addExit, addOutput, addError) = await CaptureConsoleAsync(() => application.RunAsync(
            ["creds", "add", "prod-admin"],
            CancellationToken.None));
        Assert.True(addExit == 0, $"Exit {addExit}. Stdout: {addOutput}. Stderr: {addError}");
        Assert.Contains("No enrollment required", addOutput);
    }

    [Fact]
    public async Task LogsListReadsLocalRunHistory()
    {
        var runRoot = CreateRunHistoryRoot(
            new DispatchRunResult(
                RunId: "20260618010000-a",
                StartedAt: DateTimeOffset.Parse("2026-06-18T01:00:00Z"),
                EndedAt: DateTimeOffset.Parse("2026-06-18T01:00:05Z"),
                RequestedBy: "tester",
                Transport: TransportKind.Psrp,
                PayloadType: PayloadKind.Command,
                PayloadName: "whoami",
                Targets:
                [
                    new TargetExecutionResult(
                        RunId: "20260618010000-a",
                        Target: "PC001",
                        Transport: TransportKind.Psrp,
                        PayloadType: PayloadKind.Command,
                        PayloadName: "whoami",
                        State: TargetExecutionState.Succeeded,
                        ExitCode: 0,
                        ExpectedExitCodes: [0],
                        StartedAt: DateTimeOffset.Parse("2026-06-18T01:00:00Z"),
                        EndedAt: DateTimeOffset.Parse("2026-06-18T01:00:04Z"),
                        FailureCategory: FailureCategory.None,
                        FailureMessage: null,
                        StdoutPath: @"C:\Dispatch\Tests\Runs\20260618010000-a\Targets\PC001\stdout.txt",
                        StderrPath: @"C:\Dispatch\Tests\Runs\20260618010000-a\Targets\PC001\stderr.txt")
                ],
                ResultPath: string.Empty),
            new DispatchRunResult(
                RunId: "20260618020000-b",
                StartedAt: DateTimeOffset.Parse("2026-06-18T02:00:00Z"),
                EndedAt: DateTimeOffset.Parse("2026-06-18T02:00:05Z"),
                RequestedBy: "tester",
                Transport: TransportKind.WinRm,
                PayloadType: PayloadKind.Script,
                PayloadName: "Fix.ps1",
                Targets:
                [
                    new TargetExecutionResult(
                        RunId: "20260618020000-b",
                        Target: "PC002",
                        Transport: TransportKind.WinRm,
                        PayloadType: PayloadKind.Script,
                        PayloadName: "Fix.ps1",
                        State: TargetExecutionState.Failed,
                        ExitCode: 1603,
                        ExpectedExitCodes: [0],
                        StartedAt: DateTimeOffset.Parse("2026-06-18T02:00:00Z"),
                        EndedAt: DateTimeOffset.Parse("2026-06-18T02:00:04Z"),
                        FailureCategory: FailureCategory.UnexpectedExitCode,
                        FailureMessage: "Installer failed.",
                        StdoutPath: @"C:\Dispatch\Tests\Runs\20260618020000-b\Targets\PC002\stdout.txt",
                        StderrPath: @"C:\Dispatch\Tests\Runs\20260618020000-b\Targets\PC002\stderr.txt")
                ],
                ResultPath: string.Empty));
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "list", "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            var runs = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal("20260618020000-b", runs[0].GetProperty("runId").GetString());
            Assert.Equal("20260618010000-a", runs[1].GetProperty("runId").GetString());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsShowLatestReadsMostRecentRunResult()
    {
        var result = new DispatchRunResult(
            RunId: "20260618030000-c",
            StartedAt: DateTimeOffset.Parse("2026-06-18T03:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T03:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618030000-c",
                    Target: "PC003",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T03:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T03:00:04Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618030000-c\Targets\PC003\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618030000-c\Targets\PC003\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(["logs", "show", "latest"], CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch run complete", output);
            Assert.Contains("20260618030000-c", output);
            Assert.Contains("Outputs", output);
            Assert.Contains("events.ndjson", output);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsTailReadsLatestRunEventsAsNdjson()
    {
        var result = new DispatchRunResult(
            RunId: "20260618040000-d",
            StartedAt: DateTimeOffset.Parse("2026-06-18T04:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T04:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618040000-d",
                    Target: "PC004",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T04:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T04:00:04Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618040000-d\Targets\PC004\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618040000-d\Targets\PC004\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var eventPath = Path.Combine(runRoot, result.RunId, "Admin", "events.ndjson");
        File.WriteAllText(
            eventPath,
            string.Join(
                Environment.NewLine,
                [
                    "{\"type\":\"run.started\",\"runId\":\"20260618040000-d\",\"timestamp\":\"2026-06-18T04:00:00Z\"}",
                    "{\"type\":\"progress\",\"runId\":\"20260618040000-d\",\"timestamp\":\"2026-06-18T04:00:02Z\",\"target\":\"PC004\",\"state\":\"Executing\",\"message\":\"Running whoami.\"}",
                    "{\"type\":\"result\",\"runId\":\"20260618040000-d\",\"timestamp\":\"2026-06-18T04:00:05Z\",\"result\":{\"runId\":\"20260618040000-d\"}}"
                ]));
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "tail", "--count", "2", "--output", "ndjson"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var first = JsonDocument.Parse(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[0]);
            using var second = JsonDocument.Parse(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)[1]);
            Assert.Equal("progress", first.RootElement.GetProperty("type").GetString());
            Assert.Equal("result", second.RootElement.GetProperty("type").GetString());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsTailRendersLatestRunEventsInRichMode()
    {
        var result = new DispatchRunResult(
            RunId: "20260618050000-e",
            StartedAt: DateTimeOffset.Parse("2026-06-18T05:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T05:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.WinRm,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618050000-e",
                    Target: "PC005",
                    Transport: TransportKind.WinRm,
                    PayloadType: PayloadKind.Script,
                    PayloadName: "Fix.ps1",
                    State: TargetExecutionState.Failed,
                    ExitCode: 1603,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T05:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T05:00:04Z"),
                    FailureCategory: FailureCategory.UnexpectedExitCode,
                    FailureMessage: "Installer failed.",
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618050000-e\Targets\PC005\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618050000-e\Targets\PC005\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var eventPath = Path.Combine(runRoot, result.RunId, "Admin", "events.ndjson");
        File.WriteAllText(
            eventPath,
            string.Join(
                Environment.NewLine,
                [
                    "{\"type\":\"execution.started\",\"runId\":\"20260618050000-e\",\"timestamp\":\"2026-06-18T05:00:01Z\",\"targetCount\":1}",
                    "{\"type\":\"progress\",\"runId\":\"20260618050000-e\",\"timestamp\":\"2026-06-18T05:00:03Z\",\"target\":\"PC005\",\"state\":\"Failed\",\"message\":\"Installer failed.\"}"
                ]));
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "tail", "latest", "--count", "5"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            Assert.Contains("Dispatch log tail", output);
            Assert.Contains("20260618050000-e", output);
            Assert.Contains("execution.started", output);
            Assert.Contains("Installer failed.", output);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsTailRejectsNonPositiveCount()
    {
        var result = new DispatchRunResult(
            RunId: "20260618060000-f",
            StartedAt: DateTimeOffset.Parse("2026-06-18T06:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T06:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets: [],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "tail", "--count", "0"],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Invalid Dispatch Command", error);
            Assert.Contains("Tail count must be greater than zero.", error);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsTailReturnsZeroEventsWhenEventFileIsMissing()
    {
        var result = new DispatchRunResult(
            RunId: "20260618070000-g",
            StartedAt: DateTimeOffset.Parse("2026-06-18T07:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T07:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets: [],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        File.Delete(Path.Combine(runRoot, result.RunId, "Admin", "events.ndjson"));
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "tail", "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            Assert.Equal("20260618070000-g", document.RootElement.GetProperty("runId").GetString());
            Assert.Empty(document.RootElement.GetProperty("events").EnumerateArray());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsExportWritesSelectedRunFiles()
    {
        var result = new DispatchRunResult(
            RunId: "20260618080000-h",
            StartedAt: DateTimeOffset.Parse("2026-06-18T08:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T08:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618080000-h",
                    Target: "PC008",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T08:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T08:00:04Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618080000-h\Targets\PC008\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618080000-h\Targets\PC008\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var eventPath = Path.Combine(runRoot, result.RunId, "Admin", "events.ndjson");
        File.WriteAllText(
            eventPath,
            "{\"type\":\"run.summary\",\"runId\":\"20260618080000-h\",\"timestamp\":\"2026-06-18T08:00:05Z\"}");
        var exportRoot = Path.Combine(Path.GetTempPath(), $"dispatch-export-{Guid.NewGuid():N}");
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "export", "latest", "--dest", exportRoot, "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("20260618080000-h", root.GetProperty("runId").GetString());

            var exportedResults = root.GetProperty("resultsJsonPath").GetString()!;
            var exportedEvents = root.GetProperty("eventsNdjsonPath").GetString()!;
            var exportedCsv = root.GetProperty("resultsCsvPath").GetString()!;

            Assert.True(File.Exists(exportedResults), exportedResults);
            Assert.True(File.Exists(exportedEvents), exportedEvents);
            Assert.True(File.Exists(exportedCsv), exportedCsv);
            Assert.Contains("20260618080000-h", File.ReadAllText(exportedResults));
            Assert.Contains("run.summary", File.ReadAllText(exportedEvents));
            var csv = File.ReadAllText(exportedCsv);
            Assert.Contains("RunId,RunStartedAt", csv);
            Assert.Contains("PC008", csv);
        }
        finally
        {
            if (Directory.Exists(runRoot))
            {
                Directory.Delete(runRoot, recursive: true);
            }

            if (Directory.Exists(exportRoot))
            {
                Directory.Delete(exportRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LogsExportRequiresDestination()
    {
        var result = new DispatchRunResult(
            RunId: "20260618090000-i",
            StartedAt: DateTimeOffset.Parse("2026-06-18T09:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T09:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets: [],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "export", "latest"],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Invalid Dispatch Command", error);
            Assert.Contains("logs export requires --dest <path>.", error);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsRetryBuildsRetryPlanForFailedCommandTargets()
    {
        var result = new DispatchRunResult(
            RunId: "20260618100000-j",
            StartedAt: DateTimeOffset.Parse("2026-06-18T10:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T10:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami /all",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618100000-j",
                    Target: "PC010",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami /all",
                    State: TargetExecutionState.Failed,
                    ExitCode: 1,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T10:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T10:00:04Z"),
                    FailureCategory: FailureCategory.ExecutionFailed,
                    FailureMessage: "Command failed.",
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618100000-j\Targets\PC010\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618100000-j\Targets\PC010\stderr.txt",
                    TransportMetadata: new Dictionary<string, string>
                    {
                        ["commandShell"] = "cmd"
                    }),
                new TargetExecutionResult(
                    RunId: "20260618100000-j",
                    Target: "PC011",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami /all",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T10:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T10:00:04Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618100000-j\Targets\PC011\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618100000-j\Targets\PC011\stderr.txt",
                    TransportMetadata: new Dictionary<string, string>
                    {
                        ["commandShell"] = "cmd"
                    })
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "retry", "latest", "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("20260618100000-j", root.GetProperty("runId").GetString());
            Assert.Equal(1, root.GetProperty("retryTargetCount").GetInt32());
            Assert.True(root.GetProperty("reexecutionSupported").GetBoolean());
            Assert.Contains("dispatch run cmd", root.GetProperty("suggestedCommand").GetString());
            Assert.Contains("PC010", root.GetProperty("suggestedCommand").GetString());
            Assert.DoesNotContain("PC011", root.GetProperty("suggestedCommand").GetString());
            var target = Assert.Single(root.GetProperty("targets").EnumerateArray());
            Assert.Equal("PC010", target.GetProperty("target").GetString());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsRetryDoesNotReconstructScriptPayloads()
    {
        var result = new DispatchRunResult(
            RunId: "20260618110000-k",
            StartedAt: DateTimeOffset.Parse("2026-06-18T11:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T11:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.WinRm,
            PayloadType: PayloadKind.Script,
            PayloadName: "Fix.ps1",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618110000-k",
                    Target: "PC012",
                    Transport: TransportKind.WinRm,
                    PayloadType: PayloadKind.Script,
                    PayloadName: "Fix.ps1",
                    State: TargetExecutionState.TimedOut,
                    ExitCode: null,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T11:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T11:00:04Z"),
                    FailureCategory: FailureCategory.TimedOut,
                    FailureMessage: "Timed out.",
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618110000-k\Targets\PC012\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618110000-k\Targets\PC012\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "retry", "latest", "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            Assert.False(document.RootElement.GetProperty("reexecutionSupported").GetBoolean());
            Assert.True(document.RootElement.GetProperty("suggestedCommand").ValueKind == JsonValueKind.Null);
            Assert.Contains("script path", document.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsRetryReportsNoRetryableTargets()
    {
        var result = new DispatchRunResult(
            RunId: "20260618120000-l",
            StartedAt: DateTimeOffset.Parse("2026-06-18T12:00:00Z"),
            EndedAt: DateTimeOffset.Parse("2026-06-18T12:00:05Z"),
            RequestedBy: "tester",
            Transport: TransportKind.Psrp,
            PayloadType: PayloadKind.Command,
            PayloadName: "whoami",
            Targets:
            [
                new TargetExecutionResult(
                    RunId: "20260618120000-l",
                    Target: "PC013",
                    Transport: TransportKind.Psrp,
                    PayloadType: PayloadKind.Command,
                    PayloadName: "whoami",
                    State: TargetExecutionState.Succeeded,
                    ExitCode: 0,
                    ExpectedExitCodes: [0],
                    StartedAt: DateTimeOffset.Parse("2026-06-18T12:00:00Z"),
                    EndedAt: DateTimeOffset.Parse("2026-06-18T12:00:04Z"),
                    FailureCategory: FailureCategory.None,
                    FailureMessage: null,
                    StdoutPath: @"C:\Dispatch\Tests\Runs\20260618120000-l\Targets\PC013\stdout.txt",
                    StderrPath: @"C:\Dispatch\Tests\Runs\20260618120000-l\Targets\PC013\stderr.txt")
            ],
            ResultPath: string.Empty);
        var runRoot = CreateRunHistoryRoot(result);
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "retry", "latest", "--output", "json"],
                CancellationToken.None));

            Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
            using var document = JsonDocument.Parse(output);
            Assert.Equal(0, document.RootElement.GetProperty("retryTargetCount").GetInt32());
            Assert.False(document.RootElement.GetProperty("reexecutionSupported").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("targets").EnumerateArray());
            Assert.Contains("no failed", document.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task LogsRetryReturnsNotFoundForMissingRun()
    {
        var runRoot = CreateRunHistoryRoot();
        var application = CreateApplication(new CapturingPlanner(), options: new DispatchOptions
        {
            ExpectedExitCodes = [0],
            LocalRunRoot = runRoot
        });

        try
        {
            var (exitCode, _, error) = await CaptureConsoleAsync(() => application.RunAsync(
                ["logs", "retry", "missing-run"],
                CancellationToken.None));

            Assert.Equal(1, exitCode);
            Assert.Contains("Dispatch Logs Not Found", error);
            Assert.Contains("missing-run", error);
        }
        finally
        {
            Directory.Delete(runRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RunCommandRouteUsesSharedRequestAndCommandPayload()
    {
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
            [
                "run",
                "cmd",
                "whoami",
                "--target",
                "PC001",
                "--transport",
                "winrm",
                "--plan",
                "--",
                "/all"
            ],
            CancellationToken.None));

        Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
        var payload = Assert.IsType<CommandPayload>(planner.LastRequest!.Payload);
        Assert.Equal("whoami /all", payload.CommandLine);
        Assert.Equal("cmd", payload.Shell);
        Assert.Null(payload.WorkingDirectory);
        Assert.Equal(TransportKind.WinRm, planner.LastRequest.Transport);
        Assert.True(planner.LastRequest.DryRun);
        Assert.Equal(["PC001"], planner.LastRequest.Targets.Select(static target => target.Name));
    }

    [Fact]
    public async Task RunExecutableRouteUsesSharedRequestAndCommandPayload()
    {
        var planner = new CapturingPlanner();
        var application = CreateApplication(planner);

        var (exitCode, output, error) = await CaptureConsoleAsync(() => application.RunAsync(
            [
                "run",
                "exe",
                @"C:\Tools\tool.exe",
                "--target",
                "PC001",
                "--transport",
                "winrm",
                "--plan",
                "--",
                "/quiet",
                "/norestart"
            ],
            CancellationToken.None));

        Assert.True(exitCode == 0, $"Exit {exitCode}. Stdout: {output}. Stderr: {error}");
        var payload = Assert.IsType<CommandPayload>(planner.LastRequest!.Payload);
        Assert.Equal(@"C:\Tools\tool.exe /quiet /norestart", payload.CommandLine);
        Assert.Equal("exe", payload.Shell);
        Assert.Null(payload.WorkingDirectory);
        Assert.Equal(TransportKind.WinRm, planner.LastRequest.Transport);
        Assert.True(planner.LastRequest.DryRun);
    }

    [Fact]
    public async Task SpectreDashboardRendererShowsRunStatusTargetPhaseAndFailures()
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
            var now = DateTimeOffset.UnixEpoch.AddSeconds(70);
            var dashboard = new SpectreRunDashboard(plan, DateTimeOffset.UnixEpoch, () => now);
            dashboard.Update(new DispatchExecutionProgress(
                plan.RunId,
                "PC001",
                TargetExecutionState.Executing,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                Details: new DispatchExecutionProgressDetails(
                    Operation: "winrm-upload",
                    Location: @"C:\ProgramData\Dispatch\Runs\run-test\script\Fix.ps1",
                    CompletedUnits: 1,
                    TotalUnits: 4,
                    UnitLabel: "chunks",
                    CompletedBytes: 1024,
                    TotalBytes: 4096)));
            dashboard.Update(new DispatchExecutionProgress(
                plan.RunId,
                "PC002",
                TargetExecutionState.Probing,
                DateTimeOffset.UnixEpoch.AddSeconds(2)));
            dashboard.Update(new DispatchExecutionProgress(
                plan.RunId,
                "PC002",
                TargetExecutionState.Failed,
                DateTimeOffset.UnixEpoch.AddSeconds(4),
                FailureCategory.ExecutionFailed,
                "Installer returned 1603."));

            using var writer = new StringWriter();
            dashboard.RenderSnapshot(writer);
            var output = writer.ToString();

            Assert.Contains("Dispatch Run", output);
            Assert.Contains("Completion", output);
            Assert.Contains("Outcome Chart", output);
            Assert.Contains("run-test", output);
            Assert.Contains("psexec", output);
            Assert.Contains("PC001", output);
            Assert.Contains("Running", output);
            Assert.Contains("Executing", output);
            Assert.Contains("Progress", output);
            Assert.Contains("01:09", output);
            Assert.Contains("PC002", output);
            Assert.Contains("Installer returned 1603", output);
            Assert.Contains("00:02", output);
            Assert.DoesNotContain(" 65%", output);
            Assert.True(output.IndexOf("PC001", StringComparison.Ordinal) < output.IndexOf("PC002", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }

    [Fact]
    public async Task DashboardLoopDoesNotThrowWhenEventsBeatHeartbeat()
    {
        var channel = Channel.CreateUnbounded<DispatchExecutionProgress>();
        var processed = new List<DispatchExecutionProgress>();
        var refreshCount = 0;

        var loopTask = SpectreLiveRunRenderer.RunDashboardLoopAsync(
            channel.Reader,
            processed.Add,
            () => refreshCount++,
            CancellationToken.None);

        await channel.Writer.WriteAsync(
            new DispatchExecutionProgress("run-test", "PC001", TargetExecutionState.Probing, DateTimeOffset.UnixEpoch));
        await Task.Delay(10);
        await channel.Writer.WriteAsync(
            new DispatchExecutionProgress("run-test", "PC001", TargetExecutionState.Executing, DateTimeOffset.UnixEpoch.AddMilliseconds(10)));
        channel.Writer.TryComplete();

        await loopTask;

        Assert.Equal(2, processed.Count);
        Assert.True(refreshCount >= 2);
    }

    [Fact]
    public async Task RunHelpUsesDispatchSpectreHelp()
    {
        var application = CreateApplication(new CapturingPlanner());

        var (exitCode, output, _) = await CaptureConsoleAsync(() => application.RunAsync(["run", "--help"], CancellationToken.None));
        var normalized = NormalizeWhitespace(output);

        Assert.Equal(0, exitCode);
        Assert.Contains("dispatch run", output);
        Assert.Contains("dispatch run ps", output);
        Assert.Contains("--no-progress", output);
        Assert.Contains("--no-color", output);
        Assert.Contains("--quiet", output);
        Assert.Contains("--verbose", output);
        Assert.Contains("--trace", output);
        Assert.Contains("Compatibility", output);
        Assert.Contains("Usage:", output);
        Assert.Contains("Current execution support: run ps through psexec, winrm, or psrp; run cmd and run exe through winrm or psrp", normalized);
        Assert.Contains("dispatch run cmd whoami --target PC001 --transport psrp", normalized);
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
        TextWriter? statusWriter = null,
        DispatchOptions? options = null,
        ICredentialProvider? credentialProvider = null,
        IRuntimeCredentialResolver? runtimeCredentialResolver = null,
        IRuntimeCredentialPrompt? runtimeCredentialPrompt = null) =>
        new(
            Options.Create(options ?? new DispatchOptions { ExpectedExitCodes = [0] }),
            planner,
            executor ?? new ThrowingExecutor(),
            doctor ?? new StaticDoctor(new DispatchDoctorReport([])),
            displayMode,
            statusWriter,
            credentialProvider,
            runtimeCredentialResolver,
            runtimeCredentialPrompt);

    private static IReadOnlyList<JsonDocument> ParseNdjson(string output) =>
        output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line))
            .ToArray();

    private static string CreateRunHistoryRoot(params DispatchRunResult[] results)
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-runs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        foreach (var result in results)
        {
            var adminRoot = Path.Combine(root, result.RunId, "Admin");
            Directory.CreateDirectory(adminRoot);
            var resultPath = Path.Combine(adminRoot, "results.json");
            var normalized = result with { ResultPath = resultPath };
            File.WriteAllText(resultPath, DispatchJson.Serialize(normalized));
            File.WriteAllText(Path.Combine(adminRoot, "events.ndjson"), "{\"type\":\"result\"}");
        }

        return root;
    }

    private static string NormalizeWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DispatchResolvedCredential CreateResolvedCredential(string referenceName)
    {
        var password = new SecureString();
        foreach (var character in "secret-value")
        {
            password.AppendChar(character);
        }

        password.MakeReadOnly();
        return new DispatchResolvedCredential(referenceName, @"SCF\prod.admin", "prompt", password);
    }

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
            var plannedRemoteScriptPath = request.Payload is ScriptPayload scriptPayload
                ? Path.Combine(@"C:\ProgramData\Dispatch\Runs\run-test\script", Path.GetFileName(scriptPayload.ScriptPath))
                : null;
            var plannedCommand = request.Payload switch
            {
                ScriptPayload => plannedRemoteScriptPath is null
                    ? null
                    : new DirectExecutionCommand(
                        "powershell.exe",
                        ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", plannedRemoteScriptPath]),
                CommandPayload commandPayload => new DirectExecutionCommand(
                    "cmd.exe",
                    ["/c", commandPayload.CommandLine]),
                _ => null
            };
            var targets = request.Targets
                .Select(target => new TargetExecution(
                    "run-test",
                    target,
                    TargetExecutionState.Pending,
                    Path.Combine(@"C:\Dispatch\Tests\run-test\Targets", target.Name),
                    Path.Combine(@"C:\Dispatch\Tests\run-test\Targets", target.Name, "result.json"),
                    plannedRemoteScriptPath,
                    plannedCommand))
                .ToArray();
            var job = new DispatchJob(
                RunId: "run-test",
                Targets: request.Targets,
                Payload: request.Payload,
                Transport: request.Transport,
                ExecutionContext: request.ExecutionContext,
                ScriptTransferPolicy: new ScriptTransferPolicy(
                    @"C:\ProgramData\Dispatch\Runs\run-test",
                    request.Payload is ScriptPayload),
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
                LocalResultsJsonPath: @"C:\Dispatch\Tests\run-test\Admin\results.json",
                LocalEventsNdjsonPath: @"C:\Dispatch\Tests\run-test\Admin\events.ndjson");
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
                        ResultPath: plan.Job.ResultPolicy.WritePerTargetJson ? target.PlannedLocalResultPath ?? string.Empty : string.Empty)
                ],
                ResultPath: plan.LocalResultsJsonPath);
        }
    }

    private sealed class CapturingSucceedingExecutor : IDispatchExecutor
    {
        public ExecutionPlan? LastPlan { get; private set; }

        public Task<DispatchRunResult> ExecuteAsync(ExecutionPlan plan, CancellationToken cancellationToken) =>
            ExecuteAsync(plan, NullDispatchExecutionObserver.Instance, cancellationToken);

        public async Task<DispatchRunResult> ExecuteAsync(
            ExecutionPlan plan,
            IDispatchExecutionObserver observer,
            CancellationToken cancellationToken)
        {
            LastPlan = plan;
            var target = Assert.Single(plan.Targets);
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
                        ResultPath: plan.Job.ResultPolicy.WritePerTargetJson ? target.PlannedLocalResultPath ?? string.Empty : string.Empty)
                ],
                ResultPath: plan.LocalResultsJsonPath);
        }
    }

    private sealed class RecordingRuntimeCredentialResolver(
        IReadOnlyDictionary<string, DispatchResolvedCredential>? resolvedCredentials = null) : IRuntimeCredentialResolver
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<RuntimeCredentialResolutionResult> ResolveAsync(
            IEnumerable<string> credentialReferences,
            CancellationToken cancellationToken)
        {
            var references = credentialReferences.ToArray();
            Requests.Add(references);

            return Task.FromResult(RuntimeCredentialResolutionResult.Success(
                resolvedCredentials ?? new Dictionary<string, DispatchResolvedCredential>(StringComparer.OrdinalIgnoreCase)));
        }
    }

    private sealed class RecordingRuntimeCredentialPrompt(params string[] passwords) : IRuntimeCredentialPrompt
    {
        private readonly Queue<string> passwords = new(passwords);

        public List<RuntimeCredentialPromptRequest> Requests { get; } = [];

        public Task<SecureString> PromptForPasswordAsync(
            RuntimeCredentialPromptRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var password = passwords.Count == 0 ? string.Empty : passwords.Dequeue();
            var secureString = new SecureString();
            foreach (var character in password)
            {
                secureString.AppendChar(character);
            }

            return Task.FromResult(secureString);
        }
    }

    private sealed class CapturingCredentialProvider(bool available, bool succeeds = true) : ICredentialProvider
    {
        private const string ProviderName = "test-provider";
        private readonly List<CredentialReference> references = [];

        public string? LastOperation { get; private set; }

        public CredentialAddRequest? LastAddRequest { get; private set; }

        public CredentialReferenceRequest? LastReferenceRequest { get; private set; }

        public Task<CredentialProviderStatus> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CredentialProviderStatus(
                ProviderName,
                available,
                available ? "test-provider is available." : "test-provider is unavailable."));

        public Task<CredentialProviderOperationResult> AddAsync(
            CredentialAddRequest request,
            CancellationToken cancellationToken)
        {
            LastOperation = "add";
            LastAddRequest = request;
            if (!available)
            {
                return Task.FromResult(Unavailable());
            }

            references.RemoveAll(reference => reference.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            references.Add(new CredentialReference(request.Name, request.UserName));
            return Task.FromResult(Success("Credential reference added.", references));
        }

        public Task<CredentialProviderOperationResult> ListAsync(CancellationToken cancellationToken)
        {
            LastOperation = "list";
            return Task.FromResult(available ? Success("Credential references listed.", references) : Unavailable());
        }

        public Task<CredentialProviderOperationResult> TestAsync(
            CredentialReferenceRequest request,
            CancellationToken cancellationToken)
        {
            LastOperation = "test";
            LastReferenceRequest = request;
            if (!available)
            {
                return Task.FromResult(Unavailable());
            }

            return Task.FromResult(succeeds
                ? Success("Credential reference is available.", [])
                : Failed($"Credential reference '{request.Name}' is not defined."));
        }

        public Task<CredentialProviderOperationResult> RemoveAsync(
            CredentialReferenceRequest request,
            CancellationToken cancellationToken)
        {
            LastOperation = "remove";
            LastReferenceRequest = request;
            if (!available)
            {
                return Task.FromResult(Unavailable());
            }

            references.RemoveAll(reference => reference.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(Success("Credential reference removed.", []));
        }

        private static CredentialProviderOperationResult Success(
            string message,
            IReadOnlyList<CredentialReference> credentialReferences) =>
            new(
                ProviderName,
                ProviderAvailable: true,
                Succeeded: true,
                message,
                credentialReferences);

        private static CredentialProviderOperationResult Unavailable() =>
            new(
                ProviderName,
                ProviderAvailable: false,
                Succeeded: false,
                "test-provider is unavailable.",
                []);

        private static CredentialProviderOperationResult Failed(string message) =>
            new(
                ProviderName,
                ProviderAvailable: true,
                Succeeded: false,
                message,
                []);
    }

    private sealed class StaticDoctor(DispatchDoctorReport report) : IDispatchDoctor
    {
        public DispatchDoctorReport Run() => report;
    }

}
