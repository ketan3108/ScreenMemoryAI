using Dapper;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.Core.Data;

public class ScreenshotRepository
{
    private readonly DatabaseService _databaseService;

    public ScreenshotRepository(DatabaseService databaseService)
    {
        _databaseService = databaseService;
    }

    public void InsertIfNotExists(ScreenshotRecord record)
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        const string sql =
        """
        INSERT OR IGNORE INTO screenshots (
            id,
            file_path,
            file_name,
            file_size_bytes,
            created_at,
            modified_at,
            thumbnail_path,
            imported_at
        )
        VALUES (
            @Id,
            @FilePath,
            @FileName,
            @FileSizeBytes,
            @CreatedAt,
            @ModifiedAt,
            @ThumbnailPath,
            @ImportedAt
        );
        """;

        connection.Execute(sql, record);
    }

    public bool ExistsByFilePath(string path)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string sql = "SELECT COUNT(1) FROM screenshots WHERE file_path = @Path;";
        return connection.ExecuteScalar<int>(sql, new { Path = path }) > 0;
    }

    public int Count()
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots");
    }

    public List<ScreenshotRecord> GetRecent(int limit = 50)
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        const string sql =
        """
        SELECT
            id AS Id,
            file_path AS FilePath,
            file_name AS FileName,
            file_size_bytes AS FileSizeBytes,
            created_at AS CreatedAt,
            modified_at AS ModifiedAt,
            thumbnail_path AS ThumbnailPath,
            ocr_text AS OcrText,
            ocr_status AS OcrStatus,
            imported_at AS ImportedAt
        FROM screenshots
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit }).ToList();
    }

    public void InsertManyIfNotExists(IEnumerable<ScreenshotRecord> records)
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        using var transaction = connection.BeginTransaction();

        const string sql =
        """
        INSERT OR IGNORE INTO screenshots (
            id,
            file_path,
            file_name,
            file_size_bytes,
            created_at,
            modified_at,
            thumbnail_path,
            imported_at
        )
        VALUES (
            @Id,
            @FilePath,
            @FileName,
            @FileSizeBytes,
            @CreatedAt,
            @ModifiedAt,
            @ThumbnailPath,
            @ImportedAt
        );
        """;

        connection.Execute(sql, records, transaction);

        transaction.Commit();
    }

    public List<ScreenshotRecord> SearchByFileName(string query, int limit = 100)
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        const string sql =
        """
        SELECT
            id AS Id,
            file_path AS FilePath,
            file_name AS FileName,
            file_size_bytes AS FileSizeBytes,
            created_at AS CreatedAt,
            modified_at AS ModifiedAt,
            thumbnail_path AS ThumbnailPath,
            ocr_text AS OcrText,
            ocr_status AS OcrStatus,
            imported_at AS ImportedAt
        FROM screenshots
        WHERE file_name LIKE @Query
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(
            sql,
            new
            {
                Query = $"%{query}%",
                Limit = limit
            }).ToList();
    }

    public void UpdateOcrText(string id, string ocrText, string status = "completed")
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        const string sql =
        """
        UPDATE screenshots
        SET ocr_text = @OcrText,
            ocr_status = @Status
        WHERE id = @Id;
        """;

        connection.Execute(sql, new
        {
            Id = id,
            OcrText = ocrText,
            Status = status
        });
    }

    public void UpdateThumbnailPath(string id, string thumbnailPath)
    {
        using var connection = _databaseService.CreateConnection();

        connection.Open();

        const string sql =
        """
        UPDATE screenshots
        SET thumbnail_path = @ThumbnailPath
        WHERE id = @Id;
        """;

        connection.Execute(sql, new
        {
            Id = id,
            ThumbnailPath = thumbnailPath
        });
    }

    public List<ScreenshotRecord> SearchByKeywords(IEnumerable<string> keywords, int limit = 100)
    {
        var normalized = keywords
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            return [];
        }

        using var connection = _databaseService.CreateConnection();
        connection.Open();

        var dynamicParameters = new DynamicParameters();
        var conditions = new List<string>();

        for (var i = 0; i < normalized.Count; i++)
        {
            var key = $"Keyword{i}";
            conditions.Add($"file_name LIKE @{key}");
            dynamicParameters.Add(key, $"%{normalized[i]}%");
        }

        dynamicParameters.Add("Limit", limit);

        var whereClause = string.Join(" OR ", conditions);

        var sql =
        $"""
        SELECT
            id AS Id,
            file_path AS FilePath,
            file_name AS FileName,
            file_size_bytes AS FileSizeBytes,
            created_at AS CreatedAt,
            modified_at AS ModifiedAt,
            thumbnail_path AS ThumbnailPath,
            ocr_text AS OcrText,
            ocr_status AS OcrStatus,
            imported_at AS ImportedAt
        FROM screenshots
        WHERE {whereClause}
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, dynamicParameters).ToList();
    }

    public int CountOcrReady()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots WHERE ocr_status = 'completed';");
    }

    public int CountAiTagged()
    {
        return 0;
    }

    public ScreenshotRecord? GetByFilePath(string path)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string sql =
        """
        SELECT
            id AS Id,
            file_path AS FilePath,
            file_name AS FileName,
            file_size_bytes AS FileSizeBytes,
            created_at AS CreatedAt,
            modified_at AS ModifiedAt,
            thumbnail_path AS ThumbnailPath,
            ocr_text AS OcrText,
            ocr_status AS OcrStatus,
            imported_at AS ImportedAt
        FROM screenshots
        WHERE file_path = @Path
        LIMIT 1;
        """;

        return connection.QueryFirstOrDefault<ScreenshotRecord>(sql, new { Path = path });
    }

}
