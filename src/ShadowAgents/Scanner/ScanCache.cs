using System.Text.Json.Serialization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ShadowAgents;

/// <summary>
/// SQLite cache of per-file detection results, keyed on path, size and modified time (Section 9:
/// re-scanning an unchanged root reads ~0 files). Results are written as each file is scanned, so a
/// scan that is killed partway resumes from here on the next run.
/// </summary>
public sealed class ScanCache : IDisposable
{
    /// <summary>
    /// Bump whenever detection rules or the cached result shape change: every cached result is then
    /// discarded and files are re-read once.
    /// </summary>
    public const string DetectionRulesVersion = "2026-11-05.1";

    private const int WritesPerTransaction = 200;

    private static readonly JsonSerializerOptions ResultJsonOptions = new JsonSerializerOptions()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SqliteConnection connection;
    private SqliteTransaction transaction;
    private int writesInTransaction;

    private ScanCache(SqliteConnection connection)
    {
        this.connection = connection;
        CreateSchema();
        DiscardResultsFromOlderRules();
        transaction = this.connection.BeginTransaction();
    }

    /// <summary>Opens (or creates) the cache file. With a null path the cache lives in memory for this run only.</summary>
    public static ScanCache Open(string cacheFilePath)
    {
        string connectionString;
        if (cacheFilePath == null)
        {
            connectionString = "Data Source=:memory:";
        }
        else
        {
            string cacheFolder = Path.GetDirectoryName(Path.GetFullPath(cacheFilePath));
            if (cacheFolder != null)
            {
                Directory.CreateDirectory(cacheFolder);
            }

            connectionString = new SqliteConnectionStringBuilder { DataSource = cacheFilePath, Pooling = false }.ToString();
        }

        SqliteConnection connection = new SqliteConnection(connectionString);
        connection.Open();
        return new ScanCache(connection);
    }

    public bool TryGetFileResult(string filePath, long fileSize, long modifiedTicks, out FileDetectionResult result)
    {
        result = FileDetectionResult.Empty();
        using SqliteCommand command = CreateCommand("SELECT result FROM files WHERE path = $path AND size = $size AND mtime = $mtime");
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$mtime", modifiedTicks);

        string resultJson = command.ExecuteScalar() as string;
        if (resultJson == null)
        {
            return false;
        }

        FileDetectionResult cachedResult = JsonSerializer.Deserialize<FileDetectionResult>(resultJson, ResultJsonOptions);
        if (cachedResult == null)
        {
            return false;
        }

        result = cachedResult;
        return true;
    }

    public void StoreFileResult(string filePath, long fileSize, long modifiedTicks, FileDetectionResult result)
    {
        using SqliteCommand command = CreateCommand(
            """
            INSERT INTO files (path, size, mtime, result, scanned_utc) VALUES ($path, $size, $mtime, $result, $scanned)
            ON CONFLICT(path) DO UPDATE SET size = $size, mtime = $mtime, result = $result, scanned_utc = $scanned
            """);
        command.Parameters.AddWithValue("$path", filePath);
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$mtime", modifiedTicks);
        command.Parameters.AddWithValue("$result", JsonSerializer.Serialize(result, ResultJsonOptions));
        command.Parameters.AddWithValue("$scanned", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        CommitEveryFewWrites();
    }

    /// <summary>Returns when a finding was first seen, recording <paramref name="now"/> if it is new.</summary>
    public DateTimeOffset GetOrRecordFirstSeen(string findingId, DateTimeOffset now)
    {
        using SqliteCommand selectCommand = CreateCommand("SELECT first_seen FROM findings WHERE id = $id");
        selectCommand.Parameters.AddWithValue("$id", findingId);
        string firstSeenText = selectCommand.ExecuteScalar() as string;
        if (firstSeenText != null && DateTimeOffset.TryParse(firstSeenText, out DateTimeOffset firstSeen))
        {
            return firstSeen;
        }

        using SqliteCommand insertCommand = CreateCommand("INSERT INTO findings (id, first_seen) VALUES ($id, $firstSeen)");
        insertCommand.Parameters.AddWithValue("$id", findingId);
        insertCommand.Parameters.AddWithValue("$firstSeen", now.ToString("O"));
        insertCommand.ExecuteNonQuery();
        CommitEveryFewWrites();
        return now;
    }

    public void Dispose()
    {
        transaction.Commit();
        transaction.Dispose();
        this.connection.Dispose();
    }

    private void CreateSchema()
    {
        using SqliteCommand command = this.connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS files (
                path TEXT PRIMARY KEY,
                size INTEGER NOT NULL,
                mtime INTEGER NOT NULL,
                result TEXT NOT NULL,
                scanned_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS findings (id TEXT PRIMARY KEY, first_seen TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    private void DiscardResultsFromOlderRules()
    {
        using SqliteCommand selectCommand = this.connection.CreateCommand();
        selectCommand.CommandText = "SELECT value FROM meta WHERE key = 'rules_version'";
        if (selectCommand.ExecuteScalar() as string == DetectionRulesVersion)
        {
            return;
        }

        using SqliteCommand resetCommand = this.connection.CreateCommand();
        resetCommand.CommandText =
            """
            DELETE FROM files;
            INSERT INTO meta (key, value) VALUES ('rules_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = $version;
            """;
        resetCommand.Parameters.AddWithValue("$version", DetectionRulesVersion);
        resetCommand.ExecuteNonQuery();
    }

    /// <summary>One transaction per batch of writes: far faster than one per write, and a crash loses little.</summary>
    private void CommitEveryFewWrites()
    {
        writesInTransaction++;
        if (writesInTransaction < WritesPerTransaction)
        {
            return;
        }

        transaction.Commit();
        transaction.Dispose();
        transaction = this.connection.BeginTransaction();
        writesInTransaction = 0;
    }

    private SqliteCommand CreateCommand(string commandText)
    {
        SqliteCommand command = this.connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = transaction;
        return command;
    }
}
