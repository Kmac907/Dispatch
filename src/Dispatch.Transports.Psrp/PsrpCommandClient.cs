using Dispatch.Core.Models;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpCommandClient : IPsrpCommandClient
{
    private const string ApplicationName = "/wsman";
    private const string ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";
    private const int HttpPort = 5985;
    private const int HttpsPort = 5986;
    private const string RemoteProcessWrapper = """
param(
  [string]$Executable,
  [string]$Arguments,
  [string]$WorkingDirectory
)

$ErrorActionPreference = 'Stop'

try {
  $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
  $startInfo.FileName = $Executable
  $startInfo.Arguments = $Arguments
  $startInfo.UseShellExecute = $false
  $startInfo.RedirectStandardOutput = $true
  $startInfo.RedirectStandardError = $true
  $startInfo.CreateNoWindow = $true

  if (-not [string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $startInfo.WorkingDirectory = $WorkingDirectory
  }

  $process = [System.Diagnostics.Process]::new()
  $process.StartInfo = $startInfo
  $null = $process.Start()
  $stdout = $process.StandardOutput.ReadToEnd()
  $stderr = $process.StandardError.ReadToEnd()
  $process.WaitForExit()

  [pscustomobject]@{
    succeeded = $true
    exitCode = $process.ExitCode
    stdout = $stdout
    stderr = $stderr
    failureMessage = $null
  } | ConvertTo-Json -Compress
}
catch {
  [pscustomobject]@{
    succeeded = $false
    exitCode = $null
    stdout = ''
    stderr = ''
    failureMessage = $_.Exception.Message
  } | ConvertTo-Json -Compress
}
""";

    public Task<PsrpCommandResult> ExecuteAsync(PsrpCommandRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteInternal(request, cancellationToken), cancellationToken);

    private static PsrpCommandResult ExecuteInternal(PsrpCommandRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var attempts = new[]
        {
            new EndpointAttempt(false, HttpPort, "http"),
            new EndpointAttempt(true, HttpsPort, "https")
        };

        var failures = new List<string>();
        FailureCategory? classifiedFailure = null;

        foreach (var attempt in attempts)
        {
            try
            {
                var connectionInfo = CreateConnectionInfo(request, attempt);
                using var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
                runspace.Open();

                using var powerShell = PowerShell.Create();
                powerShell.Runspace = runspace;
                powerShell.AddScript(RemoteProcessWrapper);
                powerShell.AddParameter("Executable", request.Executable);
                powerShell.AddParameter("Arguments", request.Arguments);
                powerShell.AddParameter("WorkingDirectory", request.WorkingDirectory ?? string.Empty);

                var output = powerShell.Invoke();
                var streamRecords = PsrpPowerShellStreamMapper.Capture(powerShell.Streams);
                var errorText = PsrpPowerShellStreamMapper.GetErrorText(streamRecords);

                if (powerShell.HadErrors && string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "PSRP command execution reported an error without details.";
                }

                var payload = output.LastOrDefault()?.BaseObject?.ToString();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return PsrpCommandResult.Failed(
                        FailureCategory.ExecutionFailed,
                        string.IsNullOrWhiteSpace(errorText)
                            ? $"PSRP command execution did not return a result for '{request.Target}'."
                            : errorText,
                        metadata: AttemptMetadata(attempt),
                        streamRecords: streamRecords);
                }

                var parsed = ParseResult(payload, errorText, attempt.Scheme, attempt.Port, streamRecords);
                return parsed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failureMessage = exception.Message;
                failures.Add($"{attempt.Scheme}://{request.Target}:{attempt.Port}/wsman: {failureMessage}");
                classifiedFailure ??= PsrpFailureClassifier.Classify(failureMessage);
            }
        }

        return PsrpCommandResult.Failed(
            classifiedFailure ?? FailureCategory.TransportUnavailable,
            $"Could not execute a PSRP command on '{request.Target}'. {string.Join(" ", failures)}");
    }

    private static WSManConnectionInfo CreateConnectionInfo(PsrpCommandRequest request, EndpointAttempt attempt)
    {
        var connectionInfo = new WSManConnectionInfo(
            attempt.UseSsl,
            request.Target,
            attempt.Port,
            ApplicationName,
            ShellUri,
            credential: null);

        if (request.ExecutionTimeout is { } timeout && timeout > TimeSpan.Zero)
        {
            var milliseconds = ClampMilliseconds(timeout);
            connectionInfo.OpenTimeout = milliseconds;
            connectionInfo.OperationTimeout = milliseconds;
            connectionInfo.CancelTimeout = milliseconds;
        }

        return connectionInfo;
    }

    internal static PsrpCommandResult ParseResult(
        string payload,
        string errorText,
        string scheme,
        int port,
        IReadOnlyList<PowerShellStreamRecord>? streamRecords = null)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var succeeded = root.TryGetProperty("succeeded", out var succeededProperty) && succeededProperty.ValueKind == JsonValueKind.True;
        var metadata = AttemptMetadata(scheme, port);

        if (!succeeded)
        {
            var failureMessage = root.TryGetProperty("failureMessage", out var failureProperty)
                ? failureProperty.GetString()
                : null;

            return PsrpCommandResult.Failed(
                FailureCategory.ExecutionFailed,
                string.IsNullOrWhiteSpace(failureMessage)
                    ? string.IsNullOrWhiteSpace(errorText)
                        ? "PSRP command execution failed."
                        : errorText
                    : failureMessage,
                metadata,
                streamRecords);
        }

        var exitCode = root.TryGetProperty("exitCode", out var exitCodeProperty)
            && exitCodeProperty.ValueKind == JsonValueKind.Number
            && exitCodeProperty.TryGetInt32(out var parsedExitCode)
                ? parsedExitCode
                : 0;
        var stdout = root.TryGetProperty("stdout", out var stdoutProperty) ? stdoutProperty.GetString() ?? string.Empty : string.Empty;
        var stderr = root.TryGetProperty("stderr", out var stderrProperty) ? stderrProperty.GetString() ?? string.Empty : string.Empty;

        if (!string.IsNullOrWhiteSpace(errorText))
        {
            stderr = string.IsNullOrWhiteSpace(stderr)
                ? errorText
                : $"{stderr}{Environment.NewLine}{errorText}";
        }

        return PsrpCommandResult.Success(exitCode, stdout, stderr, metadata, streamRecords);
    }

    private static Dictionary<string, string> AttemptMetadata(EndpointAttempt attempt) =>
        AttemptMetadata(attempt.Scheme, attempt.Port);

    internal static Dictionary<string, string> AttemptMetadata(string scheme, int port) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["scheme"] = scheme,
            ["port"] = port.ToString(),
            ["protocol"] = "psrp-over-wsman"
        };

    internal static int ClampMilliseconds(TimeSpan timeout)
    {
        var milliseconds = timeout.TotalMilliseconds;
        if (milliseconds <= 0)
        {
            return 1000;
        }

        if (milliseconds > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)milliseconds;
    }

    private readonly record struct EndpointAttempt(bool UseSsl, int Port, string Scheme);
}
