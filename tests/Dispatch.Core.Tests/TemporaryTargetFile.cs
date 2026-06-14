namespace Dispatch.Core.Tests;

internal sealed class TemporaryTargetFile : IDisposable
{
    private TemporaryTargetFile(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryTargetFile Create(string content)
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dispatch-targets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "targets.txt");
        File.WriteAllText(path, content);
        return new TemporaryTargetFile(path);
    }

    public void Dispose()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (directory is not null && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
