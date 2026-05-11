using System.Diagnostics;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using Tesseract;

namespace ScreenMemory.AI.App.Services;

public class OcrService
{
    private const string TessdataPath = @"C:\Program Files\Tesseract-OCR\tessdata";

    public async Task<string> ExtractTextAsync(string imagePath, CancellationToken token)
    {
        string? tempPath = null;
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                using var engine = new TesseractEngine(TessdataPath, "eng", EngineMode.Default);
                tempPath = PreprocessForOcr(imagePath);
                using var img = Pix.LoadFromFile(tempPath ?? imagePath);
                using var page = engine.Process(img);
                return page.GetText()?.Trim() ?? string.Empty;
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

    private static string PreprocessForOcr(string imagePath)
    {
        using var image = SixLabors.ImageSharp.Image.Load(imagePath);

        if (image.Width > 2200)
        {
            var targetHeight = (int)Math.Round(image.Height * (2200.0 / image.Width));
            image.Mutate(x => x.Resize(2200, targetHeight));
        }

        image.Mutate(x =>
        {
            x.Grayscale();
            x.Contrast(1.15f);
        });

        var tempPath = Path.Combine(Path.GetTempPath(), $"screenmemory_ocr_{Guid.NewGuid():N}.png");
        image.Save(tempPath, new PngEncoder());
        return tempPath;
    }
}
