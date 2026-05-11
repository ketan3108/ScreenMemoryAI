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

    public event Action<OcrQueueProgress>? ProgressChanged;

    public OcrQueueService(
        OcrService ocrService,
        ScreenshotRepository repository,
        int? maxConcurrency = null)
    {
        _ocrService = ocrService;
        _repository = repository;
        _maxConcurrency = maxConcurrency ?? (Environment.ProcessorCount > 8 ? 2 : 1);
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
        var updates = new List<(string Id, string OcrText, string Status)>();

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
                lock (sync)
                {
                    updates.Add((record.Id, text, status));
                    processed++;

                    if (updates.Count >= 10 || processed == pending.Count)
                    {
                        _repository.UpdateOcrBatch(updates);
                        updates.Clear();
                    }

                    progress = new OcrQueueProgress
                    {
                        Processed = processed,
                        Total = pending.Count,
                        CurrentFile = record.FileName
                    };
                }

                ProgressChanged?.Invoke(progress);
            });
    }
}
