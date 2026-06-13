using System.Text.Json.Serialization;

namespace Dispatch.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<FailureCategory>))]
public enum FailureCategory
{
    None,
    TargetResolutionFailed,
    ProbeFailed,
    PayloadPreparationFailed,
    ScriptTransferFailed,
    SecretHandoffFailed,
    ExecutionFailed,
    UnexpectedExitCode,
    TimedOut,
    ArtifactCollectionFailed,
    CleanupFailed,
    Cancelled,
    TransportUnavailable,
    AuthenticationFailed,
    AuthorizationFailed,
    InternalError
}
