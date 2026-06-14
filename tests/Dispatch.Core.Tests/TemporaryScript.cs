namespace Dispatch.Core.Tests;

internal sealed class TemporaryScript : IDisposable
{
    private TemporaryScript(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TemporaryScript Create(string fileName = "DispatchTest.ps1")
    {
        var directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dispatch-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, fileName);
        File.WriteAllText(path, "Write-Output 'test'");
        return new TemporaryScript(path);
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
