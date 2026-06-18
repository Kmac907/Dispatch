namespace Dispatch.Transports.Psrp;

public interface IPsrpArtifactClient
{
    Task<PsrpArtifactDownloadResult> DownloadFolderAsync(
        PsrpArtifactRequest request,
        CancellationToken cancellationToken);
}
