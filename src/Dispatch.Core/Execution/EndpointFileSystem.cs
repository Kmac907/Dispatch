namespace Dispatch.Core.Execution;

internal sealed class EndpointFileSystem : IEndpointFileSystem
{
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    public Task CopyFileAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(sourcePath, destinationPath, overwrite);
        return Task.CompletedTask;
    }
}
