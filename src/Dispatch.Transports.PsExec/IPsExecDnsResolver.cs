namespace Dispatch.Transports.PsExec;

public interface IPsExecDnsResolver
{
    Task<PsExecProbeResult> ResolveAsync(string target, CancellationToken cancellationToken);
}
