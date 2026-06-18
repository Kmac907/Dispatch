using Dispatch.Core.Models;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.Versioning;
using System.Text;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpArtifactClient : IPsrpArtifactClient
{
    private const string ApplicationName = "/wsman";
    private const int HttpPort = 5985;
    private const int HttpsPort = 5986;
    private const string RemoteArtifactWrapper = """
param(
  [string]$RemoteFolder
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $RemoteFolder -PathType Container)) {
  [pscustomobject]@{
    kind = 'status'
    status = 'missing'
  }
  return
}

$zipPath = Join-Path $env:TEMP ('dispatch-artifacts-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::CreateFromDirectory(
    $RemoteFolder,
    $zipPath,
    [System.IO.Compression.CompressionLevel]::Fastest,
    $false)

  $fileBytes = [System.IO.File]::ReadAllBytes($zipPath)
  $totalBytes = $fileBytes.Length
  $offset = 0
  $chunkSize = 24576

  while ($offset -lt $totalBytes) {
    $count = [Math]::Min($chunkSize, $totalBytes - $offset)
    $chunk = New-Object byte[] $count
    [Array]::Copy($fileBytes, $offset, $chunk, 0, $count)

    [pscustomobject]@{
      kind = 'progress'
      completedBytes = $offset + $count
      totalBytes = $totalBytes
    }

    [pscustomobject]@{
      kind = 'chunk'
      data = [Convert]::ToBase64String($chunk)
    }

    $offset += $count
  }
}
catch {
  throw
}
finally {
  if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
  }
}
""";

    public Task<PsrpArtifactDownloadResult> DownloadFolderAsync(
        PsrpArtifactRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(() => DownloadInternal(request, cancellationToken), cancellationToken);

    private static PsrpArtifactDownloadResult DownloadInternal(
        PsrpArtifactRequest request,
        CancellationToken cancellationToken)
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
                powerShell.AddScript(RemoteArtifactWrapper);
                powerShell.AddParameter("RemoteFolder", request.RemoteFolder);

                using var input = new PSDataCollection<PSObject>();
                using var output = new PSDataCollection<PSObject>();
                input.Complete();

                var base64 = new StringBuilder();
                var sync = new object();
                var missing = false;

                output.DataAdded += (_, eventArgs) =>
                {
                    lock (sync)
                    {
                        ProcessOutputObject(output[eventArgs.Index], request.ProgressReporter, base64, ref missing);
                    }
                };

                powerShell.Invoke<PSObject, PSObject>(input, output, settings: null);

                var errorText = string.Join(
                    Environment.NewLine,
                    powerShell.Streams.Error.Select(static record => record.ToString()).Where(static text => !string.IsNullOrWhiteSpace(text)));

                if (powerShell.HadErrors && string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "PSRP artifact collection reported an error without details.";
                }

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    return PsrpArtifactDownloadResult.Failed(
                        $"Artifact collection failed for '{request.Target}' at '{request.RemoteFolder}': {errorText}");
                }

                if (missing)
                {
                    return PsrpArtifactDownloadResult.Missing();
                }

                if (base64.Length == 0)
                {
                    return PsrpArtifactDownloadResult.Failed(
                        $"Artifact collection failed for '{request.Target}' at '{request.RemoteFolder}': the PSRP artifact download returned no archive content.");
                }

                try
                {
                    return PsrpArtifactDownloadResult.Success(Convert.FromBase64String(base64.ToString()));
                }
                catch (FormatException exception)
                {
                    return PsrpArtifactDownloadResult.Failed(
                        $"Artifact collection failed for '{request.Target}' at '{request.RemoteFolder}': invalid archive payload returned over PSRP ({exception.Message}).");
                }
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

        return PsrpArtifactDownloadResult.Failed(
            $"Could not collect PSRP artifacts from '{request.Target}'. {string.Join(" ", failures)}");
    }

    private static void ProcessOutputObject(
        PSObject output,
        Action<PsrpArtifactProgress>? progressReporter,
        StringBuilder base64,
        ref bool missing)
    {
        var kind = output.Properties["kind"]?.Value?.ToString();
        switch (kind)
        {
            case "status" when string.Equals(output.Properties["status"]?.Value?.ToString(), "missing", StringComparison.OrdinalIgnoreCase):
                missing = true;
                break;

            case "progress"
                when progressReporter is not null
                     && TryReadInt64(output.Properties["completedBytes"]?.Value, out var completedBytes)
                     && TryReadInt64(output.Properties["totalBytes"]?.Value, out var totalBytes)
                     && totalBytes > 0:
                progressReporter(new PsrpArtifactProgress(completedBytes, totalBytes));
                break;

            case "chunk":
            {
                var chunk = output.Properties["data"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(chunk))
                {
                    base64.Append(chunk);
                }

                break;
            }
        }
    }

    private static bool TryReadInt64(object? value, out long result)
    {
        switch (value)
        {
            case long int64Value:
                result = int64Value;
                return true;
            case int intValue:
                result = intValue;
                return true;
            default:
                return long.TryParse(value?.ToString(), out result);
        }
    }

    private static WSManConnectionInfo CreateConnectionInfo(PsrpArtifactRequest request, EndpointAttempt attempt)
    {
        var configurationName = PsrpCommandClient.NormalizeConfigurationName(request.ConfigurationName);
        var connectionInfo = new WSManConnectionInfo(
            attempt.UseSsl,
            request.Target,
            attempt.Port,
            ApplicationName,
            PsrpCommandClient.BuildShellUri(configurationName),
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
