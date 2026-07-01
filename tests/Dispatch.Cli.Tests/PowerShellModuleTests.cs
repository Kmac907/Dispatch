using System.Diagnostics;

namespace Dispatch.Cli.Tests;

public sealed class PowerShellModuleTests
{
    [Fact]
    public async Task ModuleImportsAndWrapsDispatchDiagnostics()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dispatch-module-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var fakeDispatch = Path.Combine(root, "dispatch.cmd");
        var argumentLog = Path.Combine(root, "dispatch-args.txt");
        var scriptPath = Path.Combine(root, "module-test.ps1");
        var moduleManifest = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "module", "Dispatch", "Dispatch.psd1"));

        await File.WriteAllTextAsync(fakeDispatch, """
            @echo off
            echo %*>> "%DISPATCH_TEST_ARGUMENT_LOG%"
            if "%1"=="version" (
              echo Dispatch
              echo Version: 9.9.9-test
              echo Command service: fake command surface
              exit /b 0
            )
            if "%1"=="doctor" (
              echo {"checks":[{"name":"fake","status":"pass","message":"ok","detail":"ok"}],"succeeded":true}
              exit /b 0
            )
            if "%1"=="run" (
              echo {"succeeded":true,"targets":[{"name":"PC001","state":"succeeded"}]}
              exit /b 0
            )
            echo unexpected arguments: %*
            exit /b 9
            """);

        await File.WriteAllTextAsync(scriptPath, $$"""
            $ErrorActionPreference = 'Stop'
            Import-Module -Name '{{EscapePowerShellSingleQuotedString(moduleManifest)}}' -Force

            $commands = Get-Command -Module Dispatch |
                Select-Object -ExpandProperty Name |
                Sort-Object
            $expected = @(
                'Get-DispatchVersion',
                'Invoke-DispatchCommand',
                'Invoke-DispatchExecutable',
                'Invoke-DispatchPowerShell',
                'Test-Dispatch'
            )
            if (($commands -join '|') -ne ($expected -join '|')) {
                throw "Unexpected exported commands: $($commands -join ', ')"
            }

            $env:DISPATCH_EXE = '{{EscapePowerShellSingleQuotedString(fakeDispatch)}}'
            $env:DISPATCH_TEST_ARGUMENT_LOG = '{{EscapePowerShellSingleQuotedString(argumentLog)}}'

            $version = Get-DispatchVersion
            if ($version.Product -ne 'Dispatch') {
                throw "Unexpected product: $($version.Product)"
            }
            if ($version.Version -ne '9.9.9-test') {
                throw "Unexpected version: $($version.Version)"
            }
            if ($version.CommandService -ne 'fake command surface') {
                throw "Unexpected command service: $($version.CommandService)"
            }

            $doctor = Test-Dispatch -Transport psrp
            if (-not $doctor.succeeded) {
                throw 'Doctor report did not succeed.'
            }
            if ($doctor.checks[0].name -ne 'fake') {
                throw "Unexpected doctor check: $($doctor.checks[0].name)"
            }
            if ($doctor.ExitCode -ne 0) {
                throw "Unexpected doctor exit code: $($doctor.ExitCode)"
            }

            $psResult = Invoke-DispatchPowerShell `
                -Script 'C:\Scripts\Fix Me.ps1' `
                -Target PC001 `
                -Transport psrp `
                -CredentialName admin-session `
                -Plan `
                -ExpectedExitCode 0,3010 `
                -Secret 'packageSas=prod-package-sas' `
                -ArgumentList '-Mode', 'Audit'
            if (-not $psResult.succeeded -or $psResult.targets[0].name -ne 'PC001') {
                throw 'PowerShell wrapper did not parse structured run output.'
            }
            if ($psResult.ExitCode -ne 0) {
                throw "Unexpected PowerShell wrapper exit code: $($psResult.ExitCode)"
            }

            $cmdResult = Invoke-DispatchCommand `
                -Command 'whoami' `
                -Target PC001 `
                -Transport winrm `
                -CredentialName admin-session `
                -Plan `
                -ArgumentList '/all'
            if (-not $cmdResult.succeeded -or $cmdResult.targets[0].name -ne 'PC001') {
                throw 'Command wrapper did not parse structured run output.'
            }
            if ($cmdResult.ExitCode -ne 0) {
                throw "Unexpected command wrapper exit code: $($cmdResult.ExitCode)"
            }

            $exeResult = Invoke-DispatchExecutable `
                -Path 'C:\Tools\tool.exe' `
                -Target PC001 `
                -Transport psrp `
                -CredentialName admin-session `
                -Plan `
                -ArgumentList '-Mode', 'Audit'
            if (-not $exeResult.succeeded -or $exeResult.targets[0].name -ne 'PC001') {
                throw 'Executable wrapper did not parse structured run output.'
            }
            if ($exeResult.ExitCode -ne 0) {
                throw "Unexpected executable wrapper exit code: $($exeResult.ExitCode)"
            }

            $argumentLines = Get-Content -LiteralPath '{{EscapePowerShellSingleQuotedString(argumentLog)}}'
            if (-not ($argumentLines | Where-Object { $_ -like '*run ps*--target PC001*--credential admin-session*--transport psrp*--expected-exit-code 0,3010*--plan*--secret packageSas=prod-package-sas*--output json*--no-progress*-- -Mode Audit*' })) {
                throw "PowerShell wrapper arguments were not mapped as expected: $($argumentLines -join ' | ')"
            }
            if (-not ($argumentLines | Where-Object { $_ -like '*run cmd whoami*--target PC001*--credential admin-session*--transport winrm*--plan*--output json*--no-progress*-- /all*' })) {
                throw "Command wrapper arguments were not mapped as expected: $($argumentLines -join ' | ')"
            }
            if (-not ($argumentLines | Where-Object { $_ -like '*run exe C:\Tools\tool.exe*--target PC001*--credential admin-session*--transport psrp*--plan*--output json*--no-progress*-- -Mode Audit*' })) {
                throw "Executable wrapper arguments were not mapped as expected: $($argumentLines -join ' | ')"
            }
            """);

        try
        {
            var results = new List<ProcessResult>();
            foreach (var shell in ResolvePowerShellCandidates())
            {
                results.Add(await RunPowerShellAsync(shell, scriptPath));
            }

            Assert.NotEmpty(results);
            foreach (var result in results)
            {
                Assert.True(
                    result.ExitCode == 0,
                    $"PowerShell shell '{result.ShellPath}' failed with exit code {result.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{result.StandardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{result.StandardError}");
                Assert.Equal(string.Empty, result.StandardError.Trim());
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(string shellPath, string scriptPath)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = shellPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-File");
        process.StartInfo.ArgumentList.Add(scriptPath);

        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new ProcessResult(shellPath, process.ExitCode, await stdout, await stderr);
    }

    private static IReadOnlyList<string> ResolvePowerShellCandidates()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var candidates = new[] { "pwsh.exe", "powershell.exe" };
        var resolved = new List<string>();

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidate in candidates)
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath) &&
                    !resolved.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    resolved.Add(fullPath);
                }
            }
        }

        if (resolved.Count == 0)
        {
            resolved.Add("powershell.exe");
        }

        return resolved;
    }

    private static string EscapePowerShellSingleQuotedString(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }

    private sealed record ProcessResult(string ShellPath, int ExitCode, string StandardOutput, string StandardError);
}
