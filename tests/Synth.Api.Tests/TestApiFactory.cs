using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Synth.Infrastructure;

namespace Synth.Api.Tests;

// Every Synth.Api.Tests fixture should use this instead of a bare WebApplicationFactory<Program>.
// Program.cs's AddSynthVcs/AddSynthCodeGraph/AddSynthLogging all resolve a shared
// SqliteConnectionFactory that defaults to the real ~/.synth/synth.db when no path is given — a bare
// factory makes every HTTP-level test read and write the developer's actual repository registry,
// call graph and log history (this is how ~60 stale test-fixture collections ended up in the real
// database). This subclass points that connection at a private temp file instead, deleted on dispose.
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    // A bare temp file under the shared system temp dir, not an owned subdirectory: WithWebHostBuilder
    // clones (used throughout these tests, e.g. `_factory.WithWebHostBuilder(...).CreateClient()`) wrap
    // and dispose the original factory too, so Dispose(bool) below can run more than once per instance.
    // Deleting a directory a second time throws; deleting an already-gone file guarded by File.Exists
    // does not, so this stays correct regardless of how many times disposal fires.
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"synth-api-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<SqliteConnectionFactory>();
            services.AddSingleton(new SqliteConnectionFactory(_databasePath));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;

        // SQLite's WAL mode (enabled by SqliteConnectionFactory) leaves -shm/-wal siblings alongside
        // the main file; clean up all three.
        foreach (var path in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
