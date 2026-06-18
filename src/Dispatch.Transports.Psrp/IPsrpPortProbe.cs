namespace Dispatch.Transports.Psrp;

public interface IPsrpPortProbe
{
    Task<PsrpProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken);
}
