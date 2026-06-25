using Dispatch.Core.Credentials;
using Dispatch.Core.Execution;
using Dispatch.Core.Models;

namespace Dispatch.Transports.Psrp;

public interface IPsrpFileTransferClient
{
    Task<PsrpFileTransferResult> UploadAsync(
        PsrpFileTransferRequest request,
        CancellationToken cancellationToken);
}

public sealed record PsrpFileTransferRequest(
    string Target,
    string RemotePath,
    ScriptTransferPlan TransferPlan,
    Action<PsrpUploadProgress>? ProgressReporter = null,
    DispatchResolvedCredential? Credential = null,
    bool Overwrite = true,
    bool Backup = false);

public sealed record PsrpUploadProgress(
    string Target,
    string RemotePath,
    int ChunksUploaded,
    int TotalChunks,
    long BytesUploaded,
    long TotalBytes);

public sealed record PsrpFileTransferResult(
    bool Succeeded,
    FailureCategory FailureCategory,
    string? FailureMessage,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public static PsrpFileTransferResult Success(IReadOnlyDictionary<string, string>? metadata = null) =>
        new(true, FailureCategory.None, null, metadata);

    public static PsrpFileTransferResult Failed(
        FailureCategory failureCategory,
        string failureMessage,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(false, failureCategory, failureMessage, metadata);
}
