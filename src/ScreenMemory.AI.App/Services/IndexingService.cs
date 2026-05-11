using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using System.IO;

namespace ScreenMemory.AI.App.Services;

public class IndexSingleFileResult
{
    public bool IsInserted { get; init; }
    public ScreenshotRecord? Record { get; init; }
}

public class IndexingService
{
    private static readonly HashSet<string> SupportedExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".webp"
    ];

    private readonly ScreenshotRepository _repository;

    public IndexingService(ScreenshotRepository repository)
    {
        _repository = repository;
    }

    public static bool IsSupportedImage(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(ext) &&
               SupportedExtensions.Contains(ext.ToLowerInvariant());
    }

    public async Task<IndexSingleFileResult> IndexSingleFileAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !IsSupportedImage(path))
        {
            return new IndexSingleFileResult { IsInserted = false };
        }

        if (_repository.ExistsByFilePath(path))
        {
            return new IndexSingleFileResult
            {
                IsInserted = false,
                Record = _repository.GetByFilePath(path)
            };
        }

        var fileInfo = new FileInfo(path);
        var record = new ScreenshotRecord
        {
            FilePath = path,
            FileName = fileInfo.Name,
            FileSizeBytes = fileInfo.Length,
            CreatedAt = fileInfo.CreationTimeUtc,
            ModifiedAt = fileInfo.LastWriteTimeUtc,
            ThumbnailPath = string.Empty
        };

        await Task.Run(() => _repository.InsertIfNotExists(record), token);

        return new IndexSingleFileResult
        {
            IsInserted = true,
            Record = record
        };
    }
}
