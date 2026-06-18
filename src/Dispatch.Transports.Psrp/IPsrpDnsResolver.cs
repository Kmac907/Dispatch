namespace Dispatch.Transports.Psrp;

public interface IPsrpDnsResolver
{
    Task<PsrpProbeResult> ResolveAsync(string target, CancellationToken cancellationToken);
}
