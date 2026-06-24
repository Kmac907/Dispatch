using Dispatch.Core.Models;
using System.Text;

namespace Dispatch.Transports.WinRm;

public sealed class WinRmScriptTransferClient(IWinRmShellClient shellClient) : IWinRmScriptTransferClient
{
    public async Task<WinRmScriptTransferResult> UploadAsync(
        WinRmScriptTransferRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var frames = request.TransferPlan.Chunks
            .Select(static chunk => Encoding.ASCII.GetBytes(chunk.Base64Data + "\n"))
            .ToArray();
        var totalFrameBytes = frames.Sum(static frame => (long)frame.Length);

        var shellResult = await shellClient.ExecuteAsync(
                new WinRmShellCommandRequest(
                    request.Target,
                    "powershell.exe",
                    [
                        "-NoProfile",
                        "-ExecutionPolicy",
                        "Bypass",
                        "-EncodedCommand",
                        BuildUploaderEncodedCommand(request.RemoteScriptPath, request.Overwrite)
                    ],
                    frames,
                    ProgressReporter: progress =>
                    {
                        if (progress.Kind != WinRmShellTransferKind.Input)
                        {
                            return;
                        }

                        request.ProgressReporter?.Invoke(new WinRmUploadProgress(
                            request.Target,
                            request.RemoteScriptPath,
                            progress.FramesTransferred ?? 0,
                                progress.TotalFrames ?? request.TransferPlan.ChunkCount,
                                progress.BytesTransferred,
                                progress.TotalBytes ?? totalFrameBytes));
                    },
                    Credential: request.Credential),
                cancellationToken)
            .ConfigureAwait(false);

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["uploadTarget"] = request.Target,
            ["uploadRemoteScriptPath"] = request.RemoteScriptPath,
            ["uploadChunkCount"] = request.TransferPlan.ChunkCount.ToString(),
            ["uploadChunkSizeBytes"] = request.TransferPlan.ChunkSizeBytes.ToString(),
            ["uploadOverwrite"] = request.Overwrite.ToString()
        };

        if (shellResult.Metadata is not null)
        {
            foreach (var pair in shellResult.Metadata)
            {
                metadata[pair.Key] = pair.Value;
            }
        }

        if (!shellResult.Succeeded)
        {
            metadata["uploadStage"] = "shell";
            return WinRmScriptTransferResult.Failed(
                FailureCategory.ScriptTransferFailed,
                shellResult.FailureMessage ?? $"Raw WinRM upload failed for '{request.Target}'.",
                metadata);
        }

        if (shellResult.ExitCode != 0)
        {
            metadata["uploadStage"] = "remote-command";
            metadata["uploadExitCode"] = shellResult.ExitCode?.ToString() ?? string.Empty;
            return WinRmScriptTransferResult.Failed(
                FailureCategory.ScriptTransferFailed,
                $"Raw WinRM upload failed for '{request.Target}' with exit code {shellResult.ExitCode}. {shellResult.Stderr}".Trim(),
                metadata);
        }

        var reportedSha = ExtractLastNonEmptyLine(shellResult.Stdout);
        if (!string.Equals(reportedSha, request.TransferPlan.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            metadata["uploadStage"] = "hash-verify";
            metadata["uploadReportedSha256"] = reportedSha ?? string.Empty;
            metadata["uploadExpectedSha256"] = request.TransferPlan.ContentSha256;
            if (!string.IsNullOrWhiteSpace(shellResult.Stderr))
            {
                metadata["uploadStderr"] = shellResult.Stderr.Trim();
            }
            return WinRmScriptTransferResult.Failed(
                FailureCategory.ScriptTransferFailed,
                $"Raw WinRM upload reported SHA-256 '{reportedSha ?? "<empty>"}' for '{request.Target}', expected '{request.TransferPlan.ContentSha256}'.",
                metadata);
        }

        metadata["uploadStage"] = "completed";
        metadata["uploadReportedSha256"] = reportedSha!;
        metadata["uploadExpectedSha256"] = request.TransferPlan.ContentSha256;
        return WinRmScriptTransferResult.Success(metadata);
    }

    private static string BuildUploaderEncodedCommand(string remoteScriptPath, bool overwrite)
    {
        var remoteScriptPathBase64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(remoteScriptPath));
        var overwriteLiteral = overwrite ? "$true" : "$false";
        var script = $$"""
try {
$ErrorActionPreference = 'Stop'
$path = [System.Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('{{remoteScriptPathBase64}}'))
$overwrite = {{overwriteLiteral}}
$directory = [System.IO.Path]::GetDirectoryName($path)
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$fileMode = if ($overwrite) { [System.IO.FileMode]::Create } else { [System.IO.FileMode]::CreateNew }
$stream = [System.IO.File]::Open($path, $fileMode, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
try {
    while (($line = [Console]::In.ReadLine()) -ne $null) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $bytes = [Convert]::FromBase64String($line)
        $stream.Write($bytes, 0, $bytes.Length)
    }
}
finally {
    $stream.Dispose()
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $readStream = [System.IO.File]::OpenRead($path)
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
[Console]::Out.WriteLine($hash)
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
""";

        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string? ExtractLastNonEmptyLine(string value) =>
        value.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
}
