namespace Dispatch.Transports.Psrp;

public sealed record PsrpProbeResult(bool Succeeded, string? FailureMessage = null)
{
    public static PsrpProbeResult Success { get; } = new(true);

    public static PsrpProbeResult Failed(string failureMessage) => new(false, failureMessage);
}
