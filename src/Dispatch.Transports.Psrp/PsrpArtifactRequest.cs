using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public sealed record PsrpArtifactRequest(
    string Target,
    string RemoteFolder,
    TimeSpan? ExecutionTimeout = null,
    Action<PsrpArtifactProgress>? ProgressReporter = null,
    string? ConfigurationName = null,
    PsrpConnectionKind ConnectionKind = PsrpConnectionKind.WsMan,
    PsrpAuthenticationKind AuthenticationKind = PsrpAuthenticationKind.Default,
    string? CertificateThumbprint = null);

public sealed record PsrpArtifactProgress(long CompletedBytes, long TotalBytes);
