using Microsoft.Data.Sqlite;
using Synth.Domain.Graph;

namespace Synth.Infrastructure.Graph;

/// <summary>
/// SQLite-backed <see cref="ICodeGraphStore"/> storing one relational row per <see cref="CallEdge"/>
/// in the <c>call_edges</c> table (unlike the registry's one-row-per-collection shape — the call
/// graph must filter on <see cref="CallEdge.Caller"/> and <see cref="CallEdge.Callee"/> in
/// <em>both</em> directions, so each edge is its own indexed row). Two compound indexes back the two
/// query directions, matching the old <c>MongoCodeGraphStore</c>'s two compound indexes exactly. The
/// table and indexes are created on first use via <c>CREATE TABLE/INDEX IF NOT EXISTS</c>; there is
/// no migration machinery (out of scope for issue #80). SQLite is embedded, so unlike Mongo there is
/// no "server unreachable" case to swallow — failures propagate normally.
/// </summary>
public sealed class SqliteCodeGraphStore : ICodeGraphStore
{
    private readonly SqliteConnectionFactory _connectionFactory;

    public SqliteCodeGraphStore(SqliteConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task ReplaceEdgesAsync(string collection, IReadOnlyList<CallEdge> edges, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(edges);

        await using var connection = await OpenAndEnsureSchemaAsync(ct);
        // Delete-then-insert within one transaction: a full replace so a re-index never leaves stale
        // edges behind, and never a partially-applied swap. Same semantics as the Mongo version.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM call_edges WHERE Collection = $collection;";
            delete.Parameters.AddWithValue("$collection", collection);
            await delete.ExecuteNonQueryAsync(ct);
        }

        if (edges.Count > 0)
        {
            // Reuse a single prepared command across the bulk insert, rebinding parameters per edge.
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO call_edges (Collection, Caller, Callee, SourceFile, Line)
                VALUES ($collection, $caller, $callee, $sourceFile, $line);
                """;
            var collectionParam = insert.Parameters.Add("$collection", SqliteType.Text);
            var callerParam = insert.Parameters.Add("$caller", SqliteType.Text);
            var calleeParam = insert.Parameters.Add("$callee", SqliteType.Text);
            var sourceFileParam = insert.Parameters.Add("$sourceFile", SqliteType.Text);
            var lineParam = insert.Parameters.Add("$line", SqliteType.Integer);

            foreach (var edge in edges)
            {
                collectionParam.Value = edge.Collection;
                callerParam.Value = edge.Caller;
                calleeParam.Value = edge.Callee;
                sourceFileParam.Value = edge.SourceFile;
                lineParam.Value = edge.Line;
                await insert.ExecuteNonQueryAsync(ct);
            }
        }

        await transaction.CommitAsync(ct);
    }

    public Task<IReadOnlyList<CallEdge>> FindCallersAsync(string collection, string symbol, CancellationToken ct = default) =>
        // Callers of the symbol: edges whose Callee equals it, within the collection.
        FindAsync(collection, "Callee", symbol, ct);

    public Task<IReadOnlyList<CallEdge>> FindCalleesAsync(string collection, string symbol, CancellationToken ct = default) =>
        // What the symbol calls: edges whose Caller equals it, within the collection.
        FindAsync(collection, "Caller", symbol, ct);

    private async Task<IReadOnlyList<CallEdge>> FindAsync(string collection, string symbolColumn, string symbol, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(symbol);

        await using var connection = await OpenAndEnsureSchemaAsync(ct);
        await using var command = connection.CreateCommand();
        // symbolColumn is a fixed literal ("Caller"/"Callee") chosen internally — never user input —
        // so interpolating it is safe; the filter values stay parameterized.
        command.CommandText =
            $"SELECT Collection, Caller, Callee, SourceFile, Line FROM call_edges "
            + $"WHERE Collection = $collection AND {symbolColumn} = $symbol;";
        command.Parameters.AddWithValue("$collection", collection);
        command.Parameters.AddWithValue("$symbol", symbol);

        var edges = new List<CallEdge>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            edges.Add(new CallEdge(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4)));
        }

        return edges;
    }

    private async Task<SqliteConnection> OpenAndEnsureSchemaAsync(CancellationToken ct)
    {
        var connection = _connectionFactory.OpenConnection();
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS call_edges (
                    Id         INTEGER PRIMARY KEY AUTOINCREMENT,
                    Collection TEXT NOT NULL,
                    Caller     TEXT NOT NULL,
                    Callee     TEXT NOT NULL,
                    SourceFile TEXT NOT NULL,
                    Line       INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_call_edges_caller ON call_edges(Collection, Callee);
                CREATE INDEX IF NOT EXISTS idx_call_edges_callee ON call_edges(Collection, Caller);
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
}
