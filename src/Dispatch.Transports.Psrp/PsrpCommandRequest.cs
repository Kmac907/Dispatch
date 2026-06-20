using Dispatch.Core.Credentials;
using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public sealed record PsrpCommandRequest(
    string Target,
    string Executable,
    string Arguments,
    string? WorkingDirectory,
    TimeSpan? ExecutionTimeout,
    string? ConfigurationName,
    PsrpConnectionKind ConnectionKind,
    PsrpAuthenticationKind AuthenticationKind,
    string? CertificateThumbprint,
    DispatchResolvedCredential? Credential = null);
