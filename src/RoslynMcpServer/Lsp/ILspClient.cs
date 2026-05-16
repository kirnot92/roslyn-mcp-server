using System.Text.Json;

namespace RoslynMcpServer.Lsp;

public interface ILspClient
{
    event Action<string, JsonElement?>? NotificationReceived;
    event Action<Exception>? Faulted
    {
        add { }
        remove { }
    }

    int PendingRequestCount { get; }
    bool IsFaulted => false;
    Exception? FaultException => null;

    Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool isExpensive = false);

    Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken);

    Task ShutdownAsync(TimeSpan timeout, CancellationToken cancellationToken);
}
