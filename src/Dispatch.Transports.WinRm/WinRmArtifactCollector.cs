using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Transports.WinRm;

[SupportedOSPlatform("windows")]
public sealed class WinRmArtifactCollector(IWinRmShellClient shellClient) : ITransportArtifactCollector
{
    private const string ArtifactProgressPrefix = "DISPATCH_ARTIFACT_PROGRESS=";
    public TransportKind Kind => TransportKind.WinRm;

    public async Task<ArtifactCollectionResult> CollectAsync(
        ExecutionPlan plan,
        TargetExecution target,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(target.PlannedLocalTargetRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No local target root was planned.");
        }

        if (string.IsNullOrWhiteSpace(plan.RemoteRunRoot))
        {
            return new ArtifactCollectionResult("skipped", [], "No remote run path was planned.");
        }

        var copiedArtifacts = new List<string>();
        var artifactFolders = GetArtifactFolders(plan.Job.ArtifactPolicy);

        foreach (var folder in artifactFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var remoteFolder = CombineWindowsPath(plan.RemoteRunRoot, folder);
            var download = await DownloadFolderArchiveAsync(
                plan,
                target,
                remoteFolder,
                cancellationToken,
                progressReporter).ConfigureAwait(false);

            if (!download.Succeeded)
            {
                return new ArtifactCollectionResult(
                    "failed",
                    copiedArtifacts,
                    download.FailureMessage ?? $"Artifact collection failed for target '{target.Target.Name}'.");
            }

            if (download.IsMissing || download.ZipBytes.Length == 0)
            {
                continue;
            }

            var localFolder = Path.Combine(target.PlannedLocalTargetRoot, SanitizeRelativePath(folder));
            Directory.CreateDirectory(localFolder);

            try
            {
                copiedArtifacts.AddRange(ExtractArtifacts(download.ZipBytes, localFolder, folder));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or ArgumentException)
            {
                return new ArtifactCollectionResult(
                    "failed",
                    copiedArtifacts,
                    $"Artifact collection failed for target '{target.Target.Name}': {exception.Message}");
            }
        }

        return new ArtifactCollectionResult(
            copiedArtifacts.Count > 0 ? "collected" : "not-found",
            copiedArtifacts);
    }

