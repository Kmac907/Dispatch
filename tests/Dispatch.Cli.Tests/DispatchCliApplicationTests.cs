using Dispatch.Cli;
using Dispatch.Core;
using Dispatch.Core.Configuration;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;
using Microsoft.Extensions.Options;
using System.Text.Json;

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
        Assert.Contains("direct command execution", normalized);
        Assert.Contains("live validation remains in progress", normalized);
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
                credential: prod-admin
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
    [InlineData("logs", "list")]
    [InlineData("creds", "list")]
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
            var dashboard = new SpectreRunDashboard(plan, DateTimeOffset.UnixEpoch);
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
            dashboard.RenderSnapshot(writer);
            var output = writer.ToString();

            Assert.Contains("Dispatch Run", output);
            Assert.Contains("Outcome Chart", output);
            Assert.Contains("run-test", output);
            Assert.Contains("psexec", output);
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
        Assert.Contains("Current execution support: run ps through psexec or winrm; run cmd and run exe through winrm", normalized);
        Assert.Contains("dispatch run cmd whoami --target PC001 --transport winrm", normalized);
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
        DispatchOptions? options = null) =>
        new(
            Options.Create(options ?? new DispatchOptions { ExpectedExitCodes = [0] }),
            planner,
            executor ?? new ThrowingExecutor(),
            doctor ?? new StaticDoctor(new DispatchDoctorReport([])),
            displayMode,
            statusWriter);

    private static IReadOnlyList<JsonDocument> ParseNdjson(string output) =>
        output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(static line => JsonDocument.Parse(line))
            .ToArray();

    private static string NormalizeWhitespace(string value) =>
        string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
