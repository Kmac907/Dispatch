using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public sealed record TransportEndpointProbeRequest(
    ExecutionPlan Plan,
    TargetExecution Target);
