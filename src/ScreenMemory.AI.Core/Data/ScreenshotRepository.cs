using Dapper;
using ScreenMemory.AI.Core.Models;
using System.Text.RegularExpressions;

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

        var saved = GetByFilePath(record.FilePath);
        if (saved is not null)
        {
            UpsertFts(saved.Id, saved.FileName, saved.OcrText);
        }
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
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit })
            .OrderByDescending(GetBestTimestamp)
            .Take(limit)
            .ToList();
    }

    public List<ScreenshotRecord> GetRecentPaged(int skip, int take)
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
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Take OFFSET @Skip;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Skip = skip, Take = take })
            .OrderByDescending(GetBestTimestamp)
            .ToList();
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

        RebuildSearchIndex();
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

        const string ftsLookupSql =
        """
        SELECT file_name AS FileName
        FROM screenshots
        WHERE id = @Id
        LIMIT 1;
        """;

        var fileName = connection.QueryFirstOrDefault<string>(ftsLookupSql, new { Id = id });
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            UpsertFts(id, fileName, ocrText);
        }
    }

    public void UpdateOcrBatch(IEnumerable<(string Id, string OcrText, string Status)> updates)
    {
        var list = updates.ToList();
        if (list.Count == 0)
        {
            return;
        }

        using var connection = _databaseService.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        const string updateSql =
        """
        UPDATE screenshots
        SET ocr_text = @OcrText,
            ocr_status = @Status
        WHERE id = @Id;
        """;

        connection.Execute(updateSql, list.Select(x => new
        {
            x.Id,
            x.OcrText,
            x.Status
        }), transaction);

        const string lookupSql =
        """
        SELECT id AS Id, file_name AS FileName
        FROM screenshots
        WHERE id IN @Ids;
        """;

        var idSet = list.Select(x => x.Id).Distinct().ToList();
        var fileNames = connection.Query<(string Id, string FileName)>(lookupSql, new { Ids = idSet }, transaction)
            .ToDictionary(x => x.Id, x => x.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (fileNames.TryGetValue(item.Id, out var fileName))
            {
                const string deleteSql = "DELETE FROM screenshot_fts WHERE screenshot_id = @ScreenshotId;";
                const string insertSql =
                """
                INSERT INTO screenshot_fts (screenshot_id, file_name, ocr_text)
                VALUES (@ScreenshotId, @FileName, @OcrText);
                """;

                connection.Execute(deleteSql, new { ScreenshotId = item.Id }, transaction);
                connection.Execute(insertSql, new
                {
                    ScreenshotId = item.Id,
                    FileName = fileName ?? string.Empty,
                    OcrText = item.OcrText ?? string.Empty
                }, transaction);
            }
        }

        transaction.Commit();
    }

    public List<ScreenshotRecord> SearchByText(string query, int limit = 100)
    {
        return SearchFullText(query, limit);
    }

    public List<ScreenshotRecord> SearchFullText(string query, int limit = 100)
    {
        var ftsQuery = BuildFtsQuery(query);
        if (string.IsNullOrWhiteSpace(ftsQuery))
        {
            return GetRecent(limit);
        }

        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string ftsSql =
        """
        SELECT
            s.id AS Id,
            s.file_path AS FilePath,
            s.file_name AS FileName,
            s.file_size_bytes AS FileSizeBytes,
            s.created_at AS CreatedAt,
            s.modified_at AS ModifiedAt,
            s.thumbnail_path AS ThumbnailPath,
            s.ocr_text AS OcrText,
            s.ocr_status AS OcrStatus,
            s.imported_at AS ImportedAt
        FROM screenshot_fts f
        JOIN screenshots s ON s.id = f.screenshot_id
        WHERE screenshot_fts MATCH @Query
        ORDER BY bm25(screenshot_fts), s.imported_at DESC
        LIMIT @Limit;
        """;

        try
        {
            return connection.Query<ScreenshotRecord>(ftsSql, new
            {
                Query = ftsQuery,
                Limit = limit
            }).ToList();
        }
        catch
        {
            return SearchByTextLike(query, limit);
        }
    }

    public List<ScreenshotRecord> SearchHybrid(string query, int limit = 100)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return GetRecent(limit);
        }

        var terms = trimmed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (trimmed.Length < 3)
        {
            return SearchByTextLike(trimmed, limit);
        }

        if (terms.Length <= 1)
        {
            return SearchByTextLike(trimmed, limit);
        }

        var likeResults = SearchByTextLike(trimmed, limit);
        var ftsResults = SearchFullText(trimmed, limit);

        var merged = new List<ScreenshotRecord>(limit);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void append(IEnumerable<ScreenshotRecord> source)
        {
            foreach (var item in source)
            {
                if (merged.Count >= limit)
                {
                    break;
                }

                if (seen.Add(item.Id))
                {
                    merged.Add(item);
                }
            }
        }

        // Priority order for UX:
        // 1) exact filename
        // 2) partial filename / OCR from LIKE
        // 3) ranked FTS matches (bm25)
        append(likeResults.Where(r => string.Equals(r.FileName, trimmed, StringComparison.OrdinalIgnoreCase)));
        append(likeResults.Where(r => !string.Equals(r.FileName, trimmed, StringComparison.OrdinalIgnoreCase)));
        append(ftsResults);

        return merged;
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

    public void UpdateThumbnailPathsBatch(IEnumerable<(string Id, string ThumbnailPath)> updates)
    {
        var list = updates.ToList();
        if (list.Count == 0)
        {
            return;
        }

        using var connection = _databaseService.CreateConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        const string sql =
        """
        UPDATE screenshots
        SET thumbnail_path = @ThumbnailPath
        WHERE id = @Id;
        """;

        connection.Execute(sql, list.Select(x => new
        {
            x.Id,
            x.ThumbnailPath
        }), transaction);

        transaction.Commit();
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

    public List<ScreenshotRecord> GetPendingOcr(int limit = 100)
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
        WHERE ocr_status IS NULL
           OR ocr_status = ''
           OR ocr_status = 'pending'
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit }).ToList();
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

    public void UpsertFts(string screenshotId, string fileName, string ocrText)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string deleteSql = "DELETE FROM screenshot_fts WHERE screenshot_id = @ScreenshotId;";
        const string insertSql =
        """
        INSERT INTO screenshot_fts (screenshot_id, file_name, ocr_text)
        VALUES (@ScreenshotId, @FileName, @OcrText);
        """;

        connection.Execute(deleteSql, new { ScreenshotId = screenshotId });
        connection.Execute(insertSql, new
        {
            ScreenshotId = screenshotId,
            FileName = fileName ?? string.Empty,
            OcrText = ocrText ?? string.Empty
        });
    }

    public void RebuildSearchIndex()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM screenshot_fts;", transaction: transaction);

        const string insertSql =
        """
        INSERT INTO screenshot_fts (screenshot_id, file_name, ocr_text)
        SELECT id, file_name, IFNULL(ocr_text, '')
        FROM screenshots;
        """;
        connection.Execute(insertSql, transaction: transaction);

        transaction.Commit();
    }

    private List<ScreenshotRecord> SearchByTextLike(string query, int limit)
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
           OR ocr_text LIKE @Query
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new
        {
            Query = $"%{query}%",
            Limit = limit
        }).ToList();
    }

    private static string BuildFtsQuery(string rawQuery)
    {
        if (string.IsNullOrWhiteSpace(rawQuery))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(rawQuery, @"[^\w\s]", " ");
        var words = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return words.Count == 0
            ? string.Empty
            : string.Join(" AND ", words);
    }

    public static DateTime GetBestTimestamp(ScreenshotRecord record)
    {
        if (record.CreatedAt > DateTime.MinValue)
        {
            return record.CreatedAt;
        }

        if (record.ModifiedAt > DateTime.MinValue)
        {
            return record.ModifiedAt;
        }

        return record.ImportedAt;
    }

}
