using Dispatch.Core.Models;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Dispatch.Transports.Psrp;

[SupportedOSPlatform("windows")]
public sealed class PsrpFileTransferClient : IPsrpFileTransferClient
{
    private const string ApplicationName = "/wsman";
    private const int HttpPort = 5985;
    private const int HttpsPort = 5986;
    private const string RemoteUploadWrapper = """
param(
  [string]$RemotePath,
  [string[]]$Chunks,
  [bool]$Overwrite,
  [bool]$Backup
)

$ErrorActionPreference = 'Stop'

try {
  $directory = [System.IO.Path]::GetDirectoryName($RemotePath)
  if (-not [string]::IsNullOrWhiteSpace($directory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
  }

  $backupPath = $null
  if ($Backup -and [System.IO.File]::Exists($RemotePath)) {
    $backupPath = $RemotePath + '.dispatch-backup.' + [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfff')
    [System.IO.File]::Copy($RemotePath, $backupPath, $false)
  }

  $fileMode = if ($Overwrite) { [System.IO.FileMode]::Create } else { [System.IO.FileMode]::CreateNew }
  $stream = [System.IO.File]::Open($RemotePath, $fileMode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
  try {
    foreach ($chunk in $Chunks) {
      if ([string]::IsNullOrWhiteSpace($chunk)) {
        continue
      }

      $bytes = [Convert]::FromBase64String($chunk)
      $stream.Write($bytes, 0, $bytes.Length)
    }
  }
  finally {
    $stream.Dispose()
  }

  $sha256 = [System.Security.Cryptography.SHA256]::Create()
  try {
    $readStream = [System.IO.File]::OpenRead($RemotePath)
    try {
      $hashBytes = $sha256.ComputeHash($readStream)
    }
    finally {
      $readStream.Dispose()
    }
  }
  finally {
    $sha256.Dispose()
  }

  $hash = -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
  [pscustomobject]@{
    succeeded = $true
    sha256 = $hash
    backupPath = $backupPath
    failureMessage = $null
  } | ConvertTo-Json -Compress
}
catch {
  [pscustomobject]@{
    succeeded = $false
    sha256 = $null
    backupPath = $null
    failureMessage = $_.Exception.Message
  } | ConvertTo-Json -Compress
}
""";

    public Task<PsrpFileTransferResult> UploadAsync(
        PsrpFileTransferRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(() => UploadInternal(request, cancellationToken), cancellationToken);

    private static PsrpFileTransferResult UploadInternal(
        PsrpFileTransferRequest request,
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
        EndpointAttempt? classifiedAttempt = null;

        foreach (var attempt in attempts)
        {
            try
            {
                var connectionInfo = CreateConnectionInfo(request, attempt);
                using var runspace = RunspaceFactory.CreateRunspace(connectionInfo);
                runspace.Open();

                using var powerShell = PowerShell.Create();
                powerShell.Runspace = runspace;
                powerShell.AddScript(RemoteUploadWrapper);
                powerShell.AddParameter("RemotePath", request.RemotePath);
                powerShell.AddParameter("Chunks", request.TransferPlan.Chunks.Select(static chunk => chunk.Base64Data).ToArray());
                powerShell.AddParameter("Overwrite", request.Overwrite);
                powerShell.AddParameter("Backup", request.Backup);

                var output = powerShell.Invoke();
                var errorText = string.Join(
                    Environment.NewLine,
                    powerShell.Streams.Error.Select(static record => record.ToString()).Where(static text => !string.IsNullOrWhiteSpace(text)));

                if (powerShell.HadErrors && string.IsNullOrWhiteSpace(errorText))
                {
                    errorText = "PSRP file upload reported an error without details.";
                }

                var metadata = CreateMetadata(request, attempt);
                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    metadata["uploadStage"] = "remote-command";
                    metadata["uploadStderr"] = errorText.Trim();
                    return PsrpFileTransferResult.Failed(
                        FailureCategory.ScriptTransferFailed,
                        $"PSRP upload failed for '{request.Target}' at '{request.RemotePath}': {errorText}",
                        metadata);
                }

                var payload = output.LastOrDefault()?.BaseObject?.ToString();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    metadata["uploadStage"] = "remote-command";
                    return PsrpFileTransferResult.Failed(
                        FailureCategory.ScriptTransferFailed,
                        $"PSRP upload did not return a result for '{request.Target}' at '{request.RemotePath}'.",
                        metadata);
                }

                return ParseResult(request, payload, metadata);
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
                classifiedAttempt ??= attempt;
            }
        }

