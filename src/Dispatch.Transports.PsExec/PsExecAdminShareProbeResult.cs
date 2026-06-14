namespace Dispatch.Transports.PsExec;

public enum PsExecAdminShareFailureKind
{
    Unreachable,
    Authorization
}

public sealed record PsExecAdminShareProbeResult(
    bool Succeeded,
    string? FailureMessage = null,
    PsExecAdminShareFailureKind FailureKind = PsExecAdminShareFailureKind.Unreachable)
{
    public static PsExecAdminShareProbeResult Success { get; } = new(true);

    public static PsExecAdminShareProbeResult Failed(
        string failureMessage,
        PsExecAdminShareFailureKind failureKind = PsExecAdminShareFailureKind.Unreachable) =>
        new(false, failureMessage, failureKind);
}
