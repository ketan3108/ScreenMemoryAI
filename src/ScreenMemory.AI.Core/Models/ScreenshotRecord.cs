using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ScreenMemory.AI.Core.Models;

public class ScreenshotRecord : INotifyPropertyChanged
{
    private string _thumbnailPath = string.Empty;
    private bool _isSelected;

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

