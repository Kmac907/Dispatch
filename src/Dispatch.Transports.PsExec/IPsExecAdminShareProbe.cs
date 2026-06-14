namespace Dispatch.Transports.PsExec;

public interface IPsExecAdminShareProbe
{
    Task<PsExecAdminShareProbeResult> ProbeDirectoryAsync(string path, CancellationToken cancellationToken);
}
