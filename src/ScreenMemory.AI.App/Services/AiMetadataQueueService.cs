using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public sealed class AiMetadataQueueProgress
{
    public int Processed { get; init; }

    public int Total { get; init; }

    public string CurrentFile { get; init; } = string.Empty;
}

public sealed class AiMetadataQueueService
{
    private readonly ScreenshotRepository _repository;
    private readonly IAiSemanticService _semanticService;
    private readonly int _maxConcurrency;
    private const int AiUpdateBatchSize = 25;

    public event Action<AiMetadataQueueProgress>? ProgressChanged;

    public AiMetadataQueueService(
        ScreenshotRepository repository,
        IAiSemanticService semanticService,
        IActiveWindowService activeWindowService,
        int? maxConcurrency = null)
    {
        _repository = repository;
        _semanticService = semanticService;
        _maxConcurrency = maxConcurrency ?? Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 2));
    }

    public async Task ProcessPendingAsync(int limit = 100, CancellationToken token = default)
    {
        var pending = _repository.GetPendingAi(limit);
        await ProcessAsync(pending, token);
    }

    public async Task ProcessAsync(IEnumerable<ScreenshotRecord> records, CancellationToken token = default)
    {
        var pending = records
            .Where(r => r.OcrStatus.Equals("completed", StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(r.OcrText) &&
                        (string.IsNullOrWhiteSpace(r.AiStatus) ||
                         r.AiStatus.Equals("pending", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var processed = 0;
        var sync = new object();
        var stagedUpdates = new List<AiMetadataUpdate>(AiUpdateBatchSize);

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrency,
                CancellationToken = token
            },
            async (record, ct) =>
            {
                var update = await CreateMetadataUpdateAsync(record, ct);

                AiMetadataQueueProgress progress;
                List<AiMetadataUpdate>? flushBatch = null;
                lock (sync)
                {
                    stagedUpdates.Add(update);
                    processed++;

                    if (stagedUpdates.Count >= AiUpdateBatchSize)
                    {
                        flushBatch = stagedUpdates.ToList();
                        stagedUpdates.Clear();
                    }

                    progress = new AiMetadataQueueProgress
                    {
                        Processed = processed,
                        Total = pending.Count,
                        CurrentFile = record.FileName
                    };
                }

                if (flushBatch is not null)
                {
                    _repository.UpdateAiMetadataBatch(flushBatch);
                }

                if (progress.Processed < progress.Total)
                {
                    ProgressChanged?.Invoke(progress);
                }
            });

        List<AiMetadataUpdate> remaining;
        lock (sync)
        {
            remaining = stagedUpdates.ToList();
            stagedUpdates.Clear();
        }

        if (remaining.Count > 0)
        {
            _repository.UpdateAiMetadataBatch(remaining);
        }

        ProgressChanged?.Invoke(new AiMetadataQueueProgress
        {
            Processed = pending.Count,
            Total = pending.Count,
            CurrentFile = pending[^1].FileName
        });
    }

    private async Task<AiMetadataUpdate> CreateMetadataUpdateAsync(ScreenshotRecord record, CancellationToken token)
    {
        try
        {
            var windowContext = CaptureWindowContext(record);
            var semanticResult = await _semanticService.AnalyzeAsync(record.OcrText, token);

            return new AiMetadataUpdate(
                record.Id,
                windowContext.WindowTitle,
                windowContext.ProcessName,
                windowContext.ApplicationName,
                semanticResult.PrimaryCategory,
                semanticResult.TagsAsString,
                semanticResult.Summary,
                semanticResult.CategoryConfidence,
                semanticResult.Success ? "completed" : "failed",
                DateTime.UtcNow,
                SerializeEmbedding(semanticResult.Embeddings),
                semanticResult.ErrorMessage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AiMetadataUpdate(
                record.Id,
                record.ActiveWindow,
                record.ProcessName,
                record.ApplicationName,
                "unknown",
                string.Empty,
                string.Empty,
                0f,
                "failed",
                DateTime.UtcNow,
                null,
                ex.Message);
        }
    }

    private WindowContext CaptureWindowContext(ScreenshotRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.ActiveWindow) ||
            !string.IsNullOrWhiteSpace(record.ApplicationName) ||
            !string.IsNullOrWhiteSpace(record.ProcessName))
        {
            return new WindowContext
            {
                WindowTitle = record.ActiveWindow,
                ProcessName = record.ProcessName,
                ApplicationName = record.ApplicationName
            };
        }

        return new WindowContext();
    }

    private static byte[]? SerializeEmbedding(float[] embeddings)
    {
        if (embeddings.Length == 0)
        {
            return null;
        }

        var bytes = new byte[embeddings.Length * sizeof(float)];
        Buffer.BlockCopy(embeddings, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
