namespace Dispatch.Transports.Psrp;

public sealed record PsrpArtifactDownloadResult(
    bool Succeeded,
    bool IsMissing,
    byte[] ZipBytes,
    string? FailureMessage = null)
{
    public static PsrpArtifactDownloadResult Success(byte[] zipBytes) => new(true, false, zipBytes, null);

    public static PsrpArtifactDownloadResult Missing() => new(true, true, [], null);

    public static PsrpArtifactDownloadResult Failed(string message) => new(false, false, [], message);
}
