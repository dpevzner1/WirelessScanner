using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WirelessScanner.Domain;

namespace WirelessScanner.Application;

/// <summary>
/// Single-consumer, FIFO persistence queue (DR-0003, findings F02/F03).
/// Work items run strictly in enqueue order on one background consumer, so a parent row
/// (session/test) enqueued before its children (samples/cycles) is always written first,
/// and writers never block the UI or the scan loop.
/// </summary>
public sealed class OrderedPersistenceWriter : IAsyncDisposable
{
    private readonly Channel<WorkItem> _channel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly IAppLogger _logger;
    private readonly Task _consumer;
    private long _failures;
    private long _completed;

    private sealed record WorkItem(string Name, Func<Task> Work, TaskCompletionSource? Completion);

    public OrderedPersistenceWriter(IAppLogger logger)
    {
        _logger = logger;
        _consumer = Task.Run(ConsumeAsync);
    }

    /// <summary>Number of work items that threw (each is logged).</summary>
    public long Failures => Interlocked.Read(ref _failures);

    /// <summary>Number of work items that completed successfully.</summary>
    public long Completed => Interlocked.Read(ref _completed);

    /// <summary>Raised on the consumer thread when a work item fails.</summary>
    public event Action<string, Exception>? WorkFailed;

    /// <summary>Queue work. Returns false if the writer has been shut down.</summary>
    public bool Enqueue(string name, Func<Task> work) => _channel.Writer.TryWrite(new WorkItem(name, work, null));

    /// <summary>Completes when every item enqueued before this call has been processed.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_channel.Writer.TryWrite(new WorkItem("flush", () => Task.CompletedTask, tcs)))
        {
            return Task.CompletedTask;
        }
        return tcs.Task.WaitAsync(cancellationToken);
    }

    private async Task ConsumeAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync())
        {
            try
            {
                await item.Work();
                Interlocked.Increment(ref _completed);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failures);
                _logger.Error($"Persistence work '{item.Name}' failed: {ex.Message}", ex);
                WorkFailed?.Invoke(item.Name, ex);
            }
            finally
            {
                item.Completion?.TrySetResult();
            }
        }
    }

    /// <summary>Stops accepting work and drains the queue (bounded wait).</summary>
    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await Task.WhenAny(_consumer, Task.Delay(TimeSpan.FromSeconds(10)));
    }
}
