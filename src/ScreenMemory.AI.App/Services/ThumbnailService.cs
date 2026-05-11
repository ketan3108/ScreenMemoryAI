using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ScreenMemory.AI.Core.Services;

public class ThumbnailService
{
    private readonly string _thumbnailFolder;

    public ThumbnailService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var folder = Path.Combine(appData, "ScreenMemory AI");

        _thumbnailFolder = Path.Combine(folder, "thumbnails");

        Directory.CreateDirectory(_thumbnailFolder);
    }

    public string GenerateThumbnail(string imagePath)
    {
        var safeName = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(imagePath))
            .Replace("/", "_")
            .Replace("+", "-")
            .Replace("=", "");

        var thumbnailPath = Path.Combine(_thumbnailFolder, $"{safeName}.jpg");

        if (File.Exists(thumbnailPath))
            return thumbnailPath;

        using var image = SixLabors.ImageSharp.Image.Load(imagePath);

        image.Mutate(x =>
        {
            x.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(320, 180),
                Mode = ResizeMode.Max
            });
        });

        image.Save(thumbnailPath, new JpegEncoder
        {
            Quality = 75
        });

        return thumbnailPath;
    }
}