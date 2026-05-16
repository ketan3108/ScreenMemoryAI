using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using ScreenMemory.AI.App.Services;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.Tests;

public sealed class DatabaseMigrationTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "ScreenMemoryAI.Tests",
        $"{Guid.NewGuid():N}.db");

    [Fact]
    public void InitializeCreatesAiMetadataSchemaAndSearchIndex()
    {
        var database = new DatabaseService(_databasePath);
        database.Initialize();

        using var connection = database.CreateConnection();
        connection.Open();

        var columns = connection.Query<string>("SELECT name FROM pragma_table_info('screenshots');").ToList();

        columns.Should().Contain([
            "active_window",
            "process_name",
            "application_name",
            "ai_category",
            "ai_tags",
            "ai_summary",
            "ai_confidence",
            "ai_status",
            "ai_error",
            "ai_analyzed_at",
            "embedding_vector",
            "ocr_processed_at",
            "updated_at"
        ]);

        var ftsColumns = connection.Query<string>("SELECT name FROM pragma_table_info('screenshot_fts');").ToList();
        ftsColumns.Should().Contain([
            "active_window",
            "application_name",
            "process_name",
            "ai_category",
            "ai_tags",
            "ai_summary"
        ]);

        connection.ExecuteScalar<int>("SELECT MAX(id) FROM schema_migrations;")
            .Should()
            .Be(3);
    }

    [Fact]
    public void AiMetadataBatchUpdatesRowsAndFullTextSearch()
    {
        var database = new DatabaseService(_databasePath);
        database.Initialize();
        var repository = new ScreenshotRepository(database);

        var record = new ScreenshotRecord
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = Path.Combine(Path.GetTempPath(), "screenmemory-test.png"),
            FileName = "screenmemory-test.png",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };

        repository.InsertIfNotExists(record);
        repository.UpdateOcrBatch([(record.Id, "public class InvoiceProcessor { }", "completed")]);
        repository.UpdateAiMetadataBatch(
        [
            new AiMetadataUpdate(
                record.Id,
                "InvoiceProcessor.cs - Visual Studio",
                "devenv",
                "Visual Studio",
                "Code Snippet",
                "#code, InvoiceProcessor",
                "public class InvoiceProcessor",
                0.9f,
                "completed",
                DateTime.UtcNow,
                null,
                null)
        ]);

        repository.CountAiTagged().Should().Be(1);
        repository.SearchHybrid("InvoiceProcessor", 10)
            .Should()
            .ContainSingle(x => x.Id == record.Id);
        repository.SearchHybrid("Visual Studio", 10)
            .Should()
            .ContainSingle(x => x.Id == record.Id);
    }

    [Fact]
    public async Task SearchServiceAiModeFallsBackToKeywordSearchWhenAiUnavailable()
    {
        var database = new DatabaseService(_databasePath);
        database.Initialize();
        var repository = new ScreenshotRepository(database);

        var record = new ScreenshotRecord
        {
            Id = Guid.NewGuid().ToString(),
            FilePath = Path.Combine(Path.GetTempPath(), "screenmemory-ai-mode-test.png"),
            FileName = "screenmemory-ai-mode-test.png",
            CreatedAt = DateTime.UtcNow,
            ModifiedAt = DateTime.UtcNow,
            ImportedAt = DateTime.UtcNow
        };

        repository.InsertIfNotExists(record);
        repository.UpdateOcrBatch([(record.Id, "payment receipt from online checkout", "completed")]);

        var results = await ScreenshotSearchService.SearchAsync(
            repository,
            new UnavailableSemanticService(),
            "payment receipt",
            10,
            SearchMode.Ai);

        results.Should().ContainSingle(x => x.Id == record.Id);
    }

    private sealed class UnavailableSemanticService : IAiSemanticService
    {
        public bool IsInitialized => false;

        public string AvailabilityState => "Unavailable";

        public Task<AiSemanticResult> AnalyzeAsync(string text, CancellationToken token = default)
            => Task.FromResult(new AiSemanticResult { ErrorMessage = "Unavailable" });
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
