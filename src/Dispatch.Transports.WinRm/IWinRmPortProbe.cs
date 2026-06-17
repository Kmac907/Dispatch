namespace Dispatch.Transports.WinRm;

public interface IWinRmPortProbe
{
    Task<WinRmProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken);
}