        return PsrpFileTransferResult.Failed(
            classifiedFailure ?? FailureCategory.TransportUnavailable,
            $"Could not upload a file over PSRP to '{request.Target}'. {string.Join(" ", failures)}",
            CreateMetadata(request, classifiedAttempt ?? attempts[0]));
    }

    private static PsrpFileTransferResult ParseResult(
        PsrpFileTransferRequest request,
        string payload,
        Dictionary<string, string> metadata)
    {
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        var succeeded = root.TryGetProperty("succeeded", out var succeededProperty) && succeededProperty.ValueKind == JsonValueKind.True;
        if (!succeeded)
        {
            metadata["uploadStage"] = "remote-command";
            var failureMessage = root.TryGetProperty("failureMessage", out var failureProperty)
                ? failureProperty.GetString()
                : null;
            return PsrpFileTransferResult.Failed(
                FailureCategory.ScriptTransferFailed,
                string.IsNullOrWhiteSpace(failureMessage)
                    ? $"PSRP upload failed for '{request.Target}' at '{request.RemotePath}'."
                    : $"PSRP upload failed for '{request.Target}' at '{request.RemotePath}': {failureMessage}",
                metadata);
        }

        var reportedSha = root.TryGetProperty("sha256", out var shaProperty)
            ? shaProperty.GetString()
            : null;
        if (!string.Equals(reportedSha, request.TransferPlan.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            metadata["uploadStage"] = "hash-verify";
            metadata["uploadReportedSha256"] = reportedSha ?? string.Empty;
            metadata["uploadExpectedSha256"] = request.TransferPlan.ContentSha256;
            return PsrpFileTransferResult.Failed(
                FailureCategory.ScriptTransferFailed,
                $"PSRP upload reported SHA-256 '{reportedSha ?? "<empty>"}' for '{request.Target}', expected '{request.TransferPlan.ContentSha256}'.",
                metadata);
        }

        metadata["uploadStage"] = "completed";
        metadata["uploadReportedSha256"] = reportedSha!;
        metadata["uploadExpectedSha256"] = request.TransferPlan.ContentSha256;
        var backupPath = root.TryGetProperty("backupPath", out var backupProperty)
            ? backupProperty.GetString()
            : null;
        metadata["uploadBackupCreated"] = string.IsNullOrWhiteSpace(backupPath) ? "False" : "True";
        if (!string.IsNullOrWhiteSpace(backupPath))
        {
            metadata["uploadBackupPath"] = backupPath;
        }

        request.ProgressReporter?.Invoke(new PsrpUploadProgress(
            request.Target,
            request.RemotePath,
            request.TransferPlan.ChunkCount,
            request.TransferPlan.ChunkCount,
            request.TransferPlan.TotalBytes,
            request.TransferPlan.TotalBytes));
        return PsrpFileTransferResult.Success(metadata);
    }

    private static Dictionary<string, string> CreateMetadata(
        PsrpFileTransferRequest request,
        EndpointAttempt attempt)
    {
        var metadata = PsrpCommandClient.MergeConnectionMetadata(
            PsrpCommandClient.AttemptMetadata(attempt.Scheme, attempt.Port),
            null,
            PsrpConnectionKind.WsMan,
            PsrpAuthenticationKind.Default);
        metadata["uploadTarget"] = request.Target;
        metadata["uploadRemoteScriptPath"] = request.RemotePath;
        metadata["uploadChunkCount"] = request.TransferPlan.ChunkCount.ToString();
        metadata["uploadChunkSizeBytes"] = request.TransferPlan.ChunkSizeBytes.ToString();
        metadata["uploadOverwrite"] = request.Overwrite.ToString();
        metadata["uploadBackupRequested"] = request.Backup.ToString();
        metadata["uploadTransport"] = "psrp";
        return metadata;
    }

    private static WSManConnectionInfo CreateConnectionInfo(PsrpFileTransferRequest request, EndpointAttempt attempt)
    {
        var connectionInfo = new WSManConnectionInfo(
            attempt.UseSsl,
            request.Target,
            attempt.Port,
            ApplicationName,
            PsrpCommandClient.BuildShellUri(PsrpCommandClient.DefaultConfigurationName),
            PsrpCommandClient.CreatePowerShellCredential(request.Credential));
        connectionInfo.AuthenticationMechanism = PsrpCommandClient.MapAuthenticationMechanism(PsrpAuthenticationKind.Default);
        return connectionInfo;
    }

    private readonly record struct EndpointAttempt(bool UseSsl, int Port, string Scheme);
}
