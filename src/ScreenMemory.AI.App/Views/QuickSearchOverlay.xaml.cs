using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.App.Views;

public partial class QuickSearchOverlay : Window
{
    private sealed class OverlayResultItem : INotifyPropertyChanged
    {
        public ScreenshotRecord Record { get; init; } = new();
        public string FileName => Record.FileName;
        public string CreatedAtLabel => Record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        public string MetadataLabel
        {
            get
            {
                var category = string.IsNullOrWhiteSpace(Record.AiCategory) ? "unknown" : Record.AiCategory;
                var app = string.IsNullOrWhiteSpace(Record.ApplicationName) ? string.Empty : $" · {Record.ApplicationName}";
                return $"{category}{app}";
            }
        }

        private ImageSource? _thumbnailImage;
        public ImageSource? ThumbnailImage
        {
            get => _thumbnailImage;
            set
            {
                if (!ReferenceEquals(_thumbnailImage, value))
                {
                    _thumbnailImage = value;
                    OnPropertyChanged();
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private readonly ScreenshotRepository _repository;
    private readonly Action<ScreenshotRecord> _openRecord;
    private readonly Action<ScreenshotRecord> _revealRecord;
    
    // Atomic lock for search debounce
    private int _searchVersion;

    private const double CompactHeight = 100;
    private const double BaseHeight = 110;
    private const double RowHeight = 68;
    private const double MinOverlayHeight = 100;
    private const double MaxOverlayHeight = 620;

    public QuickSearchOverlay(
        ScreenshotRepository repository,
        Action<ScreenshotRecord> openRecord,
        Action<ScreenshotRecord> revealRecord)
    {
        InitializeComponent();
        _repository = repository;
        _openRecord = openRecord;
        _revealRecord = revealRecord;

        // FIX 1: Strip out the aggressive recycling mode that causes blank rows on resize
        VirtualizingPanel.SetVirtualizationMode(ResultsList, VirtualizationMode.Standard);
    }

    public void Open()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Max(MinOverlayHeight, workArea.Height - 40);
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + (workArea.Height * 0.2);

        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.Text = string.Empty; 
    }

    public void InvalidateSearchCache()
    {
        // No-op: cache intentionally removed
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var currentVersion = Interlocked.Increment(ref _searchVersion);

        if (string.IsNullOrWhiteSpace(query))
        {
            SetCompactState();
            return;
        }

        await RunSearchAsync(query, currentVersion);
    }

    private async Task RunSearchAsync(string query, int version)
    {
            await Task.Delay(120);
        
        // If user typed something else while we were waiting, die instantly
        if (Volatile.Read(ref _searchVersion) != version) return;

        List<OverlayResultItem> results;
        try
        {
            results = await Task.Run(() =>
            {
                var records = _repository.SearchHybrid(query, 10);
                return records.Select(r => new OverlayResultItem
                {
                    Record = r,
                    ThumbnailImage = LoadThumbnail(r.ThumbnailPath, r.FilePath)
                }).ToList();
            });
        }
        catch
        {
            return;
        }

        // Final check before hitting UI thread
        if (Volatile.Read(ref _searchVersion) != version) return;

        ResultsList.ItemsSource = results;

        if (results.Count > 0)
        {
            // FIX 2: ListBox stays Visible. We only toggle the visual divider.
            ResultsList.Visibility = Visibility.Visible; 
            Divider.Visibility = Visibility.Visible;
            
            ResultsList.SelectedIndex = 0;
            UpdateOverlaySize(results.Count);
            
            // FIX 3: Force WPF layout engine to instantly render the children
            ResultsList.UpdateLayout();
        }
        else
        {
            SetCompactState();
        }
    }

    private void SetCompactState()
    {
        ResultsList.ItemsSource = null;
        Divider.Visibility = Visibility.Collapsed;
        
        // DO NOT set ResultsList to Collapsed. Let the empty ItemsSource handle the 0-height.
        Height = CompactHeight;
    }

    private void UpdateOverlaySize(int count)
    {
        if (count <= 0)
        {
            Height = CompactHeight;
            return;
        }

        var target = BaseHeight + (Math.Min(count, 7) * RowHeight);
        var maxAllowed = Math.Min(MaxOverlayHeight, MaxHeight);
        Height = Math.Max(MinOverlayHeight, Math.Min(maxAllowed, target));
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
            case Key.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case Key.Down when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when ResultsList.Items.Count > 0:
                ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
                e.Handled = true;
                break;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => OpenSelected();

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayResultItem item })
        {
            _openRecord(item.Record);
            Hide();
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OverlayResultItem item })
        {
            _revealRecord(item.Record);
            Hide();
        }
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpenSelected()
    {
        if (ResultsList.SelectedItem is OverlayResultItem item)
        {
            _openRecord(item.Record);
            Hide();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }

    private static BitmapImage? LoadThumbnail(string thumbnailPath, string sourceImagePath)
    {
        var pathToLoad = thumbnailPath;

        if (string.IsNullOrWhiteSpace(pathToLoad) || !File.Exists(pathToLoad))
        {
            pathToLoad = sourceImagePath;
        }

        if (string.IsNullOrWhiteSpace(pathToLoad) || !File.Exists(pathToLoad))
        {
            return null;
        }

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = 160;
            bmp.UriSource = new Uri(pathToLoad, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
