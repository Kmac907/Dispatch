namespace Dispatch.Core.Execution;

public interface IEndpointFileSystem
{
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken);

    Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> CopyDirectoryAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken);
}
