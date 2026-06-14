using Dispatch.Core.Models;

namespace Dispatch.Core.Transports;

public sealed record TransportEndpointProbeResult(
    bool Succeeded,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
