namespace RoslynMcpServer.Tests;

internal sealed class TestRoot : IDisposable
{
    private TestRoot(string path)
    {
        Path = path;
    }

    public string Path { get; }

    public static TestRoot Create()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "roslyn-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return new TestRoot(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
