using System.Globalization;
using Microsoft.Data.Sqlite;
using Synth.Domain.Logging;

namespace Synth.Infrastructure.Logging;

/// <summary>
/// Durable <see cref="ILogEntryStore"/> backed by the shared <c>~/.synth/synth.db</c> SQLite file
/// (via <see cref="SqliteConnectionFactory"/>) — the final #80 slice replacing the capped-collection
/// <c>MongoLogEntryStore</c>. One row per entry in the <c>logs</c> table; <c>Id INTEGER PRIMARY KEY
/// AUTOINCREMENT</c> is the insertion-order key (SQLite's equivalent of Mongo's <c>$natural</c>). The
/// table is created on first use via <c>CREATE TABLE IF NOT EXISTS</c>, matching the other SQLite
/// stores; there is no migration machinery (out of scope for #80). SQLite is embedded, so unlike the
/// old Mongo store there is no "server unreachable" case to swallow — failures propagate normally.
/// </summary>
/// <remarks>
/// SQLite has no native capped collection, so the document-count bound Mongo gave for free is
/// emulated: once the table grows past <see cref="MaxDocuments"/> rows the oldest are deleted. Running
/// that trim on every insert would add a write to the hot logging path, so it is batched — the trim
/// query runs only once per <see cref="EvictionCheckInterval"/> inserts. Between trims the table can
/// transiently exceed the bound by up to that interval; the byte-size half of Mongo's cap is not
/// replicated (nothing in <see cref="ILogEntryStore"/>'s contract promises callers a byte bound).
/// </remarks>
public sealed class SqliteLogEntryStore : ILogEntryStore
{
    // Document-count bound carried over from MongoLogEntryStore's capped collection.
    private const int MaxDocuments = 20_000;

    // Trim at most once per this many inserts so a hot logging path is not one extra write per line.
    private const int EvictionCheckInterval = 500;

    // Client-facing read size. Far more history stays on disk (up to MaxDocuments), but a snapshot
    // returns only the most recent slice so GET /logs behaves exactly as it did over the ring buffer.
    private const int ReadLimit = InMemoryLogEntryStore.DefaultCapacity;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly int _maxDocuments;
    private readonly int _evictionInterval;

    // Inserts since the last trim; only the counter is shared state, guarded so concurrent drains
    // (there is normally a single background writer) can't both miss the interval boundary.
    private int _insertsSinceEviction;
    private readonly object _evictionGate = new();

    /// <param name="connectionFactory">Shared factory for the ~/.synth/synth.db file.</param>
    /// <param name="maxDocuments">Row cap before the oldest entries are evicted. Defaults to the
    /// production bound; tests pass a small value to exercise eviction without inserting 20k rows.</param>
    /// <param name="evictionInterval">Inserts between trim passes. Defaults to the production batch
    /// size; tests pass 1 to make eviction deterministic per insert.</param>
    public SqliteLogEntryStore(
        SqliteConnectionFactory connectionFactory,
        int maxDocuments = MaxDocuments,
        int evictionInterval = EvictionCheckInterval)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        if (maxDocuments <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxDocuments), maxDocuments, "Must be positive.");
        if (evictionInterval <= 0)
            throw new ArgumentOutOfRangeException(nameof(evictionInterval), evictionInterval, "Must be positive.");

        _maxDocuments = maxDocuments;
        _evictionInterval = evictionInterval;
    }

    public async Task RecordAsync(LogEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var connection = await OpenAndEnsureSchemaAsync(ct);
        await using var insert = connection.CreateCommand();
        insert.CommandText =
            """
            INSERT INTO logs (Timestamp, Level, Message, Exception)
            VALUES ($timestamp, $level, $message, $exception);
            """;
        insert.Parameters.AddWithValue("$timestamp", FormatTimestamp(entry.Timestamp));
        insert.Parameters.AddWithValue("$level", entry.Level);
        insert.Parameters.AddWithValue("$message", entry.Message);
        insert.Parameters.AddWithValue("$exception", (object?)entry.Exception ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(ct);

        if (ShouldEvictNow())
            await TrimToBoundAsync(connection, ct);
    }

    public async Task<IReadOnlyList<LogEntry>> SnapshotAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAndEnsureSchemaAsync(ct);
        await using var command = connection.CreateCommand();
        // Id DESC = newest first; take the most recent ReadLimit, then reverse to the oldest-first
        // order LogsController (and the in-memory store) expose.
        command.CommandText =
            "SELECT Timestamp, Level, Message, Exception FROM logs ORDER BY Id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", ReadLimit);

        var entries = new List<LogEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            entries.Add(new LogEntry(
                ParseTimestamp(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        entries.Reverse();
        return entries;
    }

    // True once per _evictionInterval inserts, so the trim query is amortized instead of per-write.
    private bool ShouldEvictNow()
    {
        lock (_evictionGate)
        {
            if (++_insertsSinceEviction < _evictionInterval)
                return false;
            _insertsSinceEviction = 0;
            return true;
        }
    }

    // Delete everything older than the newest _maxDocuments rows. Ids are AUTOINCREMENT (monotonic,
    // never reused) and we only ever delete from the bottom, so the surviving rows stay a contiguous
    // tail — MAX(Id) - _maxDocuments is exactly the cutoff below which rows are surplus. A no-op while
    // under budget (the cutoff falls below the smallest live Id).
    private async Task TrimToBoundAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM logs WHERE Id <= (SELECT MAX(Id) FROM logs) - $maxDocuments;";
        command.Parameters.AddWithValue("$maxDocuments", _maxDocuments);
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<SqliteConnection> OpenAndEnsureSchemaAsync(CancellationToken ct)
    {
        var connection = _connectionFactory.OpenConnection();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS logs (
                    Id        INTEGER PRIMARY KEY AUTOINCREMENT,
                    Timestamp TEXT NOT NULL,
                    Level     TEXT NOT NULL,
                    Message   TEXT NOT NULL,
                    Exception TEXT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    // Round-trip ("O") format preserves the offset, so a snapshotted entry equals the recorded one.
    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
