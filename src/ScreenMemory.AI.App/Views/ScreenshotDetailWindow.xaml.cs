using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.App.Views;

public partial class ScreenshotDetailWindow : Window
{
    private readonly ScreenshotRecord _record;
    private readonly bool _fileExists;

    public ScreenshotDetailWindow(ScreenshotRecord record)
    {
        InitializeComponent();
        _record = record;
        _fileExists = File.Exists(_record.FilePath);
        LoadData();
    }

    private void LoadData()
    {
        FileNameText.Text = _record.FileName;
        FilePathText.Text = _record.FilePath;
        CreatedAtText.Text = _record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        FileSizeText.Text = FormatFileSize(_record.FileSizeBytes);
        OcrStatusText.Text = string.IsNullOrWhiteSpace(_record.OcrStatus) ? "pending" : _record.OcrStatus;
        AiCategoryText.Text = string.IsNullOrWhiteSpace(_record.AiCategory) ? "unknown" : _record.AiCategory;
        AiTagsText.Text = string.IsNullOrWhiteSpace(_record.AiTags) ? "No tags yet" : _record.AiTags;
        ApplicationText.Text = string.IsNullOrWhiteSpace(_record.ApplicationName) ? "Unknown" : _record.ApplicationName;
        ActiveWindowText.Text = string.IsNullOrWhiteSpace(_record.ActiveWindow) ? "Unavailable" : _record.ActiveWindow;
        AiSummaryText.Text = string.IsNullOrWhiteSpace(_record.AiSummary) ? "No summary yet" : _record.AiSummary;
        OcrTextBox.Text = _record.OcrText ?? string.Empty;

        if (_fileExists)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(_record.FilePath, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                PreviewImage.Source = image;
            }
            catch
            {
                FileNotFoundText.Text = "Preview unavailable";
                FileNotFoundText.Visibility = Visibility.Visible;
            }
        }
        else
        {
            FileNotFoundText.Visibility = Visibility.Visible;
            OpenImageButton.IsEnabled = false;
            RevealButton.IsEnabled = false;
        }
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (!_fileExists)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(_record.FilePath)
        {
            UseShellExecute = true
        });
    }

    private void RevealInExplorer_Click(object sender, RoutedEventArgs e)
    {
        if (!_fileExists)
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_record.FilePath}\"")
        {
            UseShellExecute = true
        });
    }

    private void CopyFilePath_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_record.FilePath ?? string.Empty);
    }

    private void CopyOcrText_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_record.OcrText ?? string.Empty);
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static string FormatFileSize(long bytes)
    {
        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;

        return bytes switch
        {
            >= (long)gb => $"{bytes / gb:0.##} GB",
            >= (long)mb => $"{bytes / mb:0.##} MB",
            >= (long)kb => $"{bytes / kb:0.##} KB",
            _ => $"{bytes} B"
        };
    }
}
