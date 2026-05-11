using System.IO;
using System.Windows;
using ScreenMemory.AI.App.Services;
using ScreenMemory.AI.App.Views;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App;

public partial class MainWindow : Window
{
    private readonly AppSettingsService _settingsService = new();
    private readonly ScreenshotScannerService _scannerService = new();
    private readonly DatabaseService _databaseService = new();
    private readonly ScreenshotRepository _repository;
    private readonly ThumbnailService _thumbnailService = new();
    private readonly ThumbnailQueueService _thumbnailQueueService;
    private readonly IndexingService _indexingService;
    private readonly FileWatcherService _fileWatcherService;
    private CancellationTokenSource? _thumbnailQueueCts;

    private const int HomeRecentLimit = 12;
    private const int SearchLimit = 100;
    private const int CollectionLimit = 100;

    public ViewMode CurrentViewMode { get; private set; } = ViewMode.Home;
    public string CurrentCollection { get; private set; } = string.Empty;
    public string CurrentSearchQuery { get; private set; } = string.Empty;
    public List<ScreenshotRecord> DisplayedScreenshots { get; private set; } = [];

    public MainWindow()
    {
        InitializeComponent();

        _databaseService.Initialize();
        _repository = new ScreenshotRepository(_databaseService);
        _indexingService = new IndexingService(_repository);
        _thumbnailQueueService = new ThumbnailQueueService(_thumbnailService, _repository);
        _thumbnailQueueService.ProgressChanged += ThumbnailQueueService_ProgressChanged;
        _fileWatcherService = new FileWatcherService(_settingsService, _indexingService, _thumbnailService, _repository);
        _fileWatcherService.ScreenshotIndexed += FileWatcherService_ScreenshotIndexed;
        _fileWatcherService.Start();

        LoadHomeDashboard();
    }

