namespace Dispatch.Core.Models;

public sealed record DispatchRequest
{
    public DispatchRequest(
        DispatchPayload payload,
        IReadOnlyList<TargetSpec> targets,
        TransportKind transport,
        IReadOnlyList<int>? expectedExitCodes = null,
        int? throttle = null,
        bool dryRun = false)
    {
        Payload = payload;
        Targets = targets;
        Transport = transport;
        ExpectedExitCodes = expectedExitCodes ?? [0];
        Throttle = throttle;
        DryRun = dryRun;
    }

    public DispatchPayload Payload { get; init; }

    public IReadOnlyList<TargetSpec> Targets { get; init; }

    public TransportKind Transport { get; init; }

    public IReadOnlyList<int> ExpectedExitCodes { get; init; }

    public int? Throttle { get; init; }

    public bool DryRun { get; init; }
}
