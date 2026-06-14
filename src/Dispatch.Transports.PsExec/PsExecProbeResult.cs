namespace Dispatch.Transports.PsExec;

public sealed record PsExecProbeResult(bool Succeeded, string? FailureMessage = null)
{
    public static PsExecProbeResult Success { get; } = new(true);

    public static PsExecProbeResult Failed(string failureMessage) => new(false, failureMessage);
}
