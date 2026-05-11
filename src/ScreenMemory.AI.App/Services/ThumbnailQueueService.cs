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

    public event Action<ThumbnailQueueProgress>? ProgressChanged;

    public ThumbnailQueueService(ThumbnailService thumbnailService, ScreenshotRepository repository)
    {
        _thumbnailService = thumbnailService;
        _repository = repository;
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

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = cancellationToken
            },
            async (record, ct) =>
            {
                try
                {
                    var thumbnailPath = _thumbnailService.GenerateThumbnail(record.FilePath);
                    record.ThumbnailPath = thumbnailPath;
                    _repository.UpdateThumbnailPath(record.Id, thumbnailPath);
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
                    }

                    if (updatedBatch.Count >= 10 || processed == pending.Count)
                    {
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
