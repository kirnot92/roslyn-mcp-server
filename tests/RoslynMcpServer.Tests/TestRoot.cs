namespace RoslynMcpServer.Tests;

internal sealed class TestRoot : IDisposable
{
    private const int DeleteMaxAttempts = 10;
    private static readonly TimeSpan DeleteRetryDelay = TimeSpan.FromMilliseconds(500);

    private TestRoot(string path)
    {
        this.Path = path;
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
        if (!Directory.Exists(this.Path))
        {
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(this.Path, recursive: true);
                return;
            }
            catch (Exception ex) when (attempt < DeleteMaxAttempts && IsTransientDeleteFailure(ex))
            {
                Thread.Sleep(DeleteRetryDelay);
            }
        }
    }

    private static bool IsTransientDeleteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;
}
