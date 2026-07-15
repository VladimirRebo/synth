using Microsoft.Data.Sqlite;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// SQLite-backed <see cref="IRepositoryPollState"/>, one row per collection in its own <c>poll_state</c>
/// table in the shared <c>~/.synth/synth.db</c> file — a brand new table, not a column added to the
/// existing <c>repositories</c> table, specifically so this feature needs no migration story: an
/// existing database that predates <c>RepositoryPollingService</c> simply gets the table created on
/// first use, exactly like a fresh one (<c>CREATE TABLE IF NOT EXISTS</c> is safe either way).
/// </summary>
public sealed class SqlitePollStateStore : IRepositoryPollState
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    public SqlitePollStateStore(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task<string?> GetLastKnownShaAsync(string collection, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        await using var connection = await OpenAndEnsureSchemaAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastSha FROM poll_state WHERE Collection = $collection;";
        command.Parameters.AddWithValue("$collection", collection);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string;
    }

    public async Task SetLastKnownShaAsync(string collection, string sha, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sha);

        await using var connection = await OpenAndEnsureSchemaAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO poll_state (Collection, LastSha)
            VALUES ($collection, $sha)
            ON CONFLICT(Collection) DO UPDATE SET LastSha = excluded.LastSha;
            """;
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$sha", sha);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Runs CREATE TABLE IF NOT EXISTS once per store instance (this class is a DI singleton, so once
    // per process) rather than on every call — double-checked locking so concurrent first callers
    // don't race to issue the same schema statement. Same pattern as the other SQLite stores.
    private async Task<SqliteConnection> OpenAndEnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var connection = _connectionFactory.OpenConnection();
        try
        {
            if (!_schemaEnsured)
            {
                await _schemaGate.WaitAsync(cancellationToken);
                try
                {
                    if (!_schemaEnsured)
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText =
                            """
                            CREATE TABLE IF NOT EXISTS poll_state (
                                Collection TEXT NOT NULL PRIMARY KEY,
                                LastSha    TEXT NOT NULL
                            );
                            """;
                        await command.ExecuteNonQueryAsync(cancellationToken);
                        _schemaEnsured = true;
                    }
                }
                finally
                {
                    _schemaGate.Release();
                }
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
