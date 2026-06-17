using Dispatch.Core.Models;

namespace Dispatch.Core.Execution;

public enum ScriptTransferMode
{
    AdminShareCopy,
    WinRmChunkedBase64
}

public sealed record ScriptTransferChunk(
    int Index,
    int Offset,
    int ByteLength,
    string Sha256,
    string Base64Data);

public sealed record ScriptTransferPlan(
    ScriptTransferMode Mode,
    int TotalBytes,
    string ContentSha256,
    int ChunkSizeBytes,
    IReadOnlyList<ScriptTransferChunk> Chunks)
{
    public int ChunkCount => Chunks.Count;
}

public sealed record TargetScriptManifest(
    TargetSpec Target,
    string RemoteScriptPath,
    string? AdminShareScriptPath,
    ScriptTransferPlan? TransferPlan = null);

public sealed record ScriptExecutionManifest(
    string SourceScriptPath,
    IReadOnlyList<string> ScriptArguments,
    string RemoteScriptDirectory,
    IReadOnlyList<TargetScriptManifest> Targets);

public sealed record TargetScriptPreparationResult(
    TargetSpec Target,
    string RemoteScriptPath,
    string? AdminShareScriptPath,
    bool Succeeded,
    ScriptTransferPlan? TransferPlan = null,
    FailureCategory FailureCategory = FailureCategory.None,
    string? FailureMessage = null);

public sealed record ScriptPreparationResult(
    ScriptExecutionManifest? Manifest,
    IReadOnlyList<TargetScriptPreparationResult> Targets)
{
    public bool Succeeded => Targets.Count > 0 && Targets.All(static target => target.Succeeded);
}