    private async Task<ArtifactDownloadResult> DownloadFolderArchiveAsync(
        ExecutionPlan plan,
        TargetExecution target,
        string remoteFolder,
        CancellationToken cancellationToken,
        Action<DispatchExecutionProgress>? progressReporter)
    {
        long? totalArchiveBytes = null;
        var script = BuildArtifactDownloadScript(remoteFolder);
        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await shellClient.ExecuteAsync(
                new WinRmShellCommandRequest(
                    target.Target.Name,
                    "powershell.exe",
                    ["-NoProfile", "-EncodedCommand", encodedScript],
                    [],
                    ProgressReporter: progress =>
                    {
                        if (progressReporter is null || progress.Kind != WinRmShellTransferKind.Error || string.IsNullOrWhiteSpace(progress.TextChunk))
                        {
                            return;
                        }

                        foreach (var line in progress.TextChunk
                                     .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            if (!line.StartsWith(ArtifactProgressPrefix, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            var payload = line[ArtifactProgressPrefix.Length..];
                            var parts = payload.Split('/', 2, StringSplitOptions.TrimEntries);
                            if (parts.Length != 2
                                || !long.TryParse(parts[0], out var completedBytes)
                                || !long.TryParse(parts[1], out var archiveBytes))
                            {
                                continue;
                            }

                            totalArchiveBytes = archiveBytes;
                            progressReporter(new DispatchExecutionProgress(
                                plan.RunId,
                                target.Target.Name,
                                TargetExecutionState.CollectingArtifacts,
                                DateTimeOffset.UtcNow,
                                Message: $"Downloading artifacts from {remoteFolder}",
                                Details: new DispatchExecutionProgressDetails(
                                    Operation: "artifact-download",
                                    Location: remoteFolder,
                                    CompletedBytes: completedBytes,
                                    TotalBytes: archiveBytes)));
                        }
                    }),
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            return ArtifactDownloadResult.Failed(
                result.FailureMessage ?? $"Raw WinRM artifact download failed for '{target.Target.Name}'.");
        }

        if (result.ExitCode == 3)
        {
            return ArtifactDownloadResult.Missing();
        }

        if (result.ExitCode is not 0)
        {
            var detail = string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;
            return ArtifactDownloadResult.Failed(
                $"Artifact collection failed for target '{target.Target.Name}' at '{remoteFolder}' with exit code {result.ExitCode}: {detail}".Trim());
        }

        var stdoutLines = result.Stdout
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line => !line.StartsWith(ArtifactProgressPrefix, StringComparison.Ordinal))
            .ToArray();
        var base64 = new string(string.Concat(stdoutLines).Where(static character => !char.IsWhiteSpace(character)).ToArray());
        if (string.IsNullOrWhiteSpace(base64))
        {
            return ArtifactDownloadResult.Failed(
                $"Artifact collection failed for target '{target.Target.Name}' at '{remoteFolder}': the WinRM artifact download returned no archive content.");
        }

        try
        {
            var zipBytes = Convert.FromBase64String(base64);
            if (progressReporter is not null && totalArchiveBytes is not null)
            {
                progressReporter(new DispatchExecutionProgress(
                    plan.RunId,
                    target.Target.Name,
                    TargetExecutionState.CollectingArtifacts,
                    DateTimeOffset.UtcNow,
                    Message: $"Downloaded artifacts from {remoteFolder}",
                    Details: new DispatchExecutionProgressDetails(
                        Operation: "artifact-download",
                        Location: remoteFolder,
                        CompletedBytes: zipBytes.Length,
                        TotalBytes: totalArchiveBytes)));
            }

            return ArtifactDownloadResult.Success(zipBytes);
        }
        catch (FormatException exception)
        {
            return ArtifactDownloadResult.Failed(
                $"Artifact collection failed for target '{target.Target.Name}' at '{remoteFolder}': invalid archive payload returned over raw WinRM ({exception.Message}).");
        }
    }

    private static IReadOnlyList<string> ExtractArtifacts(byte[] zipBytes, string localFolder, string relativeRoot)
    {
        using var archiveStream = new MemoryStream(zipBytes, writable: false);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        var copiedArtifacts = new List<string>();
        var localFolderFullPath = Path.GetFullPath(localFolder);
        var normalizedRelativeRoot = SanitizeRelativePath(relativeRoot).Replace('/', '\\');

        foreach (var entry in archive.Entries.Where(static entry => !string.IsNullOrEmpty(entry.Name)))
        {
            var normalizedEntryPath = entry.FullName.Replace('/', '\\');
            var destinationPath = Path.GetFullPath(Path.Combine(localFolder, normalizedEntryPath));
            if (!destinationPath.StartsWith(localFolderFullPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Artifact entry '{entry.FullName}' escapes the expected destination root.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            copiedArtifacts.Add(Path.Combine(normalizedRelativeRoot, normalizedEntryPath));
        }

        return copiedArtifacts;
    }

    private static string BuildArtifactDownloadScript(string remoteFolder)
    {
        var escapedRemoteFolder = remoteFolder.Replace("'", "''", StringComparison.Ordinal);
        return $$"""
$ErrorActionPreference = 'Stop'
$folderPath = '{{escapedRemoteFolder}}'
if (-not (Test-Path -LiteralPath $folderPath -PathType Container)) {
    exit 3
}

$zipPath = Join-Path $env:TEMP ('dispatch-artifacts-' + [Guid]::NewGuid().ToString('N') + '.zip')
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $folderPath,
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
        [Console]::Error.WriteLine('{{ArtifactProgressPrefix}}' + ($offset + $count) + '/' + $totalBytes)
        [Console]::Out.Write([Convert]::ToBase64String($chunk))
        $offset += $count
    }
    exit 0
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($_.Exception.Message)) {
        [Console]::Error.Write($_.Exception.Message)
    }
    exit 1
}
finally {
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
    }
}
""";
    }

    private sealed record ArtifactDownloadResult(bool Succeeded, bool IsMissing, byte[] ZipBytes, string? FailureMessage)
    {
        public static ArtifactDownloadResult Success(byte[] zipBytes) => new(true, false, zipBytes, null);

        public static ArtifactDownloadResult Missing() => new(true, true, [], null);

        public static ArtifactDownloadResult Failed(string message) => new(false, false, [], message);
    }

    private static IReadOnlyList<string> GetArtifactFolders(ArtifactPolicy policy) =>
        policy.Paths is { Count: > 0 }
            ? policy.Paths
            : ["logs", "artifacts"];

    private static string CombineWindowsPath(params string[] parts)
    {
        var first = parts[0].TrimEnd('\\');
        var rest = parts
            .Skip(1)
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .Select(static part => part.Trim('\\'));

        return string.Join('\\', new[] { first }.Concat(rest));
    }

    private static string SanitizeRelativePath(string value)
    {
        var invalidPathChars = Path.GetInvalidPathChars();
        var sanitized = string.Concat(value.Select(character => invalidPathChars.Contains(character) ? '_' : character));
        return sanitized.Trim('\\', '/');
    }
}