    protected override void OnClosed(EventArgs e)
    {
        _fileWatcherService.ScreenshotIndexed -= FileWatcherService_ScreenshotIndexed;
        _fileWatcherService.Dispose();
        base.OnClosed(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow { Owner = this };
        settingsWindow.ShowDialog();
        _fileWatcherService.Restart();
    }

    private async void IndexNow_Click(object sender, RoutedEventArgs e)
    {
        var settings = _settingsService.Load();

        if (settings.WatchedFolders.Count == 0)
        {
            System.Windows.MessageBox.Show(
                "Please choose at least one screenshot folder first.",
                "No folders selected",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        StatusText.Text = "Indexing screenshots...";

        try
        {
            await Task.Run(() =>
            {
                var screenshots = _scannerService.FindScreenshots(settings.WatchedFolders);
                var records = new List<ScreenshotRecord>();

                foreach (var path in screenshots)
                {
                    try
                    {
                        var fileInfo = new FileInfo(path);

                        records.Add(new ScreenshotRecord
                        {
                            FilePath = path,
                            FileName = fileInfo.Name,
                            FileSizeBytes = fileInfo.Length,
                            CreatedAt = fileInfo.CreationTimeUtc,
                            ModifiedAt = fileInfo.LastWriteTimeUtc,
                            ThumbnailPath = string.Empty
                        });
                    }
                    catch
                    {
                        // Ignore unreadable file metadata.
                    }
                }

                _repository.InsertManyIfNotExists(records);
            });

            LoadHomeDashboard();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message,
                "Indexing Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        LoadHomeDashboard();
    }

    private void Recent_Click(object sender, RoutedEventArgs e)
    {
        ShowResults("Recent", _repository.GetRecent(CollectionLimit));
    }

    private void Invoices_Click(object sender, RoutedEventArgs e) => ShowCollection("Invoices", ["invoice", "receipt", "bill", "payment"]);
    private void Errors_Click(object sender, RoutedEventArgs e) => ShowCollection("Errors", ["error", "bug", "exception", "crash"]);
    private void CodeSnippets_Click(object sender, RoutedEventArgs e) => ShowCollection("Code Snippets", ["code", "script", "snippet", "terminal"]);
    private void Conversations_Click(object sender, RoutedEventArgs e) => ShowCollection("Conversations", ["chat", "whatsapp", "discord", "slack"]);

    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        ShowResults("Favorites", []);
        ResultsMetaText.Text = "Favorites coming soon";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        CurrentSearchQuery = query;

        if (string.IsNullOrWhiteSpace(query))
        {
            LoadHomeDashboard();
            return;
        }

        CurrentViewMode = ViewMode.Search;
        CurrentCollection = string.Empty;

        var results = _repository.SearchByFileName(query, SearchLimit);
        ShowResults($"Results for '{query}'", results);
    }

    private void LoadHomeDashboard()
    {
        CurrentViewMode = ViewMode.Home;
        CurrentCollection = string.Empty;
        CurrentSearchQuery = string.Empty;

        StopThumbnailQueue();
        HomeDashboardPanel.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Collapsed;

        ImportedCountText.Text = _repository.Count().ToString("N0");
        OcrReadyCountText.Text = _repository.CountOcrReady().ToString("N0");
        AiTaggedCountText.Text = _repository.CountAiTagged().ToString("N0");

        var recent = _repository.GetRecent(HomeRecentLimit);
        RecentScreenshotsList.ItemsSource = recent;
        StatusText.Text = "Search across all indexed screenshots.";

        StartThumbnailQueue(recent);
    }

    private void ShowCollection(string name, IEnumerable<string> keywords)
    {
        CurrentViewMode = ViewMode.Collection;
        CurrentCollection = name;
        CurrentSearchQuery = string.Empty;

        var results = _repository.SearchByKeywords(keywords, CollectionLimit);
        ShowResults(name, results);
    }

    private void ShowResults(string title, List<ScreenshotRecord> results)
    {
        HomeDashboardPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;

        DisplayedScreenshots = results;
        ResultsTitleText.Text = title;
        ResultsMetaText.Text = $"{results.Count} result(s)";

        ResultsList.ItemsSource = results;
        StatusText.Text = $"{results.Count} items loaded";

        StartThumbnailQueue(results);
    }

    private void StartThumbnailQueue(List<ScreenshotRecord> records)
    {
        _thumbnailQueueCts?.Cancel();
        _thumbnailQueueCts?.Dispose();
        _thumbnailQueueCts = new CancellationTokenSource();

        _ = _thumbnailQueueService.GenerateMissingThumbnailsAsync(records, _thumbnailQueueCts.Token);
    }

    private void StopThumbnailQueue()
    {
        _thumbnailQueueCts?.Cancel();
        _thumbnailQueueCts?.Dispose();
        _thumbnailQueueCts = null;
    }

    private void ThumbnailQueueService_ProgressChanged(ThumbnailQueueProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (CurrentViewMode == ViewMode.Home)
            {
                if (RecentScreenshotsList.ItemsSource is IEnumerable<ScreenshotRecord> recent)
                {
                    RecentScreenshotsList.ItemsSource = null;
                    RecentScreenshotsList.ItemsSource = recent.ToList();
                }
            }
            else
            {
                if (ResultsList.ItemsSource is IEnumerable<ScreenshotRecord> results)
                {
                    ResultsList.ItemsSource = null;
                    ResultsList.ItemsSource = results.ToList();
                }
            }

            if (progress.Total > 0)
            {
                StatusText.Text = $"Generating thumbnails {progress.Processed}/{progress.Total}";
            }
        });
    }

    private void FileWatcherService_ScreenshotIndexed(object? sender, ScreenshotIndexedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            ImportedCountText.Text = _repository.Count().ToString("N0");

            if (CurrentViewMode == ViewMode.Home)
            {
                var recent = _repository.GetRecent(HomeRecentLimit);
                RecentScreenshotsList.ItemsSource = recent;
                StartThumbnailQueue(recent);
                StatusText.Text = "New screenshot indexed";
                return;
            }

            if (CurrentViewMode == ViewMode.Search)
            {
                if (!string.IsNullOrWhiteSpace(CurrentSearchQuery) &&
                    e.Record.FileName.Contains(CurrentSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    var refreshed = _repository.SearchByFileName(CurrentSearchQuery, SearchLimit);
                    ShowResults($"Results for '{CurrentSearchQuery}'", refreshed);
                    StatusText.Text = "New screenshot indexed";
                }
                return;
            }

            if (CurrentViewMode == ViewMode.Collection &&
                MatchesCurrentCollection(e.Record.FileName))
            {
                var keywords = GetCollectionKeywords(CurrentCollection);
                var refreshed = _repository.SearchByKeywords(keywords, CollectionLimit);
                ShowResults(CurrentCollection, refreshed);
                StatusText.Text = "New screenshot indexed";
            }
        });
    }

    private bool MatchesCurrentCollection(string fileName)
    {
        var keywords = GetCollectionKeywords(CurrentCollection);
        return keywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetCollectionKeywords(string collection)
    {
        return collection switch
        {
            "Invoices" => ["invoice", "receipt", "bill", "payment"],
            "Errors" => ["error", "bug", "exception", "crash"],
            "Code Snippets" => ["code", "script", "snippet", "terminal"],
            "Conversations" => ["chat", "whatsapp", "discord", "slack"],
            _ => []
        };
    }
}
