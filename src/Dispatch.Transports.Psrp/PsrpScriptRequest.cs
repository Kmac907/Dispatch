using Dispatch.Core.Credentials;
using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public sealed record PsrpScriptRequest(
    string Target,
    string ScriptPath,
    IReadOnlyList<string> ScriptArguments,
    TimeSpan? ExecutionTimeout,
    string RemoteScriptPath,
    string? ConfigurationName,
    PsrpConnectionKind ConnectionKind,
    PsrpAuthenticationKind AuthenticationKind,
    string? CertificateThumbprint,
    DispatchResolvedCredential? Credential = null);
