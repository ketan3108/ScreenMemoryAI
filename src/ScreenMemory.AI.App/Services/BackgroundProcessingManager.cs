using System.Collections.Concurrent;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.App.Services;

public enum JobPriority
{
    High,
    Low
}

public class BackgroundProcessingManager : IDisposable
{
    private readonly ThumbnailQueueService _thumbnailQueueService;
    private readonly OcrQueueService _ocrQueueService;

    private readonly ConcurrentQueue<ScreenshotRecord> _thumbHigh = new();
    private readonly ConcurrentQueue<ScreenshotRecord> _thumbLow = new();
    private readonly ConcurrentQueue<ScreenshotRecord> _ocrHigh = new();
    private readonly ConcurrentQueue<ScreenshotRecord> _ocrLow = new();

    private readonly ConcurrentDictionary<string, byte> _thumbDedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ocrDedup = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _thumbSignal = new(0);
    private readonly SemaphoreSlim _ocrSignal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _thumbWorker;
    private readonly Task _ocrWorker;

    public BackgroundProcessingManager(
        ThumbnailQueueService thumbnailQueueService,
        OcrQueueService ocrQueueService)
    {
        _thumbnailQueueService = thumbnailQueueService;
        _ocrQueueService = ocrQueueService;
        _thumbWorker = Task.Run(() => RunThumbnailWorkerAsync(_cts.Token));
        _ocrWorker = Task.Run(() => RunOcrWorkerAsync(_cts.Token));
    }

    public void EnqueueThumbnails(IEnumerable<ScreenshotRecord> records, JobPriority priority)
    {
        foreach (var record in records)
        {
            if (!_thumbDedup.TryAdd(record.Id, 0))
            {
                continue;
            }

            if (priority == JobPriority.High)
            {
                _thumbHigh.Enqueue(record);
            }
            else
            {
                _thumbLow.Enqueue(record);
            }

            _thumbSignal.Release();
        }
    }

    public void EnqueueOcr(IEnumerable<ScreenshotRecord> records, JobPriority priority)
    {
        foreach (var record in records)
        {
            if (!_ocrDedup.TryAdd(record.Id, 0))
            {
                continue;
            }

            if (priority == JobPriority.High)
            {
                _ocrHigh.Enqueue(record);
            }
            else
            {
                _ocrLow.Enqueue(record);
            }

            _ocrSignal.Release();
        }
    }

    private async Task RunThumbnailWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _thumbSignal.WaitAsync(token);

            var batch = DrainQueue(_thumbHigh, _thumbLow, maxBatch: 20);
            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await _thumbnailQueueService.GenerateMissingThumbnailsAsync(batch, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                foreach (var item in batch)
                {
                    _thumbDedup.TryRemove(item.Id, out _);
                }
            }
        }
    }

    private async Task RunOcrWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await _ocrSignal.WaitAsync(token);

            var batch = DrainQueue(_ocrHigh, _ocrLow, maxBatch: 10);
            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await _ocrQueueService.ProcessAsync(batch, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            finally
            {
                foreach (var item in batch)
                {
                    _ocrDedup.TryRemove(item.Id, out _);
                }
            }
        }
    }

    private static List<ScreenshotRecord> DrainQueue(
        ConcurrentQueue<ScreenshotRecord> high,
        ConcurrentQueue<ScreenshotRecord> low,
        int maxBatch)
    {
        var result = new List<ScreenshotRecord>(maxBatch);
        while (result.Count < maxBatch && high.TryDequeue(out var nextHigh))
        {
            result.Add(nextHigh);
        }

        while (result.Count < maxBatch && low.TryDequeue(out var nextLow))
        {
            result.Add(nextLow);
        }

        return result;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thumbSignal.Release();
        _ocrSignal.Release();
        try
        {
            Task.WaitAll([_thumbWorker, _ocrWorker], TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Ignore shutdown issues.
        }

        _thumbSignal.Dispose();
        _ocrSignal.Dispose();
        _cts.Dispose();
    }
}

