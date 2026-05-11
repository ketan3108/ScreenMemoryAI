using Microsoft.Data.Sqlite;

namespace ScreenMemory.AI.Core.Data;

public class DatabaseService
{
    private readonly string _databasePath;

    public DatabaseService()
    {
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
    }

    private static void EnsureColumnExists(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string columnDefinition)
    {
        using var check = connection.CreateCommand();
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
        alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
        alter.ExecuteNonQuery();
    }
}
