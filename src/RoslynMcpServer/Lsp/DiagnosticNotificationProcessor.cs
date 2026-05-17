using System.Text.Json;

namespace RoslynMcpServer.Lsp;

public sealed class DiagnosticNotificationProcessor : IAsyncDisposable
{
    public const int DefaultQueueCapacity = 1024;
    public const string DropNewestWhenFullPolicy = "drop_newest_when_full";

    private readonly DiagnosticStore store;
    private readonly int capacity;
    private readonly object queueLock = new();
    private readonly object processingLock = new();
    private readonly Queue<QueuedDiagnosticNotification> queue = new();
    private readonly SemaphoreSlim available = new(0, 1);
    private readonly CancellationTokenSource disposeCts = new();
    private Task? worker;
    private int generation;
    private int pending;
    private long processed;
    private long dropped;
    private long stale;
    private volatile bool disposed;

    public DiagnosticNotificationProcessor(DiagnosticStore store)
        : this(store, DefaultQueueCapacity, startAutomatically: true)
    {
    }

    public DiagnosticNotificationProcessor(DiagnosticStore store, int capacity, bool startAutomatically)
    {
        this.store = store;
        this.capacity = Math.Max(1, capacity);

        if (startAutomatically)
        {
            Start();
        }
    }

    public DiagnosticNotificationQueueStatistics Statistics => new(
        this.capacity,
        Volatile.Read(ref this.pending),
        Interlocked.Read(ref this.processed),
        Interlocked.Read(ref this.dropped),
        Interlocked.Read(ref this.stale),
        DropNewestWhenFullPolicy);

    public int CurrentGeneration => Volatile.Read(ref this.generation);

    public bool IsCurrentGeneration(int generation) => generation == Volatile.Read(ref this.generation);

    public void Start()
    {
        if (this.disposed)
        {
            return;
        }

        lock (this.queueLock)
        {
            if (!this.disposed)
            {
                this.worker ??= Task.Run(ProcessAsync);
            }
        }
    }

    public bool Enqueue(int generation, JsonElement? parameters)
    {
        if (this.disposed)
        {
            return false;
        }

        if (!IsCurrentGeneration(generation))
        {
            Interlocked.Increment(ref this.stale);
            return false;
        }

        var shouldSignal = false;
        lock (this.queueLock)
        {
            if (!IsCurrentGeneration(generation))
            {
                Interlocked.Increment(ref this.stale);
                return false;
            }

            shouldSignal = this.queue.Count == 0;
            if (this.queue.Count >= this.capacity)
            {
                Interlocked.Increment(ref this.dropped);
                return false;
            }

            this.queue.Enqueue(new QueuedDiagnosticNotification(generation, parameters));
            Volatile.Write(ref this.pending, this.queue.Count);
        }

        if (shouldSignal)
        {
            SignalAvailable();
        }

        return true;
    }

    public int ResetForNewWorkspace()
    {
        lock (this.processingLock)
        {
            var nextGeneration = Interlocked.Increment(ref this.generation);
            lock (this.queueLock)
            {
                var discarded = this.queue.Count;
                if (discarded > 0)
                {
                    Interlocked.Add(ref this.stale, discarded);
                }

                this.queue.Clear();
                Volatile.Write(ref this.pending, 0);
            }

            this.store.Clear();
            return nextGeneration;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.disposeCts.Cancel();
        SignalAvailable();

        if (this.worker is not null)
        {
            try
            {
                await this.worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }
        }

        this.disposeCts.Dispose();
        this.available.Dispose();
    }

    private async Task ProcessAsync()
    {
        try
        {
            while (!this.disposeCts.IsCancellationRequested)
            {
                await this.available.WaitAsync(this.disposeCts.Token).ConfigureAwait(false);
                while (TryDequeue(out var item))
                {
                    lock (this.processingLock)
                    {
                        if (item.Generation == Volatile.Read(ref this.generation))
                        {
                            this.store.TryUpdateFromPublishDiagnostics(item.Parameters);
                            Interlocked.Increment(ref this.processed);
                        }
                        else
                        {
                            Interlocked.Increment(ref this.stale);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (this.disposeCts.IsCancellationRequested)
        {
        }
    }

    private bool TryDequeue(out QueuedDiagnosticNotification item)
    {
        lock (this.queueLock)
        {
            if (this.queue.Count == 0)
            {
                item = default;
                Volatile.Write(ref this.pending, 0);
                return false;
            }

            item = this.queue.Dequeue();
            Volatile.Write(ref this.pending, this.queue.Count);
            return true;
        }
    }

    private void SignalAvailable()
    {
        try
        {
            this.available.Release();
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private readonly record struct QueuedDiagnosticNotification(int Generation, JsonElement? Parameters);
}

public sealed record DiagnosticNotificationQueueStatistics(
    int Capacity,
    int Pending,
    long Processed,
    long Dropped,
    long Stale,
    string OverflowPolicy);
