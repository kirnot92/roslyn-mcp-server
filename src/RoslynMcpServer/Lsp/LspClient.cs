using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Lsp;

public sealed class LspClient : ILspClient, IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _inFlightLimit;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _disposeCts = new();
    private long _nextId;
    private Task? _readLoop;

    public LspClient(Stream input, Stream output, int maxInFlightRequests, ILogger logger)
    {
        _input = input;
        _output = output;
        _logger = logger;
        _inFlightLimit = new SemaphoreSlim(Math.Max(1, maxInFlightRequests), Math.Max(1, maxInFlightRequests));
    }

    public event Action<string, JsonElement?>? NotificationReceived;

    public int PendingRequestCount => _pending.Count;

    public void Start() => _readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!await _inFlightLimit.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new UserFacingException("too_many_lsp_requests", "Too many LSP requests are already in flight.");
        }

        var id = Interlocked.Increment(ref _nextId);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, _disposeCts.Token);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(method, tcs);

        if (!_pending.TryAdd(id, pending))
        {
            throw new InvalidOperationException("Duplicate LSP request id.");
        }

        try
        {
            await WriteMessageAsync(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters ?? new { }
            }, linkedCts.Token).ConfigureAwait(false);

            using var cancellationRegistration = linkedCts.Token.UnsafeRegister(static state =>
            {
                var (client, requestId) = ((LspClient, long))state!;
                if (client._pending.TryRemove(requestId, out var removed))
                {
                    removed.Completion.TrySetCanceled();
                    _ = client.SendCancelRequestAsync(requestId);
                }
            }, (this, id));

            return await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new UserFacingException("request_timeout", $"LSP request timed out: {method}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _inFlightLimit.Release();
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken) =>
        WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters ?? new { }
        }, cancellationToken);

    public async Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync("shutdown", null, timeout, cancellationToken).ConfigureAwait(false);
            await NotifyAsync("exit", null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UserFacingException)
        {
            _logger.LogDebug(ex, "LSP shutdown did not complete cleanly.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        foreach (var pending in _pending.Values)
        {
            pending.Completion.TrySetCanceled();
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        _disposeCts.Dispose();
        _inFlightLimit.Dispose();
        _writeLock.Dispose();
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LspFraming.WriteAsync(_input, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_disposeCts.IsCancellationRequested)
            {
                using var document = await LspFraming.ReadAsync(_output, _disposeCts.Token).ConfigureAwait(false);
                if (document is null)
                {
                    break;
                }

                var root = document.RootElement.Clone();
                if (root.TryGetProperty("id", out var idElement) &&
                    idElement.TryGetInt64(out var id) &&
                    root.TryGetProperty("method", out var requestMethodElement))
                {
                    var requestMethod = requestMethodElement.GetString();
                    await ReplyToServerRequestAsync(id, requestMethod, _disposeCts.Token).ConfigureAwait(false);
                }
                else if (root.TryGetProperty("id", out idElement) && idElement.TryGetInt64(out id))
                {
                    if (!_pending.TryRemove(id, out var pending))
                    {
                        continue;
                    }

                    if (root.TryGetProperty("error", out var error))
                    {
                        pending.Completion.TrySetException(new InvalidOperationException(error.ToString()));
                    }
                    else if (root.TryGetProperty("result", out var result))
                    {
                        pending.Completion.TrySetResult(result.Clone());
                    }
                    else
                    {
                        pending.Completion.TrySetResult(default);
                    }
                }
                else if (root.TryGetProperty("method", out var methodElement))
                {
                    var method = methodElement.GetString();
                    if (!string.IsNullOrWhiteSpace(method))
                    {
                        JsonElement? parameters = root.TryGetProperty("params", out var paramsElement)
                            ? paramsElement.Clone()
                            : null;
                        NotificationReceived?.Invoke(method, parameters);
                    }
                }
            }

            CompletePending(new IOException("LSP stream closed."));
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LSP read loop stopped.");
            CompletePending(ex);
        }
    }

    private Task ReplyToServerRequestAsync(long id, string? method, CancellationToken cancellationToken)
    {
        object? result = string.Equals(method, "workspace/configuration", StringComparison.Ordinal)
            ? Array.Empty<object>()
            : null;

        return WriteMessageAsync(new
        {
            jsonrpc = "2.0",
            id,
            result
        }, cancellationToken);
    }

    private async Task SendCancelRequestAsync(long id)
    {
        try
        {
            await WriteMessageAsync(new
            {
                jsonrpc = "2.0",
                method = "$/cancelRequest",
                @params = new
                {
                    id
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Failed to send LSP cancellation for request {RequestId}.", id);
        }
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pair in _pending.ToArray())
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record PendingRequest(string Method, TaskCompletionSource<JsonElement> Completion);
}
