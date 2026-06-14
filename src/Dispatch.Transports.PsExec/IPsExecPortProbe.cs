namespace Dispatch.Transports.PsExec;

public interface IPsExecPortProbe
{
    Task<PsExecProbeResult> CanConnectAsync(string target, int port, CancellationToken cancellationToken);
}
