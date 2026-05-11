using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public class ThumbnailQueueProgress
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public IReadOnlyList<ScreenshotRecord> UpdatedRecords { get; init; } = [];
}

public class ThumbnailQueueService
{
    private readonly ThumbnailService _thumbnailService;
    private readonly ScreenshotRepository _repository;
    private readonly int _maxConcurrency;

    public event Action<ThumbnailQueueProgress>? ProgressChanged;

    public ThumbnailQueueService(
        ThumbnailService thumbnailService,
        ScreenshotRepository repository,
        int maxConcurrency = 3)
    {
        _thumbnailService = thumbnailService;
        _repository = repository;
        _maxConcurrency = Math.Max(1, maxConcurrency);
    }

    public async Task GenerateMissingThumbnailsAsync(
        IEnumerable<ScreenshotRecord> records,
        CancellationToken cancellationToken = default)
    {
        var pending = records
            .Where(r => string.IsNullOrWhiteSpace(r.ThumbnailPath))
            .ToList();

        if (pending.Count == 0)
        {
            ProgressChanged?.Invoke(new ThumbnailQueueProgress
            {
                Processed = 0,
                Total = 0,
                UpdatedRecords = []
            });
            return;
        }

        var processed = 0;
        var sync = new object();
        var updatedBatch = new List<ScreenshotRecord>();
        var dbBatch = new List<(string Id, string ThumbnailPath)>();

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = _maxConcurrency,
                CancellationToken = cancellationToken
            },
            async (record, ct) =>
            {
                try
                {
                    var thumbnailPath = _thumbnailService.GenerateThumbnail(record.FilePath);
                    record.ThumbnailPath = thumbnailPath;
                }
                catch
                {
                    // Ignore files that cannot be thumbnailed.
                }

                ThumbnailQueueProgress? progressToRaise = null;

                lock (sync)
                {
                    processed++;

                    if (!string.IsNullOrWhiteSpace(record.ThumbnailPath))
                    {
                        updatedBatch.Add(record);
                        dbBatch.Add((record.Id, record.ThumbnailPath));
                    }

                    if (updatedBatch.Count >= 10 || processed == pending.Count)
                    {
                        _repository.UpdateThumbnailPathsBatch(dbBatch);
                        dbBatch.Clear();

                        progressToRaise = new ThumbnailQueueProgress
                        {
                            Processed = processed,
                            Total = pending.Count,
                            UpdatedRecords = updatedBatch.ToList()
                        };
                        updatedBatch.Clear();
                    }
                }

                if (progressToRaise is not null)
                {
                    ProgressChanged?.Invoke(progressToRaise);
                }

                await Task.CompletedTask;
            });
    }
}
