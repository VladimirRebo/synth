using Microsoft.Extensions.Hosting;
using Synth.Domain.Logging;

namespace Synth.Api.Logging;

/// <summary>
/// Background drain that moves entries from <see cref="LogEntryStoreSink"/>'s channel into the active
/// <see cref="ILogEntryStore"/> (Mongo or in-memory). This is the single, store-agnostic write path
/// that keeps <see cref="LogEntryStoreSink.Emit"/> non-blocking: the request thread only enqueues,
/// this hosted service does the (possibly Mongo I/O-bound) persistence off to the side.
/// </summary>
public sealed class LogEntryStoreWriter : BackgroundService
{
    private readonly LogEntryStoreSink _sink;
    private readonly ILogEntryStore _store;

    public LogEntryStoreWriter(LogEntryStoreSink sink, ILogEntryStore store)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var entry in _sink.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await _store.RecordAsync(entry, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception)
                {
                    // A single failed write must never break the drain loop. The store itself already
                    // swallows Mongo failures; this is belt-and-suspenders for anything unexpected.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown: the channel read was cancelled by stoppingToken.
        }
    }
}
