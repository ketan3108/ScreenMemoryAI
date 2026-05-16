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
using ScreenMemory.AI.App.Services;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

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
    private readonly IAiSemanticService _aiSemanticService;
    private readonly Action<SearchMode> _searchModeChanged;
    private readonly Action<ScreenshotRecord> _openRecord;
    private readonly Action<ScreenshotRecord> _revealRecord;
    private CancellationTokenSource? _searchCts;
    private SearchMode _searchMode;
    
    // Atomic lock for search debounce
    private int _searchVersion;

    private const double CompactHeight = 108;
    private const double BaseHeight = 118;
    private const double RowHeight = 76;
    private const double MinOverlayHeight = 108;
    private const double MaxOverlayHeight = 620;

    public QuickSearchOverlay(
        ScreenshotRepository repository,
        IAiSemanticService aiSemanticService,
        SearchMode initialSearchMode,
        Action<SearchMode> searchModeChanged,
        Action<ScreenshotRecord> openRecord,
        Action<ScreenshotRecord> revealRecord)
    {
        InitializeComponent();
        _repository = repository;
        _aiSemanticService = aiSemanticService;
        _searchMode = initialSearchMode;
        _searchModeChanged = searchModeChanged;
        _openRecord = openRecord;
        _revealRecord = revealRecord;

        // FIX 1: Strip out the aggressive recycling mode that causes blank rows on resize
        VirtualizingPanel.SetVirtualizationMode(ResultsList, VirtualizationMode.Standard);
        UpdateSearchModeUi();
    }

    public void Open()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Max(MinOverlayHeight, workArea.Height - 40);
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + (workArea.Height * 0.2);

        Show();
        Activate();
        UpdateSearchModeUi();
        SearchBox.Focus();
        SearchBox.Text = string.Empty; 
    }

    public void SetSearchMode(SearchMode mode, bool notifyOwner = true)
    {
        if (mode == SearchMode.Ai && !_aiSemanticService.IsInitialized)
        {
            mode = SearchMode.Keyword;
        }

        if (_searchMode == mode)
        {
            UpdateSearchModeUi();
            return;
        }

        _searchMode = mode;
        UpdateSearchModeUi();
        if (notifyOwner)
        {
            _searchModeChanged(mode);
        }

        var currentVersion = Interlocked.Increment(ref _searchVersion);
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            _searchCts?.Cancel();
            _searchCts?.Dispose();
            _searchCts = new CancellationTokenSource();
            _ = RunSearchAsync(SearchBox.Text.Trim(), currentVersion, _searchCts.Token);
        }
    }

    public void InvalidateSearchCache()
    {
        // No-op: cache intentionally removed
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var currentVersion = Interlocked.Increment(ref _searchVersion);
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(query))
        {
            SetCompactState();
            return;
        }

        await RunSearchAsync(query, currentVersion, token);
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox textBox && string.IsNullOrEmpty(textBox.Text))
        {
            textBox.CaretIndex = 0;
        }
    }

    private void SearchBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || !string.IsNullOrEmpty(textBox.Text))
        {
            return;
        }

        e.Handled = true;
        if (!textBox.IsKeyboardFocusWithin)
        {
            textBox.Focus();
        }

        textBox.CaretIndex = 0;
    }

    private async Task RunSearchAsync(string query, int version, CancellationToken token)
    {
        try
        {
            await Task.Delay(120, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        
        // If user typed something else while we were waiting, die instantly
        if (Volatile.Read(ref _searchVersion) != version) return;

        List<OverlayResultItem> results;
        try
        {
            var records = await SearchRecordsAsync(query, 10, token);
            results = await Task.Run(() => records.Select(r => new OverlayResultItem
            {
                Record = r,
                ThumbnailImage = LoadThumbnail(r.ThumbnailPath, r.FilePath)
            }).ToList(), token);
        }
        catch (OperationCanceledException)
        {
            return;
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

    private async Task<List<ScreenshotRecord>> SearchRecordsAsync(string query, int limit, CancellationToken token)
    {
        return await ScreenshotSearchService.SearchAsync(_repository, _aiSemanticService, query, limit, _searchMode, token);
    }

    private void KeywordSearchMode_Click(object sender, RoutedEventArgs e) => SetSearchMode(SearchMode.Keyword);

    private void AiSearchMode_Click(object sender, RoutedEventArgs e) => SetSearchMode(SearchMode.Ai);

    private void UpdateSearchModeUi()
    {
        if (OverlayKeywordSearchModeButton is null || OverlayAiSearchModeButton is null)
        {
            return;
        }

        var aiAvailable = _aiSemanticService.IsInitialized;
        OverlayAiSearchModeButton.IsEnabled = aiAvailable;
        OverlayAiSearchModeButton.ToolTip = aiAvailable
            ? "Use AI semantic search"
            : $"AI search unavailable: {_aiSemanticService.AvailabilityState}";

        OverlayKeywordSearchModeButton.Tag = _searchMode == SearchMode.Keyword ? "Selected" : null;
        OverlayAiSearchModeButton.Tag = _searchMode == SearchMode.Ai ? "Selected" : null;
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
            case Key.I when Keyboard.Modifiers.HasFlag(ModifierKeys.Control):
                SetSearchMode(_searchMode == SearchMode.Ai ? SearchMode.Keyword : SearchMode.Ai);
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

    protected override void OnClosed(EventArgs e)
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        base.OnClosed(e);
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

