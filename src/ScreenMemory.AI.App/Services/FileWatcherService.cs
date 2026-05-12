using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public class ScreenshotIndexedEventArgs : EventArgs
{
    public ScreenshotRecord Record { get; init; } = new();
    public bool IsNewInsert { get; init; }
    public int BatchSize { get; init; } = 1;
}

public class FileWatcherService : IDisposable
{
    private readonly AppSettingsService _settingsService;
    private readonly IndexingService _indexingService;
    private readonly ThumbnailService _thumbnailService;
    private readonly ScreenMemory.AI.Core.Data.ScreenshotRepository _repository;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, DateTime> _recentEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly SemaphoreSlim _workerLock = new(1, 1);

    private static readonly TimeSpan QueueSettleDelay = TimeSpan.FromMilliseconds(1200);
    private const int MaxBatchPerDrain = 512;

    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public event EventHandler<ScreenshotIndexedEventArgs>? ScreenshotIndexed;

    public FileWatcherService(
        AppSettingsService settingsService,
        IndexingService indexingService,
        ThumbnailService thumbnailService,
        ScreenMemory.AI.Core.Data.ScreenshotRepository repository)
    {
        _settingsService = settingsService;
        _indexingService = indexingService;
        _thumbnailService = thumbnailService;
        _repository = repository;
    }

    public void Start()
    {
        Restart();
    }

    public void Restart()
    {
        Stop();

        _cts = new CancellationTokenSource();
        StartWatchers(_settingsService.Load().WatchedFolders);
        _workerTask = Task.Run(() => RunWorkerAsync(_cts.Token));
    }

    public void Stop()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }

        lock (_watchers)
        {
            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Created -= OnCreatedOrChanged;
                watcher.Changed -= OnCreatedOrChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Error -= OnWatcherError;
                watcher.Dispose();
            }

            _watchers.Clear();
        }
    }

    private void StartWatchers(IEnumerable<string> folders)
    {
        foreach (var folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
                    Filter = "*.*",
                    InternalBufferSize = 65536,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnCreatedOrChanged;
                watcher.Changed += OnCreatedOrChanged;
                watcher.Renamed += OnRenamed;
                watcher.Error += OnWatcherError;

                lock (_watchers)
                {
                    _watchers.Add(watcher);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileWatcherService] Failed to watch folder '{folder}'. {ex.Message}");
            }
        }
    }

    private void OnCreatedOrChanged(object sender, FileSystemEventArgs e)
    {
        EnqueuePath(e.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        EnqueuePath(e.FullPath);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        Debug.WriteLine($"[FileWatcherService] Watcher error: {e.GetException()?.Message}");
    }

    private void EnqueuePath(string path)
    {
        if (!IndexingService.IsSupportedImage(path))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var last = _recentEvents.GetOrAdd(path, _ => DateTime.MinValue);
        if ((now - last).TotalMilliseconds < 1200)
        {
            return;
        }

        _recentEvents[path] = now;
        _queue.Enqueue(path);
        _queueSignal.Release();

        if (_recentEvents.Count > 4000)
        {
            var cutoff = DateTime.UtcNow.AddMinutes(-10);
            foreach (var kvp in _recentEvents)
            {
                if (kvp.Value < cutoff)
                {
                    _recentEvents.TryRemove(kvp.Key, out _);
                }
            }
        }
    }

    private async Task RunWorkerAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_queue.TryDequeue(out var path))
            {
                continue;
            }

            await _workerLock.WaitAsync(token);
            try
            {
                var batchPaths = new List<string> { path };

                try
                {
                    await Task.Delay(QueueSettleDelay, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                while (batchPaths.Count < MaxBatchPerDrain && _queue.TryDequeue(out var next))
                {
                    batchPaths.Add(next);
                }

                var uniqueBatch = batchPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var processedRecords = new List<(ScreenshotRecord Record, bool IsInserted)>(uniqueBatch.Count);

                foreach (var itemPath in uniqueBatch)
                {
                    var processed = await ProcessFileAsync(itemPath, token);
                    if (processed is not null)
                    {
                        processedRecords.Add(processed.Value);
                    }
                }

                if (processedRecords.Count > 0)
                {
                    var last = processedRecords[^1];
                    ScreenshotIndexed?.Invoke(this, new ScreenshotIndexedEventArgs
                    {
                        IsNewInsert = last.IsInserted,
                        Record = last.Record,
                        BatchSize = processedRecords.Count
                    });
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileWatcherService] Processing batch failed. {ex.Message}");
            }
            finally
            {
                _workerLock.Release();
            }
        }
    }

    private async Task<(ScreenshotRecord Record, bool IsInserted)?> ProcessFileAsync(string path, CancellationToken token)
    {
        if (!await WaitForFileReadyAsync(path, token))
        {
            return null;
        }

        var result = await _indexingService.IndexSingleFileAsync(path, token);

        var record = result.Record ?? _repository.GetByFilePath(path);
        if (record is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(record.ThumbnailPath))
        {
            try
            {
                var thumb = _thumbnailService.GenerateThumbnail(path);
                _repository.UpdateThumbnailPath(record.Id, thumb);
                record.ThumbnailPath = thumb;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FileWatcherService] Thumbnail generation failed for '{path}'. {ex.Message}");
            }
        }

        // TODO: enqueue OCR for new screenshot

        return (record, result.IsInserted);
    }

    public static async Task<bool> WaitForFileReadyAsync(string path, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                return true;
            }
            catch
            {
                await Task.Delay(300, token);
            }
        }

        return false;
    }

    public void Dispose()
    {
        Stop();
        _queueSignal.Dispose();
        _workerLock.Dispose();
    }
}
