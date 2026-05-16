using Microsoft.Data.Sqlite;

namespace ScreenMemory.AI.Core.Data;

public class DatabaseService
{
    private readonly string _databasePath;
    private const int CurrentSchemaVersion = 4;

    public DatabaseService(string? databasePath = null)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            var customFolder = Path.GetDirectoryName(databasePath);
            if (!string.IsNullOrWhiteSpace(customFolder))
            {
                Directory.CreateDirectory(customFolder);
            }

            _databasePath = databasePath;
            return;
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var folder = Path.Combine(appData, "ScreenMemory AI");

        Directory.CreateDirectory(folder);

        _databasePath = Path.Combine(folder, "screenmemory.db");
    }

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection($"Data Source={_databasePath}");
    }

    public void Initialize()
    {
        using var connection = CreateConnection();

        connection.Open();

        const string pragmaSql =
        """
        PRAGMA journal_mode = WAL;
        PRAGMA synchronous = NORMAL;
        PRAGMA cache_size = -20000;
        PRAGMA temp_store = MEMORY;
        """;

        using (var pragmaCommand = connection.CreateCommand())
        {
            pragmaCommand.CommandText = pragmaSql;
            pragmaCommand.ExecuteNonQuery();
        }

        var command = connection.CreateCommand();

        command.CommandText =
        """
        CREATE TABLE IF NOT EXISTS screenshots (
        id TEXT PRIMARY KEY,
        file_path TEXT NOT NULL UNIQUE,
        file_name TEXT NOT NULL,
        file_size_bytes INTEGER,
        created_at TEXT,
        modified_at TEXT,
        thumbnail_path TEXT,
        ocr_text TEXT,
        ocr_status TEXT DEFAULT 'pending',
        imported_at TEXT
        );
        """;

        command.ExecuteNonQuery();

        EnsureColumnExists(connection, "screenshots", "ocr_text", "TEXT");
        EnsureColumnExists(connection, "screenshots", "ocr_status", "TEXT DEFAULT 'pending'");
        EnsureColumnExists(connection, "screenshots", "is_favorite", "INTEGER NOT NULL DEFAULT 0");

        using var ftsCommand = connection.CreateCommand();
        ftsCommand.CommandText =
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS screenshot_fts USING fts5(
            screenshot_id UNINDEXED,
            file_name,
            ocr_text
        );
        """;
        ftsCommand.ExecuteNonQuery();

        RunMigrations(connection);
    }

    private static void RunMigrations(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                id INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            """;
            command.ExecuteNonQuery();
        }

        var lastApplied = GetLastAppliedMigration(connection);
        for (var version = lastApplied + 1; version <= CurrentSchemaVersion; version++)
        {
            using var transaction = connection.BeginTransaction();
            try
            {
                ApplyMigration(connection, transaction, version);

                using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations (id) VALUES (@Id);";
                record.Parameters.AddWithValue("@Id", version);
                record.ExecuteNonQuery();

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private static int GetLastAppliedMigration(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id), 0) FROM schema_migrations;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void ApplyMigration(SqliteConnection connection, SqliteTransaction transaction, int version)
    {
        switch (version)
        {
            case 1:
                AddAiMetadataColumns(connection, transaction);
                break;
            case 2:
                RebuildFtsForAiMetadata(connection, transaction);
                break;
            case 3:
                CreatePerformanceIndexes(connection, transaction);
                break;
            case 4:
                AddFavoriteColumn(connection, transaction);
                break;
            default:
                throw new InvalidOperationException($"Unknown database migration version {version}.");
        }
    }

    private static void AddAiMetadataColumns(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureColumnExists(connection, transaction, "screenshots", "active_window", "TEXT DEFAULT ''");
        EnsureColumnExists(connection, transaction, "screenshots", "process_name", "TEXT DEFAULT ''");
        EnsureColumnExists(connection, transaction, "screenshots", "application_name", "TEXT DEFAULT ''");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_category", "TEXT DEFAULT 'unknown'");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_tags", "TEXT DEFAULT ''");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_summary", "TEXT DEFAULT ''");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_confidence", "REAL DEFAULT 0.0");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_status", "TEXT DEFAULT 'pending'");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_error", "TEXT DEFAULT NULL");
        EnsureColumnExists(connection, transaction, "screenshots", "ai_analyzed_at", "TEXT DEFAULT NULL");
        EnsureColumnExists(connection, transaction, "screenshots", "embedding_vector", "BLOB DEFAULT NULL");
        EnsureColumnExists(connection, transaction, "screenshots", "ocr_processed_at", "TEXT DEFAULT NULL");
        EnsureColumnExists(connection, transaction, "screenshots", "updated_at", "TEXT DEFAULT NULL");
    }

    private static void RebuildFtsForAiMetadata(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(
            connection,
            transaction,
            """
            DROP TABLE IF EXISTS screenshot_fts;

            CREATE VIRTUAL TABLE screenshot_fts USING fts5(
                screenshot_id UNINDEXED,
                file_name,
                ocr_text,
                active_window,
                application_name,
                process_name,
                ai_category,
                ai_tags,
                ai_summary
            );

            INSERT INTO screenshot_fts (
                screenshot_id,
                file_name,
                ocr_text,
                active_window,
                application_name,
                process_name,
                ai_category,
                ai_tags,
                ai_summary
            )
            SELECT
                id,
                file_name,
                IFNULL(ocr_text, ''),
                IFNULL(active_window, ''),
                IFNULL(application_name, ''),
                IFNULL(process_name, ''),
                IFNULL(ai_category, 'unknown'),
                IFNULL(ai_tags, ''),
                IFNULL(ai_summary, '')
            FROM screenshots;
            """);
    }

    private static void CreatePerformanceIndexes(SqliteConnection connection, SqliteTransaction transaction)
    {
        ExecuteNonQuery(
            connection,
            transaction,
            """
            CREATE INDEX IF NOT EXISTS idx_screenshots_ocr_status ON screenshots(ocr_status);
            CREATE INDEX IF NOT EXISTS idx_screenshots_ai_status ON screenshots(ai_status);
            CREATE INDEX IF NOT EXISTS idx_screenshots_ai_category ON screenshots(ai_category);
            CREATE INDEX IF NOT EXISTS idx_screenshots_application_name ON screenshots(application_name);
            CREATE INDEX IF NOT EXISTS idx_screenshots_imported_at ON screenshots(imported_at);
            CREATE INDEX IF NOT EXISTS idx_screenshots_created_at ON screenshots(created_at);
            CREATE INDEX IF NOT EXISTS idx_screenshots_updated_at ON screenshots(updated_at);
            """);
    }

    private static void AddFavoriteColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        EnsureColumnExists(connection, transaction, "screenshots", "is_favorite", "INTEGER NOT NULL DEFAULT 0");

        ExecuteNonQuery(
            connection,
            transaction,
            """
            CREATE INDEX IF NOT EXISTS idx_screenshots_is_favorite ON screenshots(is_favorite);
            """);
    }

    private static void EnsureColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
        => EnsureColumnExists(connection, null, tableName, columnName, columnDefinition);

    private static void EnsureColumnExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = $"PRAGMA table_info({tableName});";

        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            var name = reader["name"]?.ToString();
            if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.Transaction = transaction;
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
