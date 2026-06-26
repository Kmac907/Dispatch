namespace Dispatch.Core.Models;

public sealed record ScriptSecretReference(
    string Name,
    string ReferenceName);

public sealed record ScriptSecretHandoffPlan(
    string Name,
    string ReferenceName,
    string RemotePath,
    string ScriptArgumentName,
    string RedactedValue = "[redacted]");
