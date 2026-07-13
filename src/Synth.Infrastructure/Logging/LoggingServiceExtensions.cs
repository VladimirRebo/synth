using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synth.Domain.Logging;

namespace Synth.Infrastructure.Logging;

/// <summary>
/// DI wiring for the log-persistence layer: registers the <see cref="LogEntryStoreSink"/> (already
/// constructed and attached to Serilog in <c>Program.cs</c>), wires the <see cref="ILogEntryStore"/>
/// to its SQLite-backed implementation, and starts the <see cref="LogEntryStoreWriter"/> background
/// drain that moves entries from the sink's channel into that store. Per issue #80 (final slice,
/// SYNTH-64) the store always uses the local <c>~/.synth/synth.db</c> file — no Mongo-vs-fallback
/// branching, mirroring <c>VcsServiceExtensions</c> (SYNTH-62) and <c>CodeGraphServiceExtensions</c>
/// (SYNTH-63). The shared <see cref="SqliteConnectionFactory"/> is registered idempotently
/// (<c>TryAddSingleton</c>) so this composes with the other stores' wiring onto one db file.
/// <see cref="InMemoryLogEntryStore"/> stays available for unit tests but is no longer wired here.
/// </summary>
public static class LoggingServiceExtensions
{
    /// <param name="sink">The sink already wired into Serilog in Program.cs; shared here as the DI
    /// singleton the background writer drains.</param>
    public static IHostApplicationBuilder AddSynthLogging(this IHostApplicationBuilder builder, LogEntryStoreSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);

        builder.Services.AddSingleton(sink);
        builder.Services.TryAddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<ILogEntryStore>(sp =>
            new SqliteLogEntryStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        builder.Services.AddHostedService<LogEntryStoreWriter>();

        return builder;
    }
}
