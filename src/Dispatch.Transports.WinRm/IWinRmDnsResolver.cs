namespace Dispatch.Transports.WinRm;

public interface IWinRmDnsResolver
{
    Task<WinRmProbeResult> ResolveAsync(string target, CancellationToken cancellationToken);
}
