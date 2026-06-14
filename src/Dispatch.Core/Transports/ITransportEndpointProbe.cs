using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public interface ITransportEndpointProbe
{
    TransportKind Kind { get; }

    Task<TransportEndpointProbeResult> ProbeAsync(
        TransportEndpointProbeRequest request,
        CancellationToken cancellationToken);
}
