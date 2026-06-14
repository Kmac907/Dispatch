namespace Dispatch.Core.Execution;

public interface IEndpointFileSystem
{
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken);
}
