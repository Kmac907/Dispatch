namespace Dispatch.Transports.Psrp;

public sealed record PsrpArtifactRequest(
    string Target,
    string RemoteFolder,
    TimeSpan? ExecutionTimeout = null,
    Action<PsrpArtifactProgress>? ProgressReporter = null,
    string? ConfigurationName = null);

public sealed record PsrpArtifactProgress(long CompletedBytes, long TotalBytes);
