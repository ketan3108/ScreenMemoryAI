using System.IO;

namespace ScreenMemory.AI.App.Services;

public class ScreenshotScannerService
{
    private static readonly string[] Extensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".webp"
    };

    public List<string> FindScreenshots(IEnumerable<string> folders)
    {
        var results = new List<string>();

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
                continue;

            try
            {
                var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                    .Where(file => Extensions.Contains(Path.GetExtension(file).ToLowerInvariant()));

                results.AddRange(files);
            }
            catch
            {
                // Ignore inaccessible folders for now
            }
        }

        return results.Distinct().ToList();
    }
}