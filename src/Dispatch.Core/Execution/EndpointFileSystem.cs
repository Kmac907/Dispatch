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

    public Task<bool> DirectoryExistsAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Directory.Exists(path));
    }

    public Task<IReadOnlyList<string>> CopyDirectoryAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var copiedFiles = new List<string>();

        if (!Directory.Exists(sourcePath))
        {
            return Task.FromResult<IReadOnlyList<string>>(copiedFiles);
        }

        Directory.CreateDirectory(destinationPath);
        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourcePath, sourceFile);
            var destinationFile = Path.Combine(destinationPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, overwrite);
            copiedFiles.Add(destinationFile);
        }

        return Task.FromResult<IReadOnlyList<string>>(copiedFiles);
    }
}
