using System.Diagnostics;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.App.Services;

public class OcrQueueProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public string CurrentFile { get; init; } = string.Empty;
}

public class OcrQueueService
{
    private readonly OcrService _ocrService;
    private readonly ScreenshotRepository _repository;
    private readonly int _maxConcurrency;
    private const int OcrUpdateBatchSize = 25;

    public event Action<OcrQueueProgress>? ProgressChanged;

    public OcrQueueService(
        OcrService ocrService,
        ScreenshotRepository repository,
        int? maxConcurrency = null)
    {
        _ocrService = ocrService;
        _repository = repository;
        _maxConcurrency = maxConcurrency ?? Math.Max(2, Math.Min(Environment.ProcessorCount - 1, 6));
    }

    public async Task ProcessAsync(IEnumerable<ScreenshotRecord> records, CancellationToken token = default)
    {
        var pending = records
            .Where(r => string.IsNullOrWhiteSpace(r.OcrStatus) ||
                        r.OcrStatus.Equals("pending", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        var processed = 0;
        var sync = new object();
        var stagedUpdates = new List<(string Id, string OcrText, string Status)>(OcrUpdateBatchSize);

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrency,
                CancellationToken = token
            },
            async (record, ct) =>
            {
                string text;
                string status;
                try
                {
                    text = await _ocrService.ExtractTextAsync(record.FilePath, ct);
                    status = string.IsNullOrWhiteSpace(text) ? "failed" : "completed";
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrQueueService] OCR queue failed for '{record.FilePath}'. {ex.Message}");
                    text = string.Empty;
                    status = "failed";
                }

                OcrQueueProgress progress;
                List<(string Id, string OcrText, string Status)>? flushBatch = null;
                lock (sync)
                {
                    stagedUpdates.Add((record.Id, text, status));
                    processed++;

                    if (stagedUpdates.Count >= OcrUpdateBatchSize)
                    {
                        flushBatch = stagedUpdates.ToList();
                        stagedUpdates.Clear();
                    }

                    progress = new OcrQueueProgress
                    {
                        Processed = processed,
                        Total = pending.Count,
                        CurrentFile = record.FileName
                    };
                }

                if (flushBatch is not null)
                {
                    _repository.UpdateOcrBatch(flushBatch);
                }

                ProgressChanged?.Invoke(progress);
            });

        List<(string Id, string OcrText, string Status)> remaining;
        lock (sync)
        {
            remaining = stagedUpdates.ToList();
            stagedUpdates.Clear();
        }

        if (remaining.Count > 0)
        {
            _repository.UpdateOcrBatch(remaining);
        }
    }
}
