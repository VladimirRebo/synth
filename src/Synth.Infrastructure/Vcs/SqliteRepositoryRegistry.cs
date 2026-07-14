using System.Globalization;
using Microsoft.Data.Sqlite;
using Synth.Domain.Vcs;

namespace Synth.Infrastructure.Vcs;

/// <summary>
/// SQLite-backed <see cref="IRepositoryRegistry"/> storing one relational row per collection in the
/// <c>repositories</c> table (unlike the old Mongo version, which stored a JSON blob to work around
/// Mongo's dotted-field-name restriction — SQLite has real columns and no such restriction). The
/// table is created on first use via <c>CREATE TABLE IF NOT EXISTS</c>; there is no migration
/// machinery (out of scope for issue #80). SQLite is embedded, so unlike Mongo there is no
/// "server unreachable" case to swallow — failures propagate normally.
/// </summary>
public sealed class SqliteRepositoryRegistry : IRepositoryRegistry
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SemaphoreSlim _schemaGate = new(1, 1);
    private volatile bool _schemaEnsured;

    public SqliteRepositoryRegistry(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task UpsertAsync(RepositoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAndEnsureSchemaAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO repositories (Collection, SourceType, Source, Branch, LastIndexedAt, ChunkCount)
            VALUES ($collection, $sourceType, $source, $branch, $lastIndexedAt, $chunkCount)
            ON CONFLICT(Collection) DO UPDATE SET
                SourceType = excluded.SourceType,
                Source = excluded.Source,
                Branch = excluded.Branch,
                LastIndexedAt = excluded.LastIndexedAt,
                ChunkCount = excluded.ChunkCount;
            """;
        command.Parameters.AddWithValue("$collection", entry.Collection);
        command.Parameters.AddWithValue("$sourceType", entry.SourceType);
        command.Parameters.AddWithValue("$source", entry.Source);
        command.Parameters.AddWithValue("$branch", (object?)entry.Branch ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastIndexedAt", FormatTimestamp(entry.LastIndexedAt));
        command.Parameters.AddWithValue("$chunkCount", entry.ChunkCount);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(string collection, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);

        await using var connection = await OpenAndEnsureSchemaAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM repositories WHERE Collection = $collection;";
        command.Parameters.AddWithValue("$collection", collection);

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<IReadOnlyList<RepositoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAndEnsureSchemaAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT Collection, SourceType, Source, Branch, LastIndexedAt, ChunkCount FROM repositories;";

        var entries = new List<RepositoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new RepositoryEntry
            {
                Collection = reader.GetString(0),
                SourceType = reader.GetString(1),
                Source = reader.GetString(2),
                Branch = reader.IsDBNull(3) ? null : reader.GetString(3),
                LastIndexedAt = ParseTimestamp(reader.GetString(4)),
                ChunkCount = reader.GetInt32(5),
            });
        }

        return entries;
    }

    // Runs CREATE TABLE IF NOT EXISTS once per store instance (this class is a DI singleton, so once
    // per process) rather than on every call — double-checked locking so concurrent first callers
    // don't race to issue the same schema statement.
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
                            CREATE TABLE IF NOT EXISTS repositories (
                                Collection    TEXT NOT NULL PRIMARY KEY,
                                SourceType    TEXT NOT NULL,
                                Source        TEXT NOT NULL,
                                Branch        TEXT NULL,
                                LastIndexedAt TEXT NOT NULL,
                                ChunkCount    INTEGER NOT NULL
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

    // Round-trip ("O") preserves DateTimeKind (Utc), so a listed entry equals the upserted one.
    private static string FormatTimestamp(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTimestamp(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
