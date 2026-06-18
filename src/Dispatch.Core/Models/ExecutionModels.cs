namespace Dispatch.Core.Models;

public enum PsrpConnectionKind
{
    WsMan,
    Ssh
}

public enum PsrpAuthenticationKind
{
    Default,
    Negotiate,
    Kerberos,
    Basic,
    Certificate,
    CredSsp
}

public sealed record TargetSpec(string Name, string? Source = null, string? CredentialReference = null);

public sealed record ExecutionContextOptions(
    bool RunAsSystem = false,
    string? WorkingDirectory = null,
    string? PsrpConfigurationName = null,
    PsrpConnectionKind PsrpConnectionKind = PsrpConnectionKind.WsMan,
    PsrpAuthenticationKind PsrpAuthentication = PsrpAuthenticationKind.Default,
    string? PsrpCertificateThumbprint = null);

public sealed record ScriptTransferPolicy(string RemoteRoot, bool RequiresEndpointLocalScriptPath);

public sealed record TimeoutPolicy(TimeSpan? ExecutionTimeout = null, TimeSpan? ConnectionTimeout = null);

public sealed record RetryPolicy(int MaxAttempts = 1);

public sealed record ArtifactPolicy(IReadOnlyList<string>? Paths = null);

public sealed record ResultPolicy(
    string LocalRunRoot,
    bool WriteJson = true,
    bool WriteCsv = false,
    bool WritePerTargetJson = false,
    bool WriteTextLog = false,
    bool WriteEventStream = true);
