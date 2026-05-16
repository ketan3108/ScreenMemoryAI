using System.Diagnostics;
using System.IO;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace ScreenMemory.AI.App.Services;

public class OcrService : IDisposable
{
    private const uint MinImageDimension = 40;
    private readonly OcrEngine? _ocrEngine;
    private readonly SemaphoreSlim _recognitionLock = new(1, 1);
    private readonly uint _maxDimension;
    private bool _disposed;

    public OcrService()
    {
        _ocrEngine = OcrEngine.TryCreateFromUserProfileLanguages();

        if (_ocrEngine is null)
        {
            var fallbackLanguage = new Windows.Globalization.Language("en-US");
            if (OcrEngine.IsLanguageSupported(fallbackLanguage))
            {
                _ocrEngine = OcrEngine.TryCreateFromLanguage(fallbackLanguage);
            }
        }

        if (_ocrEngine is null)
        {
            Debug.WriteLine("[OcrService] Failed to initialize Windows.Media.Ocr. No supported language packs found.");
            return;
        }

        _maxDimension = OcrEngine.MaxImageDimension;
        Debug.WriteLine($"[OcrService] WinRT OCR initialized. Max image dimension: {_maxDimension}.");
    }

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken token)
    {
        if (_ocrEngine is null || _disposed)
        {
            return string.Empty;
        }

        try
        {
            await using var fileStream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var randomAccessStream = fileStream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(randomAccessStream).AsTask(token);

            using var softwareBitmap = await DecodeForOcrAsync(decoder, token);
            await _recognitionLock.WaitAsync(token);
            try
            {
                var result = await _ocrEngine.RecognizeAsync(softwareBitmap).AsTask(token);
                return result.Text?.Trim() ?? string.Empty;
            }
            finally
            {
                _recognitionLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine($"[OcrService] OCR canceled for '{imagePath}'.");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[OcrService] WinRT OCR failed for '{imagePath}'. {ex.Message}");
            throw;
        }
    }

    private async Task<SoftwareBitmap> DecodeForOcrAsync(BitmapDecoder decoder, CancellationToken token)
    {
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var targetWidth = width;
        var targetHeight = height;
        var needsResize = false;

        if (_maxDimension > 0 && (width > _maxDimension || height > _maxDimension))
        {
            var scale = Math.Min(
                (double)_maxDimension / width,
                (double)_maxDimension / height);

            targetWidth = Math.Max(1, (uint)Math.Round(width * scale));
            targetHeight = Math.Max(1, (uint)Math.Round(height * scale));
            needsResize = true;
        }
        else if (width < MinImageDimension || height < MinImageDimension)
        {
            var scale = Math.Max(
                (double)MinImageDimension / width,
                (double)MinImageDimension / height);

            targetWidth = Math.Max(MinImageDimension, (uint)Math.Round(width * scale));
            targetHeight = Math.Max(MinImageDimension, (uint)Math.Round(height * scale));
            needsResize = true;
        }

        if (needsResize)
        {
            var transform = new BitmapTransform
            {
                ScaledWidth = targetWidth,
                ScaledHeight = targetHeight
            };

            return await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(token);
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore).AsTask(token);
    }

    public void Dispose()
    {
        _disposed = true;
        _recognitionLock.Dispose();
    }
}
