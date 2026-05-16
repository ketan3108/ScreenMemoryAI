using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenMemory.AI.Core.Models;

public class ScreenshotRecord : INotifyPropertyChanged
{
    private string _thumbnailPath = string.Empty;
    private bool _isSelected;
    private bool _isFavorite;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public long FileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;

    public string ThumbnailPath
    {
        get => _thumbnailPath;
        set
        {
            if (_thumbnailPath == value)
            {
                return;
            }

            _thumbnailPath = value;
            OnPropertyChanged();
        }
    }

    public string OcrText { get; set; } = string.Empty;

    public string OcrStatus { get; set; } = "pending";

    public DateTime? OcrProcessedAt { get; set; }

    public string ActiveWindow { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    public string AiCategory { get; set; } = "unknown";

    public string AiTags { get; set; } = string.Empty;

    public string AiSummary { get; set; } = string.Empty;

    public float AiConfidence { get; set; }

    public string AiStatus { get; set; } = "pending";

    public string? AiError { get; set; }

    public DateTime? AiAnalyzedAt { get; set; }

    public byte[]? EmbeddingVector { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value)
            {
                return;
            }

            _isFavorite = value;
            OnPropertyChanged();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed record OcrUpdate(
    string Id,
    string OcrText,
    string Status,
    DateTime ProcessedAt);

public sealed record AiMetadataUpdate(
    string Id,
    string ActiveWindow,
    string ProcessName,
    string ApplicationName,
    string AiCategory,
    string AiTags,
    string AiSummary,
    float AiConfidence,
    string AiStatus,
    DateTime? AiAnalyzedAt,
    byte[]? EmbeddingVector,
    string? AiError);

