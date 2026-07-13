using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Synth.Domain.Graph;

namespace Synth.Infrastructure.Graph;

/// <summary>
/// DI wiring for the call-graph storage layer. Per issue #80 (slice SYNTH-63) the store always uses
/// the local <c>~/.synth/synth.db</c> SQLite file — no Mongo-vs-fallback branching, mirroring how
/// <c>VcsServiceExtensions</c> (SYNTH-62) and <c>ConfigStoreExtensions</c> (SYNTH-53) unconditionally
/// return their SQLite/file-backed implementations. The shared <see cref="SqliteConnectionFactory"/>
/// is registered idempotently (<c>TryAddSingleton</c>) so this composes with
/// <c>AddSynthVcs</c> — whichever runs first wins, and both resolve the same single db file.
/// <see cref="InMemoryCodeGraphStore"/> stays available for unit tests but is no longer wired into
/// production DI here.
/// </summary>
public static class CodeGraphServiceExtensions
{
    public static IHostApplicationBuilder AddSynthCodeGraph(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddSingleton<SqliteConnectionFactory>();
        builder.Services.AddSingleton<ICodeGraphStore>(sp =>
            new SqliteCodeGraphStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        return builder;
    }
}
