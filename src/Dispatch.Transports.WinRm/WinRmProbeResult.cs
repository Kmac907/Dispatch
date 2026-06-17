namespace Dispatch.Transports.WinRm;

public sealed record WinRmProbeResult(bool Succeeded, string? FailureMessage = null)
{
    public static WinRmProbeResult Success { get; } = new(true);

    public static WinRmProbeResult Failed(string failureMessage) => new(false, failureMessage);
}
