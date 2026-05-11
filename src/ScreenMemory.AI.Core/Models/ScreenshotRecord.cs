namespace ScreenMemory.AI.Core.Models;

public class ScreenshotRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    public string ThumbnailPath { get; set; } = string.Empty;

    public string OcrText { get; set; } = string.Empty;

    public string OcrStatus { get; set; } = "pending";
}

