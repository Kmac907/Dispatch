using Dispatch.Core.Models;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.Versioning;
using System.Text;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpScriptClient : IPsrpScriptClient
{
    private const string ApplicationName = "/wsman";
    private const string ShellUri = "http://schemas.microsoft.com/powershell/Microsoft.PowerShell";
    private const int HttpPort = 5985;
    private const int HttpsPort = 5986;
    private const string RemoteScriptWrapper = """
param(
  [string]$ScriptContent,
  [string]$RemoteScriptPath,
  [string[]]$ScriptArguments
)

$ErrorActionPreference = 'Stop'

try {
  $remoteDirectory = [System.IO.Path]::GetDirectoryName($RemoteScriptPath)
  if (-not [string]::IsNullOrWhiteSpace($remoteDirectory)) {
    [System.IO.Directory]::CreateDirectory($remoteDirectory) | Out-Null
  }

  $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
  [System.IO.File]::WriteAllText($RemoteScriptPath, $ScriptContent, $utf8NoBom)

  $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
  $startInfo.FileName = 'powershell.exe'
  $startInfo.UseShellExecute = $false
  $startInfo.RedirectStandardOutput = $true
  $startInfo.RedirectStandardError = $true
  $startInfo.CreateNoWindow = $true

  $argumentList = [System.Collections.Generic.List[string]]::new()
  $null = $argumentList.Add('-NoProfile')
  $null = $argumentList.Add('-ExecutionPolicy')
  $null = $argumentList.Add('Bypass')
  $null = $argumentList.Add('-File')
  $null = $argumentList.Add($RemoteScriptPath)

  foreach ($scriptArgument in $ScriptArguments) {
    $null = $argumentList.Add($scriptArgument)
  }

  $startInfo.Arguments = [string]::Join(' ', ($argumentList | ForEach-Object {
    if ([string]::IsNullOrEmpty($_)) {
      '""'
    }
    elseif ($_ -match '\s|"') {
      '"' + ($_.Replace('"', '\"')) + '"'
    }
    else {
      $_
    }
  }))

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

    public Task<PsrpCommandResult> ExecuteAsync(PsrpScriptRequest request, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteInternal(request, cancellationToken), cancellationToken);

    private static PsrpCommandResult ExecuteInternal(PsrpScriptRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string scriptContent;
        try
        {
            scriptContent = File.ReadAllText(request.ScriptPath, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PsrpCommandResult.Failed(
                FailureCategory.PayloadPreparationFailed,
                $"Could not read script '{request.ScriptPath}' for PSRP execution: {exception.Message}");
        }

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
                powerShell.AddScript(RemoteScriptWrapper);
                powerShell.AddParameter("ScriptContent", scriptContent);
                powerShell.AddParameter("RemoteScriptPath", request.RemoteScriptPath);
                powerShell.AddParameter("ScriptArguments", request.ScriptArguments.ToArray());

                var output = powerShell.Invoke();
                var errorText = string.Join(
                    Environment.NewLine,
                    powerShell.Streams.Error.Select(static record => record.ToString()).Where(static text => !string.IsNullOrWhiteSpace(text)));

                if (powerShell.HadErrors && string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "PSRP script execution reported an error without details.";
                }

                var payload = output.LastOrDefault()?.BaseObject?.ToString();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return PsrpCommandResult.Failed(
                        FailureCategory.ExecutionFailed,
                        string.IsNullOrWhiteSpace(errorText)
                            ? $"PSRP script execution did not return a result for '{request.Target}'."
                            : errorText,
                        metadata: PsrpCommandClient.AttemptMetadata(attempt.Scheme, attempt.Port));
                }

                return PsrpCommandClient.ParseResult(payload, errorText, attempt.Scheme, attempt.Port);
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
            $"Could not execute a PSRP script on '{request.Target}'. {string.Join(" ", failures)}");
    }

    private static WSManConnectionInfo CreateConnectionInfo(PsrpScriptRequest request, EndpointAttempt attempt)
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
            var milliseconds = PsrpCommandClient.ClampMilliseconds(timeout);
            connectionInfo.OpenTimeout = milliseconds;
            connectionInfo.OperationTimeout = milliseconds;
            connectionInfo.CancelTimeout = milliseconds;
        }

        return connectionInfo;
    }

    private readonly record struct EndpointAttempt(bool UseSsl, int Port, string Scheme);
}
