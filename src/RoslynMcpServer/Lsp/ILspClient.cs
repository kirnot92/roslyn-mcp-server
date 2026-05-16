using System.Text.Json;

namespace RoslynMcpServer.Lsp;

public interface ILspClient
{
    event Action<string, JsonElement?>? NotificationReceived;

    int PendingRequestCount { get; }

    Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool isExpensive = false);

    Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken);

    Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
