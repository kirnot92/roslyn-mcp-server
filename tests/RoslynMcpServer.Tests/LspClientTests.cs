using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using RoslynMcpServer.Infrastructure;
using RoslynMcpServer.Lsp;

namespace RoslynMcpServer.Tests;

public sealed class LspClientTests
{
    [Fact]
    public async Task RequestAsync_DoesNotTreatServerRequestWithSameIdAsResponse()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4);
        var responseTask = harness.Client.RequestAsync(
            "initialize",
            new { },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);
        var id = clientRequest.RootElement.GetProperty("id").GetInt64();

        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            id,
            method = "workspace/configuration",
            @params = new { }
        }, CancellationToken.None);

        using var serverRequestResponse = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(serverRequestResponse);
        Assert.Equal(id, serverRequestResponse.RootElement.GetProperty("id").GetInt64());
        Assert.True(serverRequestResponse.RootElement.TryGetProperty("result", out _));
        Assert.False(responseTask.IsCompleted);

        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                capabilities = new { }
            }
        }, CancellationToken.None);

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(response.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task ReadLoop_FailsPendingRequestWhenServerStreamCloses()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4);
        var responseTask = harness.Client.RequestAsync(
            "initialize",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);

        harness.ServerOutput.Dispose();

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("lsp_connection_closed", ex.Code);
    }

    [Fact]
    public async Task RequestAsync_EnforcesInFlightLimitAtomically()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 1);
        var first = harness.Client.RequestAsync(
            "initialize",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Client.RequestAsync("shutdown", new { }, TimeSpan.FromSeconds(1), CancellationToken.None));

        Assert.Equal("too_many_lsp_requests", ex.Code);
        harness.ServerOutput.Dispose();
        var closed = await Assert.ThrowsAsync<UserFacingException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("lsp_connection_closed", closed.Code);
    }

    [Fact]
    public async Task RequestAsync_EnforcesExpensiveRequestLimit()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4, maxExpensiveRequests: 1);
        var first = harness.Client.RequestAsync(
            "textDocument/references",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None,
            isExpensive: true);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Client.RequestAsync(
                "workspace/symbol",
                new { },
                TimeSpan.FromSeconds(1),
                CancellationToken.None,
                isExpensive: true));

        Assert.Equal("too_many_expensive_lsp_requests", ex.Code);

        var hover = harness.Client.RequestAsync(
            "textDocument/hover",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);
        using var hoverRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(hoverRequest);

        harness.ServerOutput.Dispose();
        var firstClosed = await Assert.ThrowsAsync<UserFacingException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
        var hoverClosed = await Assert.ThrowsAsync<UserFacingException>(() => hover.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("lsp_connection_closed", firstClosed.Code);
        Assert.Equal("lsp_connection_closed", hoverClosed.Code);
    }

    [Fact]
    public async Task RequestAsync_ReturnsUserFacingErrorWhenResponsePayloadExceedsLimit()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4, maxResponsePayloadBytes: 32);
        var responseTask = harness.Client.RequestAsync(
            "textDocument/documentSymbol",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);
        var id = clientRequest.RootElement.GetProperty("id").GetInt64();

        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                value = new string('x', 128)
            }
        }, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("invalid_lsp_response", ex.Code);
    }

    [Fact]
    public async Task RequestAsync_FailsNewRequestsImmediatelyAfterReadLoopFault()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4, maxResponsePayloadBytes: 32);
        var responseTask = harness.Client.RequestAsync(
            "textDocument/documentSymbol",
            new { },
            TimeSpan.FromSeconds(30),
            CancellationToken.None);

        using var clientRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(clientRequest);
        var id = clientRequest.RootElement.GetProperty("id").GetInt64();

        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            id,
            result = new
            {
                value = new string('x', 128)
            }
        }, CancellationToken.None);

        var first = await Assert.ThrowsAsync<UserFacingException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("invalid_lsp_response", first.Code);
        Assert.True(harness.Client.IsFaulted);

        var second = await Assert.ThrowsAsync<UserFacingException>(() =>
            harness.Client.RequestAsync(
                "textDocument/hover",
                new { },
                TimeSpan.FromSeconds(30),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("invalid_lsp_response", second.Code);
        Assert.Equal(0, harness.Client.PendingRequestCount);
    }

    [Fact]
    public async Task ReadLoop_RecordsReceivedNotificationMethods()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 4);

        Assert.False(harness.Client.HasReceivedNotification("workspace/projectInitializationComplete"));
        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            method = "workspace/projectInitializationComplete",
            @params = new { }
        }, CancellationToken.None);

        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (harness.Client.HasReceivedNotification("workspace/projectInitializationComplete"))
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("Notification method was not recorded by the LSP read loop.");
    }

    [Fact]
    public async Task RequestAsync_RemovesPendingRequestAfterTimeoutAndAllowsNextRequest()
    {
        await using var harness = LspClientHarness.Create(maxInFlightRequests: 1);
        var timedOut = harness.Client.RequestAsync(
            "textDocument/definition",
            new { },
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        using var timedOutRequest = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None);
        Assert.NotNull(timedOutRequest);

        var ex = await Assert.ThrowsAsync<UserFacingException>(() => timedOut.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("request_timeout", ex.Code);
        Assert.Equal(0, harness.Client.PendingRequestCount);

        var next = harness.Client.RequestAsync(
            "textDocument/hover",
            new { },
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        using var firstMessage = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        using var secondMessage = await LspFraming.ReadAsync(harness.ServerInput, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(firstMessage);
        Assert.NotNull(secondMessage);
        var messages = new[] { firstMessage.RootElement, secondMessage.RootElement };
        Assert.Contains(messages, message =>
            message.TryGetProperty("method", out var methodElement) &&
            methodElement.GetString() == "$/cancelRequest");
        var nextRequest = messages.Single(message => message.TryGetProperty("id", out _) && message.TryGetProperty("method", out _));
        var id = nextRequest.GetProperty("id").GetInt64();
        await LspFraming.WriteAsync(harness.ServerOutput, new
        {
            jsonrpc = "2.0",
            id,
            result = new { contents = "ok" }
        }, CancellationToken.None);

        var response = await next.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("ok", response.GetProperty("contents").GetString());
    }

    private sealed class LspClientHarness : IAsyncDisposable
    {
        private LspClientHarness(BlockingStream serverInput, BlockingStream serverOutput, LspClient client)
        {
            ServerInput = serverInput;
            ServerOutput = serverOutput;
            Client = client;
            Client.Start();
        }

        public BlockingStream ServerInput { get; }
        public BlockingStream ServerOutput { get; }
        public LspClient Client { get; }

        public static LspClientHarness Create(
            int maxInFlightRequests,
            int maxExpensiveRequests = 2,
            int maxResponsePayloadBytes = LspFraming.DefaultMaxContentLength)
        {
            var clientToServer = new BlockingStream();
            var serverToClient = new BlockingStream();

            var client = new LspClient(
                clientToServer,
                serverToClient,
                maxInFlightRequests,
                maxExpensiveRequests,
                NullLogger<LspClient>.Instance,
                maxResponsePayloadBytes);

            return new LspClientHarness(clientToServer, serverToClient, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            ServerInput.Dispose();
            ServerOutput.Dispose();
        }
    }

    private sealed class BlockingStream : Stream
    {
        private readonly Channel<byte> channel = Channel.CreateUnbounded<byte>();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            byte first;
            try
            {
                first = await this.channel.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            buffer.Span[0] = first;
            var read = 1;
            while (read < buffer.Length && this.channel.Reader.TryRead(out var next))
            {
                buffer.Span[read++] = next;
            }

            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                this.channel.Writer.TryWrite(buffer[offset + i]);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            foreach (var item in buffer.ToArray())
            {
                await this.channel.Writer.WriteAsync(item, cancellationToken);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                this.channel.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
