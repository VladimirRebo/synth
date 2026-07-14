using Microsoft.Data.Sqlite;

namespace Synth.Infrastructure;

/// <summary>
/// Resolves the shared SQLite database file and opens connections to it. Per issue #80's design,
/// the repository registry, call-graph store and log store all live in a single file
/// (default <c>~/.synth/synth.db</c>, mirroring <see cref="Configuration.FileConfigStore"/>'s
/// <c>~/.synth/config.json</c> convention — same <c>.synth</c> directory), each owning its own
/// table(s). SQLite creates the file on first connection, so this only has to ensure the directory
/// exists. There is no schema-versioning machinery here: each store issues its own
/// <c>CREATE TABLE IF NOT EXISTS</c> on first use (migration-free, out of scope for #80).
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _connectionString;

    public SqliteConnectionFactory(string? databasePath = null)
    {
        var path = databasePath ?? DefaultPath();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
    }

    /// <summary>
    /// Opens a connection to the shared database file, creating the file if it does not yet exist
    /// (SQLite does this automatically). The caller owns and disposes the connection. Sets a busy
    /// timeout and WAL journal mode, since Synth.Api and Synth.Mcp.Stdio can both hold connections to
    /// this same file concurrently — without these, a writer in one process can make a reader in the
    /// other fail immediately with "database is locked" instead of just waiting briefly.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA journal_mode = WAL;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".synth", "synth.db");
    }
}
