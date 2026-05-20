using Dapper;
using ScreenMemory.AI.Core.Models;
using System.Runtime.InteropServices;
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
            imported_at,
            active_window,
            process_name,
            application_name
        )
        VALUES (
            @Id,
            @FilePath,
            @FileName,
            @FileSizeBytes,
            @CreatedAt,
            @ModifiedAt,
            @ThumbnailPath,
            @ImportedAt,
            @ActiveWindow,
            @ProcessName,
            @ApplicationName
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

        const string sql = "SELECT COUNT(1) FROM screenshots WHERE file_path = @Path COLLATE NOCASE;";
        return connection.ExecuteScalar<int>(sql, new { Path = path }) > 0;
    }

    public ScreenshotRecord? DeleteByFilePath(string path)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string lookupSql =
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE file_path = @Path COLLATE NOCASE
        LIMIT 1;
        """;

        var record = connection.QueryFirstOrDefault<ScreenshotRecord>(lookupSql, new { Path = path });
        if (record is null)
        {
            return null;
        }

        using var transaction = connection.BeginTransaction();
        DeleteScreenshotRows(connection, transaction, record.Id);
        transaction.Commit();

        DeleteThumbnailFile(record.ThumbnailPath);
        return record;
    }

    public List<ScreenshotRecord> DeleteMissingFiles()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string lookupSql =
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots;
        """;

        var missingRecords = connection.Query<ScreenshotRecord>(lookupSql)
            .Where(record => !File.Exists(record.FilePath))
            .ToList();

        if (missingRecords.Count == 0)
        {
            return [];
        }

        using var transaction = connection.BeginTransaction();
        foreach (var record in missingRecords)
        {
            DeleteScreenshotRows(connection, transaction, record.Id);
        }

        transaction.Commit();

        foreach (var record in missingRecords)
        {
            DeleteThumbnailFile(record.ThumbnailPath);
        }

        return missingRecords;
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit })
            .OrderByDescending(GetBestTimestamp)
            .Take(limit)
            .ToList();
    }

    public List<ScreenshotRecord> GetRecentCards(int limit = 50)
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
            ocr_status AS OcrStatus,
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit })
            .OrderByDescending(GetBestTimestamp)
            .Take(limit)
            .ToList();
    }

    public ScreenshotRecord? GetById(string id)
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE id = @Id
        LIMIT 1;
        """;

        return connection.QueryFirstOrDefault<ScreenshotRecord>(sql, new { Id = id });
    }

    public List<ScreenshotRecord> GetFavorites(int limit = 100)
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE is_favorite = 1
        ORDER BY COALESCE(NULLIF(updated_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit }).ToList();
    }

    public void SetFavorite(string id, bool isFavorite)
        => UpdateFlag(id, "is_favorite", isFavorite);

    private void UpdateFlag(string id, string columnName, bool value)
    {
        if (columnName is not "is_favorite")
        {
            throw new ArgumentOutOfRangeException(nameof(columnName));
        }

        using var connection = _databaseService.CreateConnection();
        connection.Open();

        var sql =
        $"""
        UPDATE screenshots
        SET {columnName} = @Value,
            updated_at = @UpdatedAt
        WHERE id = @Id;
        """;

        connection.Execute(sql, new
        {
            Id = id,
            Value = value ? 1 : 0,
            UpdatedAt = DateTime.UtcNow
        });
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            updated_at AS UpdatedAt
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
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
            ocr_status = @Status,
            ocr_processed_at = @ProcessedAt,
            updated_at = @UpdatedAt
        WHERE id = @Id;
        """;

        var now = DateTime.UtcNow;
        connection.Execute(sql, new
        {
            Id = id,
            OcrText = ocrText,
            Status = status,
            ProcessedAt = now,
            UpdatedAt = now
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
            ocr_status = @Status,
            ocr_processed_at = @ProcessedAt,
            updated_at = @UpdatedAt
        WHERE id = @Id;
        """;

        var now = DateTime.UtcNow;
        connection.Execute(updateSql, list.Select(x => new
        {
            x.Id,
            x.OcrText,
            x.Status,
            ProcessedAt = now,
            UpdatedAt = now
        }), transaction);

        const string lookupSql =
        """
        SELECT
            id AS Id,
            file_name AS FileName,
            active_window AS ActiveWindow,
            application_name AS ApplicationName,
            process_name AS ProcessName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary
        FROM screenshots
        WHERE id IN @Ids;
        """;

        var idSet = list.Select(x => x.Id).Distinct().ToList();
        var ftsRows = connection.Query<FtsMetadataRow>(lookupSql, new { Ids = idSet }, transaction)
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var item in list)
        {
            if (ftsRows.TryGetValue(item.Id, out var ftsRow))
            {
                const string deleteSql = "DELETE FROM screenshot_fts WHERE screenshot_id = @ScreenshotId;";
                const string insertSql =
                """
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
                VALUES (
                    @ScreenshotId,
                    @FileName,
                    @OcrText,
                    @ActiveWindow,
                    @ApplicationName,
                    @ProcessName,
                    @AiCategory,
                    @AiTags,
                    @AiSummary
                );
                """;

                connection.Execute(deleteSql, new { ScreenshotId = item.Id }, transaction);
                connection.Execute(insertSql, new
                {
                    ScreenshotId = item.Id,
                    FileName = ftsRow.FileName ?? string.Empty,
                    OcrText = item.OcrText ?? string.Empty,
                    ActiveWindow = ftsRow.ActiveWindow ?? string.Empty,
                    ApplicationName = ftsRow.ApplicationName ?? string.Empty,
                    ProcessName = ftsRow.ProcessName ?? string.Empty,
                    AiCategory = ftsRow.AiCategory ?? "unknown",
                    AiTags = ftsRow.AiTags ?? string.Empty,
                    AiSummary = ftsRow.AiSummary ?? string.Empty
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
            s.imported_at AS ImportedAt,
            s.ocr_processed_at AS OcrProcessedAt,
            s.active_window AS ActiveWindow,
            s.process_name AS ProcessName,
            s.application_name AS ApplicationName,
            s.ai_category AS AiCategory,
            s.ai_tags AS AiTags,
            s.ai_summary AS AiSummary,
            s.ai_confidence AS AiConfidence,
            s.ai_status AS AiStatus,
            s.ai_error AS AiError,
            s.ai_analyzed_at AS AiAnalyzedAt,
            s.embedding_vector AS EmbeddingVector,
            s.is_favorite AS IsFavorite,
            s.updated_at AS UpdatedAt
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
            return SearchMetadataLike(query, limit);
        }
    }

    public List<ScreenshotRecord> SearchHybrid(string query, int limit = 100)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return GetRecent(limit);
        }

        var ftsResults = SearchFullText(trimmed, limit);
        var metadataResults = SearchMetadataLike(trimmed, Math.Min(limit, 30));

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

        append(metadataResults);
        append(ftsResults);

        if (merged.Count == 0 && trimmed.Length >= 3)
        {
            append(SearchBroadLike(trimmed, Math.Min(limit, 30)));
        }

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
            conditions.Add($"""
                (
                    file_name LIKE @{key}
                    OR ocr_text LIKE @{key}
                    OR active_window LIKE @{key}
                    OR application_name LIKE @{key}
                    OR process_name LIKE @{key}
                    OR ai_category LIKE @{key}
                    OR ai_tags LIKE @{key}
                    OR ai_summary LIKE @{key}
                )
                """);
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE {whereClause}
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, dynamicParameters).ToList();
    }

    public List<ScreenshotRecord> SearchSmartCollection(
        string[] categories,
        string[] tags,
        string[] keywords,
        int limit = 100)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        var parameters = new DynamicParameters();
        var conditions = new List<string>();

        if (categories.Length > 0)
        {
            conditions.Add("ai_category IN @Categories");
            parameters.Add("Categories", categories);
        }

        for (var i = 0; i < tags.Length; i++)
        {
            var key = $"Tag{i}";
            conditions.Add($"ai_tags LIKE @{key}");
            parameters.Add(key, $"%{tags[i]}%");
        }

        for (var i = 0; i < keywords.Length; i++)
        {
            var key = $"Keyword{i}";
            conditions.Add($"""
                (
                    file_name LIKE @{key}
                    OR ocr_text LIKE @{key}
                    OR active_window LIKE @{key}
                    OR application_name LIKE @{key}
                    OR process_name LIKE @{key}
                    OR ai_summary LIKE @{key}
                )
                """);
            parameters.Add(key, $"%{keywords[i]}%");
        }

        if (conditions.Count == 0)
        {
            return [];
        }

        parameters.Add("Limit", limit);

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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE {string.Join(" OR ", conditions)}
        ORDER BY
            CASE WHEN ai_status = 'completed' THEN 0 ELSE 1 END,
            COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, parameters).ToList();
    }

    public void ResetPendingAiForCompletedOcr()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string sql =
        """
        UPDATE screenshots
        SET ai_status = 'pending',
            ai_error = NULL,
            updated_at = @UpdatedAt
        WHERE ocr_status = 'completed'
          AND ocr_text IS NOT NULL
          AND ocr_text != '';
        """;

        connection.Execute(sql, new { UpdatedAt = DateTime.UtcNow });
    }

    public List<ScreenshotRecord> SearchByEmbedding(float[] queryEmbedding, int limit = 100)
    {
        if (queryEmbedding.Length == 0)
        {
            return [];
        }

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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE embedding_vector IS NOT NULL
          AND ai_status = 'completed'
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT 2000;
        """;

        return connection.Query<ScreenshotRecord>(sql)
            .Select(record => new
            {
                Record = record,
                Score = CosineSimilarity(queryEmbedding, record.EmbeddingVector)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => GetBestTimestamp(item.Record))
            .Take(limit)
            .Select(item =>
            {
                item.Record.EmbeddingVector = null;
                return item.Record;
            })
            .ToList();
    }

    public int CountOcrReady()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots WHERE ocr_status = 'completed';");
    }

    public int CountPendingOcr()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM screenshots
            WHERE ocr_status IS NULL
               OR ocr_status = ''
               OR ocr_status = 'pending';
            """);
    }

    public int CountFailedOcr()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots WHERE ocr_status = 'failed';");
    }

    public int CountAiTagged()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots WHERE ai_status = 'completed';");
    }

    public int CountPendingAi()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            """
            SELECT COUNT(*)
            FROM screenshots
            WHERE ocr_status = 'completed'
              AND (ai_status IS NULL OR ai_status = '' OR ai_status = 'pending');
            """);
    }

    public int CountFailedAi()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        return connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM screenshots WHERE ai_status = 'failed';");
    }

    public List<ScreenshotRecord> GetPendingAi(int limit = 100)
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE ocr_status = 'completed'
          AND (ai_status IS NULL OR ai_status = '' OR ai_status = 'pending')
          AND ocr_text IS NOT NULL
          AND ocr_text != ''
        ORDER BY COALESCE(NULLIF(created_at, ''), NULLIF(modified_at, ''), imported_at) DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new { Limit = limit }).ToList();
    }

    public void UpdateAiMetadataBatch(IEnumerable<AiMetadataUpdate> updates)
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
        SET active_window = @ActiveWindow,
            process_name = @ProcessName,
            application_name = @ApplicationName,
            ai_category = @AiCategory,
            ai_tags = @AiTags,
            ai_summary = @AiSummary,
            ai_confidence = @AiConfidence,
            ai_status = @AiStatus,
            ai_error = @AiError,
            ai_analyzed_at = @AiAnalyzedAt,
            embedding_vector = @EmbeddingVector,
            updated_at = @UpdatedAt
        WHERE id = @Id;
        """;

        var now = DateTime.UtcNow;
        connection.Execute(updateSql, list.Select(x => new
        {
            x.Id,
            ActiveWindow = x.ActiveWindow ?? string.Empty,
            ProcessName = x.ProcessName ?? string.Empty,
            ApplicationName = x.ApplicationName ?? string.Empty,
            AiCategory = x.AiCategory ?? "unknown",
            AiTags = x.AiTags ?? string.Empty,
            AiSummary = x.AiSummary ?? string.Empty,
            x.AiConfidence,
            AiStatus = x.AiStatus ?? "pending",
            x.AiError,
            x.AiAnalyzedAt,
            x.EmbeddingVector,
            UpdatedAt = now
        }), transaction);

        const string lookupSql =
        """
        SELECT
            id AS Id,
            file_name AS FileName,
            ocr_text AS OcrText,
            active_window AS ActiveWindow,
            application_name AS ApplicationName,
            process_name AS ProcessName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary
        FROM screenshots
        WHERE id IN @Ids;
        """;

        var idSet = list.Select(x => x.Id).Distinct().ToList();
        var ftsRows = connection.Query<FtsMetadataRow>(lookupSql, new { Ids = idSet }, transaction).ToList();
        UpsertFtsBatch(connection, transaction, ftsRows);

        transaction.Commit();
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE file_path = @Path COLLATE NOCASE
        LIMIT 1;
        """;

        return connection.QueryFirstOrDefault<ScreenshotRecord>(sql, new { Path = path });
    }

    public void UpsertFts(string screenshotId, string fileName, string ocrText)
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        const string lookupSql =
        """
        SELECT
            id AS Id,
            file_name AS FileName,
            ocr_text AS OcrText,
            active_window AS ActiveWindow,
            application_name AS ApplicationName,
            process_name AS ProcessName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary
        FROM screenshots
        WHERE id = @Id
        LIMIT 1;
        """;

        var row = connection.QueryFirstOrDefault<FtsMetadataRow>(lookupSql, new { Id = screenshotId })
            ?? new FtsMetadataRow
            {
                Id = screenshotId,
                FileName = fileName,
                OcrText = ocrText
            };

        row.FileName = string.IsNullOrWhiteSpace(row.FileName) ? fileName : row.FileName;
        row.OcrText = ocrText ?? row.OcrText ?? string.Empty;

        using var transaction = connection.BeginTransaction();
        UpsertFtsBatch(connection, transaction, [row]);
        transaction.Commit();
    }

    private static void UpsertFtsBatch(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        IEnumerable<FtsMetadataRow> rows)
    {
        const string deleteSql = "DELETE FROM screenshot_fts WHERE screenshot_id = @ScreenshotId;";
        const string insertSql =
        """
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
        VALUES (
            @ScreenshotId,
            @FileName,
            @OcrText,
            @ActiveWindow,
            @ApplicationName,
            @ProcessName,
            @AiCategory,
            @AiTags,
            @AiSummary
        );
        """;

        foreach (var row in rows)
        {
            connection.Execute(deleteSql, new { ScreenshotId = row.Id }, transaction);
            connection.Execute(insertSql, new
            {
                ScreenshotId = row.Id,
                FileName = row.FileName ?? string.Empty,
                OcrText = row.OcrText ?? string.Empty,
                ActiveWindow = row.ActiveWindow ?? string.Empty,
                ApplicationName = row.ApplicationName ?? string.Empty,
                ProcessName = row.ProcessName ?? string.Empty,
                AiCategory = row.AiCategory ?? "unknown",
                AiTags = row.AiTags ?? string.Empty,
                AiSummary = row.AiSummary ?? string.Empty
            }, transaction);
        }
    }

    private static void DeleteScreenshotRows(
        System.Data.IDbConnection connection,
        System.Data.IDbTransaction transaction,
        string screenshotId)
    {
        connection.Execute(
            "DELETE FROM screenshot_fts WHERE screenshot_id = @ScreenshotId;",
            new { ScreenshotId = screenshotId },
            transaction);

        connection.Execute(
            "DELETE FROM screenshots WHERE id = @ScreenshotId;",
            new { ScreenshotId = screenshotId },
            transaction);
    }

    private static void DeleteThumbnailFile(string? thumbnailPath)
    {
        if (string.IsNullOrWhiteSpace(thumbnailPath) || !File.Exists(thumbnailPath))
        {
            return;
        }

        try
        {
            File.Delete(thumbnailPath);
        }
        catch
        {
            // Thumbnail cleanup is best-effort; the database row is already removed.
        }
    }

    public void RebuildSearchIndex()
    {
        using var connection = _databaseService.CreateConnection();
        connection.Open();

        using var transaction = connection.BeginTransaction();

        connection.Execute("DELETE FROM screenshot_fts;", transaction: transaction);

        const string insertSql =
        """
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
        """;
        connection.Execute(insertSql, transaction: transaction);

        transaction.Commit();
    }

    private List<ScreenshotRecord> SearchMetadataLike(string query, int limit)
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE file_name LIKE @Query
           OR active_window LIKE @Query
           OR application_name LIKE @Query
           OR process_name LIKE @Query
           OR ai_category LIKE @Query
           OR ai_tags LIKE @Query
        ORDER BY imported_at DESC
        LIMIT @Limit;
        """;

        return connection.Query<ScreenshotRecord>(sql, new
        {
            Query = $"%{query}%",
            Limit = limit
        }).ToList();
    }

    private List<ScreenshotRecord> SearchBroadLike(string query, int limit)
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
            imported_at AS ImportedAt,
            ocr_processed_at AS OcrProcessedAt,
            active_window AS ActiveWindow,
            process_name AS ProcessName,
            application_name AS ApplicationName,
            ai_category AS AiCategory,
            ai_tags AS AiTags,
            ai_summary AS AiSummary,
            ai_confidence AS AiConfidence,
            ai_status AS AiStatus,
            ai_error AS AiError,
            ai_analyzed_at AS AiAnalyzedAt,
            embedding_vector AS EmbeddingVector,
            is_favorite AS IsFavorite,
            updated_at AS UpdatedAt
        FROM screenshots
        WHERE ocr_text LIKE @Query
           OR ai_summary LIKE @Query
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

        if (words.Count == 0)
        {
            return string.Empty;
        }

        var prefixWords = words.Select(w => $"{w}*");
        return string.Join(" AND ", prefixWords);
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

    private sealed class FtsMetadataRow
    {
        public string Id { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string OcrText { get; set; } = string.Empty;
        public string ActiveWindow { get; set; } = string.Empty;
        public string ApplicationName { get; set; } = string.Empty;
        public string ProcessName { get; set; } = string.Empty;
        public string AiCategory { get; set; } = "unknown";
        public string AiTags { get; set; } = string.Empty;
        public string AiSummary { get; set; } = string.Empty;
    }

    private static double CosineSimilarity(float[] left, byte[]? rightBytes)
    {
        if (rightBytes is null || rightBytes.Length == 0 || rightBytes.Length % sizeof(float) != 0)
        {
            return 0;
        }

        var right = MemoryMarshal.Cast<byte, float>(rightBytes.AsSpan());
        if (left.Length == 0 || left.Length != right.Length)
        {
            return 0;
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Length; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        if (leftMagnitude <= 0 || rightMagnitude <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude));
    }
}




