using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Transports.WinRm;

public interface IWinRmScriptTransferClient
{
    Task<WinRmScriptTransferResult> UploadAsync(
        WinRmScriptTransferRequest request,
        CancellationToken cancellationToken);
}

public sealed record WinRmScriptTransferRequest(
    string Target,
    string RemoteScriptPath,
    ScriptTransferPlan TransferPlan,
    Action<WinRmUploadProgress>? ProgressReporter = null,
    DispatchResolvedCredential? Credential = null,
    bool Overwrite = true,
    bool Backup = false);

public sealed record WinRmUploadProgress(
    string Target,
    string RemoteScriptPath,
    int ChunksUploaded,
    int TotalChunks,
    long BytesUploaded,
    long TotalBytes);

public sealed record WinRmScriptTransferResult(
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static WinRmScriptTransferResult Success(IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, FailureCategory.None, null, metadata);

    public static WinRmScriptTransferResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(false, failureCategory, failureMessage, metadata);
}
