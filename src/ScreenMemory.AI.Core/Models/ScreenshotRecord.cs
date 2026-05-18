using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenMemory.AI.Core.Models;

public class ScreenshotRecord : INotifyPropertyChanged
{
    private string _thumbnailPath = string.Empty;
    private string _ocrStatus = "pending";
    private string _aiStatus = "pending";
    private string _aiCategory = "unknown";
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

    public string OcrStatus
    {
        get => _ocrStatus;
        set
        {
            if (_ocrStatus == value)
            {
                return;
            }

            _ocrStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OcrStatusLabel));
            OnPropertyChanged(nameof(IsOcrReady));
            OnPropertyChanged(nameof(IsProcessing));
        }
    }

    public string OcrStatusLabel => FormatStatusLabel(OcrStatus);

    public bool IsOcrReady => OcrStatus.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                              OcrStatus.Equals("no_text", StringComparison.OrdinalIgnoreCase);

    public DateTime? OcrProcessedAt { get; set; }

    public string ActiveWindow { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    public string AiCategory
    {
        get => _aiCategory;
        set
        {
            if (_aiCategory == value)
            {
                return;
            }

            _aiCategory = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiCategoryLabel));
        }
    }

    public string AiCategoryLabel => string.IsNullOrWhiteSpace(AiCategory) ||
                                     AiCategory.Equals("unknown", StringComparison.OrdinalIgnoreCase)
        ? "Unsorted"
        : AiCategory;

    public string AiTags { get; set; } = string.Empty;

    public string AiSummary { get; set; } = string.Empty;

    public float AiConfidence { get; set; }

    public string AiStatus
    {
        get => _aiStatus;
        set
        {
            if (_aiStatus == value)
            {
                return;
            }

            _aiStatus = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(AiStatusLabel));
            OnPropertyChanged(nameof(IsAiReady));
            OnPropertyChanged(nameof(IsProcessing));
        }
    }

    public string AiStatusLabel => FormatStatusLabel(AiStatus);

    public bool IsAiReady => AiStatus.Equals("completed", StringComparison.OrdinalIgnoreCase);

    public bool IsProcessing => !IsOcrReady || (OcrStatus.Equals("completed", StringComparison.OrdinalIgnoreCase) && !IsAiReady);

    public bool HasAiSummary => !string.IsNullOrWhiteSpace(AiSummary);

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

    public string CreatedAtLabel => CreatedAt.ToLocalTime().ToString("h:mm tt");

    private static string FormatStatusLabel(string? status) => status?.Trim().ToLowerInvariant() switch
    {
        "completed" => "Ready",
        "no_text" => "No text found",
        "failed" => "Needs review",
        "pending" or "" or null => "Processing",
        _ => status
    };

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

