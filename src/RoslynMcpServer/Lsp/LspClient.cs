using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RoslynMcpServer.Infrastructure;

namespace RoslynMcpServer.Lsp;

public sealed class LspClient : ILspClient, IAsyncDisposable
{
    private readonly Stream input;
    private readonly Stream output;
    private readonly ILogger logger;
    private readonly int maxResponsePayloadBytes;
    private readonly ConcurrentDictionary<long, PendingRequest> pendingRequests = new();
    private readonly SemaphoreSlim inFlightLimit;
    private readonly SemaphoreSlim expensiveLimit;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly CancellationTokenSource disposeCts = new();
    private long nextId;
    private Task? readLoop;

    public LspClient(
        Stream input,
        Stream output,
        int maxInFlightRequests,
        int maxExpensiveRequests,
        ILogger logger,
        int maxResponsePayloadBytes = LspFraming.DefaultMaxContentLength)
    {
        this.input = input;
        this.output = output;
        this.logger = logger;
        this.maxResponsePayloadBytes = Math.Max(1, maxResponsePayloadBytes);
        inFlightLimit = new SemaphoreSlim(Math.Max(1, maxInFlightRequests), Math.Max(1, maxInFlightRequests));
        expensiveLimit = new SemaphoreSlim(Math.Max(1, maxExpensiveRequests), Math.Max(1, maxExpensiveRequests));
    }

    public event Action<string, JsonElement?>? NotificationReceived;

    public int PendingRequestCount => pendingRequests.Count;

    public void Start() => readLoop ??= Task.Run(ReadLoopAsync);

    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool isExpensive = false)
    {
        if (!await inFlightLimit.WaitAsync(0).ConfigureAwait(false))
        {
            throw new UserFacingException("too_many_lsp_requests", "Too many LSP requests are already in flight.");
        }

        var expensiveAcquired = false;
        if (isExpensive)
        {
            if (!await expensiveLimit.WaitAsync(0).ConfigureAwait(false))
            {
                inFlightLimit.Release();
                throw new UserFacingException("too_many_expensive_lsp_requests", "Too many expensive LSP requests are already in flight.");
            }

            expensiveAcquired = true;
        }

        var id = Interlocked.Increment(ref nextId);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, disposeCts.Token);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(method, tcs);

        if (!pendingRequests.TryAdd(id, pending))
        {
            if (expensiveAcquired)
            {
                expensiveLimit.Release();
            }

            inFlightLimit.Release();
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
                if (client.pendingRequests.TryRemove(requestId, out var removed))
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
            pendingRequests.TryRemove(id, out _);
            if (expensiveAcquired)
            {
                expensiveLimit.Release();
            }

            inFlightLimit.Release();
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
            logger.LogDebug(ex, "LSP shutdown did not complete cleanly.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposeCts.Cancel();
        foreach (var pending in pendingRequests.Values)
        {
            pending.Completion.TrySetCanceled();
        }

        if (readLoop is not null)
        {
            try
            {
                await readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        disposeCts.Dispose();
        inFlightLimit.Dispose();
        expensiveLimit.Dispose();
        writeLock.Dispose();
    }

    private async Task WriteMessageAsync(object payload, CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LspFraming.WriteAsync(input, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!disposeCts.IsCancellationRequested)
            {
                using var document = await LspFraming.ReadAsync(output, maxResponsePayloadBytes, disposeCts.Token).ConfigureAwait(false);
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
                    await ReplyToServerRequestAsync(id, requestMethod, disposeCts.Token).ConfigureAwait(false);
                }
                else if (root.TryGetProperty("id", out idElement) && idElement.TryGetInt64(out id))
                {
                    if (!pendingRequests.TryRemove(id, out var pending))
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
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "LSP read loop stopped.");
            var userFacingException = ex is InvalidDataException
                ? new UserFacingException("invalid_lsp_response", ex.Message, ex)
                : ex;
            CompletePending(userFacingException);
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
            logger.LogDebug(ex, "Failed to send LSP cancellation for request {RequestId}.", id);
        }
    }

    private void CompletePending(Exception exception)
    {
        foreach (var pair in pendingRequests.ToArray())
        {
            if (pendingRequests.TryRemove(pair.Key, out var pending))
            {
                pending.Completion.TrySetException(exception);
            }
        }
    }

    private sealed record PendingRequest(string Method, TaskCompletionSource<JsonElement> Completion);
}
