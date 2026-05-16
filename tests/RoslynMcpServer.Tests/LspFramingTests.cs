using System.Text;
using System.Text.Json;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class LspFramingTests
{
    [Fact]
    public void ParseContentLength_ReadsHeaderCaseInsensitively()
    {
        var length = LspFraming.ParseContentLength("content-length: 42\r\n\r\n");

        Assert.Equal(42, length);
    }

    [Fact]
    public async Task WriteAndRead_RoundTripsJsonRpcMessage()
    {
        await using var stream = new MemoryStream();
        await LspFraming.WriteAsync(stream, new { jsonrpc = "2.0", id = 1, method = "test" }, CancellationToken.None);
        stream.Position = 0;

        using var document = await LspFraming.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(document);
        Assert.Equal("test", document.RootElement.GetProperty("method").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Read_ReturnsNullOnCleanEndOfStream()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(""));

        var document = await LspFraming.ReadAsync(stream, CancellationToken.None);

        Assert.Null(document);
    }
}
