using System.Diagnostics;
using System.IO;
using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Tesseract;

namespace ScreenMemory.AI.App.Services;

public class OcrService : IDisposable
{
    private const string Language = "eng";
    private readonly ConcurrentBag<TesseractEngine> _enginePool = new();
    private readonly string _tessdataPath;
    private readonly int _maxPoolSize;
    private int _pooledCount;
    private bool _disposed;

    public OcrService()
    {
        _tessdataPath = ResolveTessdataPath();
        _maxPoolSize = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken token)
    {
        string? tempPath = null;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                var engine = RentEngine();
                tempPath = PreprocessForOcr(imagePath);

                try
                {
                    using var img = Pix.LoadFromFile(tempPath ?? imagePath);
                    using var page = engine.Process(img);
                    return page.GetText()?.Trim() ?? string.Empty;
                }
                finally
                {
                    ReturnEngine(engine);
                }
            }, token);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OcrService] OCR failed for '{imagePath}'. {ex.Message}");
            return string.Empty;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPath) && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Ignore cleanup issues.
                }
            }
        }
    }

    private TesseractEngine RentEngine()
    {
        if (_enginePool.TryTake(out var engine))
        {
            Interlocked.Decrement(ref _pooledCount);
            return engine;
        }

        return new TesseractEngine(_tessdataPath, Language, EngineMode.Default);
    }

    private void ReturnEngine(TesseractEngine engine)
    {
        if (_disposed)
        {
            engine.Dispose();
            return;
        }

        if (_pooledCount >= _maxPoolSize)
        {
            engine.Dispose();
            return;
        }

        _enginePool.Add(engine);
        Interlocked.Increment(ref _pooledCount);
    }

    private static string? PreprocessForOcr(string imagePath)
    {
        using var image = SixLabors.ImageSharp.Image.Load(imagePath);

        if (image.Width > 2200)
        {
            var targetHeight = (int)Math.Round(image.Height * (2200.0 / image.Width));
            image.Mutate(x => x.Resize(2200, targetHeight));

            image.Mutate(x =>
            {
                x.Grayscale();
                x.Contrast(1.15f);
            });

            var resizedPath = Path.Combine(Path.GetTempPath(), $"screenmemory_ocr_{Guid.NewGuid():N}.png");
            image.Save(resizedPath, new PngEncoder());
            return resizedPath;
        }

        // Keep smaller images on fast path; avoid extra temp encode/decode cost.
        return null;
    }

    private static string ResolveTessdataPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidatePaths = new[]
        {
            Path.Combine(baseDir, "tessdata"),
            Path.Combine(Directory.GetCurrentDirectory(), "tessdata"),
            Environment.GetEnvironmentVariable("TESSDATA_PREFIX"),
            @"C:\Program Files\Tesseract-OCR\tessdata"
        };

        foreach (var path in candidatePaths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            var normalized = path!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var trainedDataFile = Path.Combine(normalized, $"{Language}.traineddata");
            if (Directory.Exists(normalized) && File.Exists(trainedDataFile))
            {
                return normalized;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate 'tessdata' with eng.traineddata. " +
            "Bundle a 'tessdata' folder next to the app executable or set TESSDATA_PREFIX.");
    }

    public void Dispose()
    {
        _disposed = true;
        while (_enginePool.TryTake(out var engine))
        {
            try
            {
                engine.Dispose();
            }
            catch
            {
                // Ignore cleanup exceptions.
            }
        }
    }
}
