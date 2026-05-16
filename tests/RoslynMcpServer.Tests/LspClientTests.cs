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

        await Assert.ThrowsAsync<IOException>(() => responseTask.WaitAsync(TimeSpan.FromSeconds(2)));
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
        await Assert.ThrowsAsync<IOException>(() => first.WaitAsync(TimeSpan.FromSeconds(2)));
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

        public static LspClientHarness Create(int maxInFlightRequests)
        {
            var clientToServer = new BlockingStream();
            var serverToClient = new BlockingStream();

            var client = new LspClient(
                clientToServer,
                serverToClient,
                maxInFlightRequests,
                NullLogger<LspClient>.Instance);

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
        private readonly Channel<byte> _channel = Channel.CreateUnbounded<byte>();

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
                first = await _channel.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }

            buffer.Span[0] = first;
            var read = 1;
            while (read < buffer.Length && _channel.Reader.TryRead(out var next))
            {
                buffer.Span[read++] = next;
            }

            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            for (var i = 0; i < count; i++)
            {
                _channel.Writer.TryWrite(buffer[offset + i]);
            }
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            foreach (var item in buffer.ToArray())
            {
                await _channel.Writer.WriteAsync(item, cancellationToken);
            }
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _channel.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
