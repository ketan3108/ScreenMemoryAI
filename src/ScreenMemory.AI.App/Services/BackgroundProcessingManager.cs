using System.Collections.Concurrent;
using System.Threading.Channels;
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

    private readonly Channel<ScreenshotRecord> _thumbHigh;
    private readonly Channel<ScreenshotRecord> _thumbLow;
    private readonly Channel<ScreenshotRecord> _ocrHigh;
    private readonly Channel<ScreenshotRecord> _ocrLow;

    private readonly ConcurrentDictionary<string, byte> _thumbDedup = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _ocrDedup = new(StringComparer.OrdinalIgnoreCase);

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _thumbWorker;
    private readonly Task _ocrWorker;

    private const int ChannelCapacity = 256;

    public BackgroundProcessingManager(
        ThumbnailQueueService thumbnailQueueService,
        OcrQueueService ocrQueueService)
    {
        _thumbnailQueueService = thumbnailQueueService;
        _ocrQueueService = ocrQueueService;

        var thumbOptions = new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        };

        var ocrOptions = new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        };

        _thumbHigh = Channel.CreateBounded<ScreenshotRecord>(thumbOptions);
        _thumbLow = Channel.CreateBounded<ScreenshotRecord>(thumbOptions);
        _ocrHigh = Channel.CreateBounded<ScreenshotRecord>(ocrOptions);
        _ocrLow = Channel.CreateBounded<ScreenshotRecord>(ocrOptions);

        _thumbWorker = Task.Run(() => RunThumbnailWorkerAsync(_cts.Token));
        _ocrWorker = Task.Run(() => RunOcrWorkerAsync(_cts.Token));
    }

    public void EnqueueThumbnails(IEnumerable<ScreenshotRecord> records, JobPriority priority)
    {
        _ = Task.Run(() => EnqueueInternalAsync(records, priority, _thumbHigh, _thumbLow, _thumbDedup, _cts.Token));
    }

    public void EnqueueOcr(IEnumerable<ScreenshotRecord> records, JobPriority priority)
    {
        _ = Task.Run(() => EnqueueInternalAsync(records, priority, _ocrHigh, _ocrLow, _ocrDedup, _cts.Token));
    }

    private static async Task EnqueueInternalAsync(
        IEnumerable<ScreenshotRecord> records,
        JobPriority priority,
        Channel<ScreenshotRecord> high,
        Channel<ScreenshotRecord> low,
        ConcurrentDictionary<string, byte> dedup,
        CancellationToken token)
    {
        foreach (var record in records)
        {
            if (!dedup.TryAdd(record.Id, 0))
            {
                continue;
            }

            var writer = priority == JobPriority.High ? high.Writer : low.Writer;
            try
            {
                await writer.WriteAsync(record, token);
            }
            catch
            {
                dedup.TryRemove(record.Id, out _);
                throw;
            }
        }
    }

    private async Task RunThumbnailWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var batch = await DrainBatchAsync(_thumbHigh.Reader, _thumbLow.Reader, maxBatch: 20, token);
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
            var batch = await DrainBatchAsync(_ocrHigh.Reader, _ocrLow.Reader, maxBatch: 10, token);
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

    private static async Task<List<ScreenshotRecord>> DrainBatchAsync(
        ChannelReader<ScreenshotRecord> high,
        ChannelReader<ScreenshotRecord> low,
        int maxBatch,
        CancellationToken token)
    {
        var result = new List<ScreenshotRecord>(maxBatch);

        var first = await ReadOneAsync(high, low, token);
        if (first is null)
        {
            return result;
        }

        result.Add(first);

        while (result.Count < maxBatch)
        {
            if (high.TryRead(out var h))
            {
                result.Add(h);
                continue;
            }

            if (low.TryRead(out var l))
            {
                result.Add(l);
                continue;
            }

            break;
        }

        return result;
    }

    private static async Task<ScreenshotRecord?> ReadOneAsync(
        ChannelReader<ScreenshotRecord> high,
        ChannelReader<ScreenshotRecord> low,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            if (high.TryRead(out var h))
            {
                return h;
            }

            if (low.TryRead(out var l))
            {
                return l;
            }

            var highWait = high.WaitToReadAsync(token).AsTask();
            var lowWait = low.WaitToReadAsync(token).AsTask();
            var completed = await Task.WhenAny(highWait, lowWait);

            if (completed == highWait && await highWait)
            {
                continue;
            }

            if (completed == lowWait && await lowWait)
            {
                continue;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();

        _thumbHigh.Writer.TryComplete();
        _thumbLow.Writer.TryComplete();
        _ocrHigh.Writer.TryComplete();
        _ocrLow.Writer.TryComplete();

        try
        {
            Task.WaitAll([_thumbWorker, _ocrWorker], TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Ignore shutdown issues.
        }

        _cts.Dispose();
    }
}
