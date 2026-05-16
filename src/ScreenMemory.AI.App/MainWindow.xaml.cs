using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
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
    private readonly OcrService _ocrService = new();
    private readonly OcrQueueService _ocrQueueService;
    private readonly ActiveWindowService _activeWindowService = new();
    private readonly AiSemanticService _aiSemanticService = new();
    private readonly AiMetadataQueueService _aiMetadataQueueService;
    private readonly IndexingService _indexingService;
    private readonly FileWatcherService _fileWatcherService;
    private readonly BackgroundProcessingManager _backgroundProcessingManager;
    private readonly QuickSearchOverlay _quickSearchOverlay;
    private readonly Forms.NotifyIcon _trayIcon;
    private CancellationTokenSource? _previewLoadCts;
    private CancellationTokenSource? _searchCts;
    private bool _allowClose;
    private bool _trayDisposed;
    private ScreenshotRecord? _selectedScreenshot;
    private readonly Dictionary<string, BitmapSource> _previewCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _previewCacheOrder = [];
    private readonly DispatcherTimer _ocrEtaTimer;
    private readonly System.Diagnostics.Stopwatch _ocrRunStopwatch = new();
    private const int PreviewCacheLimit = 10;

    private const int HomeRecentLimit = 12;
    private const int RecentPageLimit = 100;
    private const int SearchLimit = 60;
    private const int CollectionLimit = 100;
    private const int OcrBackfillBatchSize = 1000;
    private const int PreviewPanelWidth = 300;
    private const int HotkeyId = 9001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint VkSpace = 0x20;
    private const int WmHotkey = 0x0312;
    private bool _hotkeyRegistered;
    private bool _wndProcAttached;
    private bool _ocrTrackingActive;
    private int _ocrRunTotal;
    private bool _aiBackfillQueued;
    private SearchMode _searchMode = SearchMode.Keyword;

    public ViewMode CurrentViewMode { get; private set; } = ViewMode.Home;
    public string CurrentCollection { get; private set; } = string.Empty;
    public string CurrentSearchQuery { get; private set; } = string.Empty;
    public List<ScreenshotRecord> DisplayedScreenshots { get; private set; } = [];
    public AppSettingsData Settings => _settingsService.Load();

    public MainWindow()
    {
        InitializeComponent();

        _databaseService.Initialize();
        _repository = new ScreenshotRepository(_databaseService);
        _indexingService = new IndexingService(_repository, _activeWindowService);
        _thumbnailQueueService = new ThumbnailQueueService(_thumbnailService, _repository);
        _thumbnailQueueService.ProgressChanged += ThumbnailQueueService_ProgressChanged;
        _ocrQueueService = new OcrQueueService(_ocrService, _repository);
        _ocrQueueService.ProgressChanged += OcrQueueService_ProgressChanged;
        _aiMetadataQueueService = new AiMetadataQueueService(_repository, _aiSemanticService, _activeWindowService);
        _aiMetadataQueueService.ProgressChanged += AiMetadataQueueService_ProgressChanged;
        _ocrQueueService.SetAiMetadataQueue(_aiMetadataQueueService);
        _backgroundProcessingManager = new BackgroundProcessingManager(_thumbnailQueueService, _ocrQueueService);
        _fileWatcherService = new FileWatcherService(_settingsService, _indexingService, _thumbnailService, _repository);
        _fileWatcherService.ScreenshotIndexed += FileWatcherService_ScreenshotIndexed;
        _fileWatcherService.Start();
        _quickSearchOverlay = new QuickSearchOverlay(
            _repository,
            _aiSemanticService,
            _searchMode,
            SetSearchMode,
            OpenImageFromOverlay,
            RevealInExplorerFromOverlay);
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "ScreenMemory AI",
            Visible = true
        };
        try
        {
            var iconStream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AppIcon.ico"))?.Stream;
            if (iconStream is not null)
            {
                _trayIcon.Icon = new Drawing.Icon(iconStream);
            }
            else
            {
                _trayIcon.Icon = Drawing.SystemIcons.Application;
            }
        }
        catch
        {
            _trayIcon.Icon = Drawing.SystemIcons.Application;
        }
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open Dashboard", null, (_, _) => RestoreDashboard());
        trayMenu.Items.Add("Exit", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = trayMenu;
        _trayIcon.DoubleClick += (_, _) => RestoreDashboard();
        _ocrEtaTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _ocrEtaTimer.Tick += OcrEtaTimer_Tick;

        LoadHomeDashboard();
        UpdateSearchModeUi();
        QueueOcrBackfill();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureHotkeyRegistration();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_hotkeyRegistered)
        {
            UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
        _fileWatcherService.ScreenshotIndexed -= FileWatcherService_ScreenshotIndexed;
        _ocrQueueService.ProgressChanged -= OcrQueueService_ProgressChanged;
        _aiMetadataQueueService.ProgressChanged -= AiMetadataQueueService_ProgressChanged;
        _fileWatcherService.Dispose();
        _backgroundProcessingManager.Dispose();
        _ocrService.Dispose();
        _quickSearchOverlay.Close();
        DisposeTrayIcon();
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _ocrEtaTimer.Stop();
        _ocrEtaTimer.Tick -= OcrEtaTimer_Tick;
        base.OnClosed(e);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        base.OnClosing(e);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        CurrentViewMode = ViewMode.Settings;
        CurrentCollection = string.Empty;
        CurrentSearchQuery = string.Empty;
        SearchBox.Text = string.Empty;

        HomeDashboardPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Visible;

        LoadSettingsFolders();
        StatusText.Text = "Settings";
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
                    catch { /* Ignore unreadable file metadata. */ }
                }

                _repository.InsertManyIfNotExists(records);
            });

            _quickSearchOverlay.InvalidateSearchCache();
            LoadHomeDashboard();
            QueueOcrBackfill(limit: 10000);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                ex.Message, "Indexing Error",
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
        CurrentViewMode = ViewMode.Collection;
        CurrentCollection = "Recent";
        CurrentSearchQuery = string.Empty;
        ShowResults("Recent", _repository.GetRecent(RecentPageLimit));
    }

    private void Invoices_Click(object sender, RoutedEventArgs e)
        => ShowSmartCollection(
            "Invoices",
            ["Financial Data"],
            ["#financial"],
            ["invoice", "receipt", "bill", "payment", "amount", "total"]);

    private void Errors_Click(object sender, RoutedEventArgs e)
        => ShowSmartCollection(
            "Errors",
            ["Error Log"],
            ["#error"],
            ["error", "bug", "exception", "crash", "failed", "stack trace"]);

    private void CodeSnippets_Click(object sender, RoutedEventArgs e)
        => ShowSmartCollection(
            "Code Snippets",
            ["Code Snippet", "Configuration"],
            ["#code", "#configuration", "#filepath"],
            ["code", "script", "snippet", "terminal", "class", "function", "async"]);

    private void Conversations_Click(object sender, RoutedEventArgs e)
        => ShowSmartCollection(
            "Conversations",
            ["Communication"],
            ["#communication"],
            ["chat", "whatsapp", "discord", "slack", "teams", "email", "message"]);

    private void Favorites_Click(object sender, RoutedEventArgs e)
    {
        ShowResults("Favorites", []);
        ResultsMetaText.Text = "Favorites coming soon";
    }

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        CurrentSearchQuery = query;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(120, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (CurrentViewMode == ViewMode.Settings)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            LoadHomeDashboard();
            return;
        }

        CurrentViewMode = ViewMode.Search;
        CurrentCollection = string.Empty;

        List<ScreenshotRecord> results;
        try
        {
            results = await SearchRecordsAsync(query, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || !string.Equals(CurrentSearchQuery, query, StringComparison.Ordinal))
        {
            return;
        }

        ShowResults($"Results for '{query}'", results);
        ResultsMetaText.Text = _searchMode == SearchMode.Ai
            ? $"{results.Count} AI results for '{query}'"
            : $"{results.Count} results for '{query}'";
    }

    private async Task<List<ScreenshotRecord>> SearchRecordsAsync(string query, CancellationToken token)
    {
        return await ScreenshotSearchService.SearchAsync(_repository, _aiSemanticService, query, SearchLimit, _searchMode, token);
    }

    private void SearchModeKeyword_Click(object sender, RoutedEventArgs e) => SetSearchMode(SearchMode.Keyword);

    private void SearchModeAi_Click(object sender, RoutedEventArgs e) => SetSearchMode(SearchMode.Ai);

    private void SetSearchMode(SearchMode mode)
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
        _quickSearchOverlay.SetSearchMode(mode, notifyOwner: false);

        if (!string.IsNullOrWhiteSpace(CurrentSearchQuery) && CurrentViewMode == ViewMode.Search)
        {
            SearchBox_TextChanged(SearchBox, new System.Windows.Controls.TextChangedEventArgs(
                System.Windows.Controls.TextBox.TextChangedEvent,
                System.Windows.Controls.UndoAction.None));
        }
    }

    private void UpdateSearchModeUi()
    {
        if (KeywordSearchModeButton is null || AiSearchModeButton is null)
        {
            return;
        }

        var aiAvailable = _aiSemanticService.IsInitialized;
        AiSearchModeButton.IsEnabled = aiAvailable;
        AiSearchModeButton.ToolTip = aiAvailable
            ? "Use AI semantic search"
            : $"AI search unavailable: {_aiSemanticService.AvailabilityState}";

        KeywordSearchModeButton.Tag = _searchMode == SearchMode.Keyword ? "Selected" : null;
        AiSearchModeButton.Tag = _searchMode == SearchMode.Ai ? "Selected" : null;
        SearchBox.ToolTip = _searchMode == SearchMode.Ai
            ? "AI search by meaning"
            : "Search screenshots by filename, OCR text, and metadata";
    }

    private void LoadHomeDashboard()
    {
        CurrentViewMode = ViewMode.Home;
        CurrentCollection = string.Empty;
        CurrentSearchQuery = string.Empty;

        StopThumbnailQueue();
        HomeDashboardPanel.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed;

        ImportedCountText.Text = _repository.Count().ToString("N0");
        OcrReadyCountText.Text = _repository.CountOcrReady().ToString("N0");
        AiTaggedCountText.Text = _repository.CountAiTagged().ToString("N0");

        var recent = _repository.GetRecent(HomeRecentLimit);
        BindRecentBuckets(recent);
        StatusText.Text = "Ready";

        StartThumbnailQueue(recent);
        QueueOcrBackfill();
        QueueAiBackfill();
    }

    private void ShowCollection(string name, IEnumerable<string> keywords)
    {
        CurrentViewMode = ViewMode.Collection;
        CurrentCollection = name;
        CurrentSearchQuery = string.Empty;

        var results = _repository.SearchByKeywords(keywords, CollectionLimit);
        ShowResults(name, results);
    }

    private void ShowSmartCollection(string name, string[] categories, string[] tags, string[] keywords)
    {
        CurrentViewMode = ViewMode.Collection;
        CurrentCollection = name;
        CurrentSearchQuery = string.Empty;

        var results = _repository.SearchSmartCollection(categories, tags, keywords, CollectionLimit);
        ShowResults(name, results);
    }

    private void ShowResults(string title, List<ScreenshotRecord> results)
    {
        HomeDashboardPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
        SettingsPanel.Visibility = Visibility.Collapsed;

        DisplayedScreenshots = results;
        ResultsTitleText.Text = title;
        ResultsMetaText.Text = $"{results.Count} result(s)";
        ResultsList.ItemsSource = results;
        StatusText.Text = $"{results.Count} items";

        StartThumbnailQueue(results);
    }

    private void LoadSettingsFolders()
    {
        var settings = _settingsService.Load();

        SettingsFoldersList.Items.Clear();

        foreach (var folder in settings.WatchedFolders.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            SettingsFoldersList.Items.Add(folder);
        }

        SettingsImportedText.Text = _repository.Count().ToString("N0");
        SettingsOcrReadyText.Text = _repository.CountOcrReady().ToString("N0");
        SettingsAiTaggedText.Text = _repository.CountAiTagged().ToString("N0");
        SettingsAiPendingText.Text = _repository.CountPendingAi().ToString("N0");
        SettingsAiModeText.Text = _aiSemanticService.AvailabilityState;
        SettingsFolderCountText.Text = SettingsFoldersList.Items.Count.ToString("N0");

        UpdateSettingsFolderListState();
    }

    private void SaveSettingsFolders()
    {
        var settings = new AppSettingsData
        {
            WatchedFolders = SettingsFoldersList.Items
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };

        _settingsService.Save(settings);
        _fileWatcherService.Restart();
        SettingsFolderCountText.Text = settings.WatchedFolders.Count.ToString("N0");
    }

    private void AddSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose screenshot folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        var selectedPath = dialog.SelectedPath;
        var exists = SettingsFoldersList.Items
            .Cast<string>()
            .Any(folder => string.Equals(folder, selectedPath, StringComparison.OrdinalIgnoreCase));

        if (exists)
        {
            return;
        }

        SettingsFoldersList.Items.Add(selectedPath);
        SaveSettingsFolders();
        UpdateSettingsFolderListState();
        StatusText.Text = "Folder added";
    }

    private void RemoveSettingsFolderItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button || button.Tag is not string folderPath)
        {
            return;
        }

        SettingsFoldersList.Items.Remove(folderPath);
        SaveSettingsFolders();
        UpdateSettingsFolderListState();
        StatusText.Text = "Folder removed";
    }

    private void RunAiBackfill_Click(object sender, RoutedEventArgs e)
    {
        _repository.ResetPendingAiForCompletedOcr();
        SettingsAiPendingText.Text = _repository.CountPendingAi().ToString("N0");
        QueueAiBackfill(limit: 5000);
        StatusText.Text = "AI backfill queued";
    }

    private void UpdateSettingsFolderListState()
    {
        var hasFolders = SettingsFoldersList.Items.Count > 0;

        SettingsFoldersList.Visibility = hasFolders
            ? Visibility.Visible
            : Visibility.Collapsed;

        SettingsEmptyState.Visibility = hasFolders
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void StartThumbnailQueue(List<ScreenshotRecord> records)
    {
        var pending = records
            .Where(r => string.IsNullOrWhiteSpace(r.ThumbnailPath) || !File.Exists(r.ThumbnailPath))
            .ToList();
        if (pending.Count == 0)
        {
            return;
        }

        _backgroundProcessingManager.EnqueueThumbnails(pending, JobPriority.High);
    }

    private void StopThumbnailQueue() { }

    private void StartOcrQueueForRecords(List<ScreenshotRecord> records)
    {
        if (records.Count == 0) return;
        _backgroundProcessingManager.EnqueueOcr(records, JobPriority.High);
        StartOrRefreshOcrTracking();
    }

    private void QueueOcrBackfill(int limit = OcrBackfillBatchSize)
    {
        var pending = _repository.GetPendingOcr(limit);
        if (pending.Count == 0)
        {
            return;
        }

        _backgroundProcessingManager.EnqueueOcr(pending, JobPriority.Low);
        StartOrRefreshOcrTracking();
    }

    private void ThumbnailQueueService_ProgressChanged(ThumbnailQueueProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            if (_ocrTrackingActive)
            {
                return;
            }

            if (progress.Total > 0)
                StatusText.Text = $"Thumbnails {progress.Processed}/{progress.Total}";
        });
    }

    private void FileWatcherService_ScreenshotIndexed(object? sender, ScreenshotIndexedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            _quickSearchOverlay.InvalidateSearchCache();
            ImportedCountText.Text = _repository.Count().ToString("N0");
            StartOcrQueueForRecords([e.Record]);

            if (CurrentViewMode == ViewMode.Home)
            {
                var recent = _repository.GetRecent(HomeRecentLimit);
                BindRecentBuckets(recent);
                StartThumbnailQueue(recent);
                StatusText.Text = "New screenshot indexed";
                return;
            }

            if (CurrentViewMode == ViewMode.Search)
            {
                if (!string.IsNullOrWhiteSpace(CurrentSearchQuery) &&
                    e.Record.FileName.Contains(CurrentSearchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    var refreshed = _repository.SearchHybrid(CurrentSearchQuery, SearchLimit);
                    ShowResults($"Results for '{CurrentSearchQuery}'", refreshed);
                    ResultsMetaText.Text = $"{refreshed.Count} results for '{CurrentSearchQuery}'";
                }
                return;
            }

            if (CurrentViewMode == ViewMode.Collection && MatchesCurrentCollection(e.Record.FileName))
            {
                var keywords = GetCollectionKeywords(CurrentCollection);
                var refreshed = _repository.SearchByKeywords(keywords, CollectionLimit);
                ShowResults(CurrentCollection, refreshed);
                return;
            }

            if (CurrentViewMode == ViewMode.Collection &&
                string.Equals(CurrentCollection, "Recent", StringComparison.OrdinalIgnoreCase))
            {
                ShowResults("Recent", _repository.GetRecent(RecentPageLimit));
                return;
            }
        });
    }

    private void OcrQueueService_ProgressChanged(OcrQueueProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            StartOrRefreshOcrTracking();
            UpdateOcrTrackingStatus();
            OcrReadyCountText.Text = _repository.CountOcrReady().ToString("N0");

            if (progress.Processed >= progress.Total)
            {
                QueueOcrBackfill();
                QueueAiBackfill();
            }
        });
    }

    private void AiMetadataQueueService_ProgressChanged(AiMetadataQueueProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            AiTaggedCountText.Text = _repository.CountAiTagged().ToString("N0");
            if (CurrentViewMode == ViewMode.Settings)
            {
                SettingsAiTaggedText.Text = _repository.CountAiTagged().ToString("N0");
                SettingsAiPendingText.Text = _repository.CountPendingAi().ToString("N0");
            }

            if (!_ocrTrackingActive && progress.Total > 0)
            {
                StatusText.Text = $"AI tagging {progress.Processed}/{progress.Total}";
            }

            if (progress.Processed >= progress.Total && !_ocrTrackingActive)
            {
                StatusText.Text = "Ready";
            }
        });
    }

    private void QueueAiBackfill(int limit = 500)
    {
        if (_repository.CountPendingAi() == 0)
        {
            return;
        }

        if (_aiBackfillQueued)
        {
            return;
        }

        _aiBackfillQueued = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _aiMetadataQueueService.ProcessPendingAsync(limit);
            }
            finally
            {
                _aiBackfillQueued = false;
            }
        });
    }

    private void OcrEtaTimer_Tick(object? sender, EventArgs e)
    {
        UpdateOcrTrackingStatus();
    }

    private void StartOrRefreshOcrTracking()
    {
        var pendingNow = _repository.CountPendingOcr();
        if (pendingNow <= 0)
        {
            StopOcrTracking("Ready");
            return;
        }

        if (!_ocrTrackingActive)
        {
            _ocrTrackingActive = true;
            _ocrRunTotal = pendingNow;
            _ocrRunStopwatch.Restart();
            _ocrEtaTimer.Start();
        }
        else if (pendingNow > _ocrRunTotal)
        {
            _ocrRunTotal = pendingNow;
        }
    }

    private void StopOcrTracking(string statusText)
    {
        _ocrTrackingActive = false;
        _ocrEtaTimer.Stop();
        _ocrRunStopwatch.Reset();
        _ocrRunTotal = 0;
        StatusText.Text = statusText;
    }

    private void UpdateOcrTrackingStatus()
    {
        if (!_ocrTrackingActive)
        {
            return;
        }

        var pendingNow = _repository.CountPendingOcr();
        if (pendingNow <= 0)
        {
            OcrReadyCountText.Text = _repository.CountOcrReady().ToString("N0");
            var failedCount = _repository.CountFailedOcr();
            StopOcrTracking(failedCount > 0
                ? $"OCR complete · failed {failedCount}"
                : "OCR complete");
            return;
        }

        if (pendingNow > _ocrRunTotal)
        {
            _ocrRunTotal = pendingNow;
        }

        var completed = Math.Max(0, _ocrRunTotal - pendingNow);
        var elapsed = _ocrRunStopwatch.Elapsed;
        var etaText = "--:--";

        if (completed > 0 && elapsed.TotalSeconds > 0)
        {
            var ratePerSecond = completed / elapsed.TotalSeconds;
            if (ratePerSecond > 0.01)
            {
                var eta = TimeSpan.FromSeconds(pendingNow / ratePerSecond);
                etaText = eta.TotalHours >= 1
                    ? eta.ToString(@"hh\:mm\:ss")
                    : eta.ToString(@"mm\:ss");
            }
        }

        var failed = _repository.CountFailedOcr();
        StatusText.Text = failed > 0
            ? $"OCR {completed}/{_ocrRunTotal} · {pendingNow} left · failed {failed} · ETA {etaText}"
            : $"OCR {completed}/{_ocrRunTotal} · {pendingNow} left · ETA {etaText}";
    }

    private bool MatchesCurrentCollection(string fileName)
    {
        var keywords = GetCollectionKeywords(CurrentCollection);
        return keywords.Any(k => fileName.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> GetCollectionKeywords(string collection) => collection switch
    {
        "Invoices"      => ["invoice", "receipt", "bill", "payment"],
        "Errors"        => ["error", "bug", "exception", "crash"],
        "Code Snippets" => ["code", "script", "snippet", "terminal"],
        "Conversations" => ["chat", "whatsapp", "discord", "slack"],
        _               => []
    };

    private void ChildList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        var eventArg = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        };
        RaiseEvent(eventArg);
    }

    private void ScreenshotCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not ScreenshotRecord record)
            return;

        SelectScreenshot(record);
    }

    private void OpenImageFromOverlay(ScreenshotRecord record)
    {
        if (!File.Exists(record.FilePath))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(record.FilePath)
        {
            UseShellExecute = true
        });
    }

    private void RevealInExplorerFromOverlay(ScreenshotRecord record)
    {
        if (!File.Exists(record.FilePath))
        {
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe",
            $"/select,\"{record.FilePath}\"")
        {
            UseShellExecute = true
        });
    }

    // -- Show preview panel and load content ------------------------------
    private void SelectScreenshot(ScreenshotRecord record)
    {
        // Deselect previous card.
        if (_selectedScreenshot is not null)
            _selectedScreenshot.IsSelected = false;

        _selectedScreenshot = record;
        _selectedScreenshot.IsSelected = true;

        // Open preview column.
        PreviewColumn.Width = new GridLength(PreviewPanelWidth);
        PreviewPanel.Visibility = Visibility.Visible;

        UpdatePreviewMetadata(record);
        _ = LoadPreviewImageAsync(record);
    }

    private void UpdatePreviewMetadata(ScreenshotRecord record)
    {
        PreviewFileNameText.Text    = record.FileName;
        PreviewFilePathText.Text    = record.FilePath;
        PreviewCreatedAtText.Text   = record.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd  HH:mm");
        PreviewFileSizeText.Text    = FormatFileSize(record.FileSizeBytes);
        PreviewOcrStatusText.Text   = string.IsNullOrWhiteSpace(record.OcrStatus) ? "pending" : record.OcrStatus;
        PreviewAiStatusText.Text    = string.IsNullOrWhiteSpace(record.AiStatus) ? "pending" : record.AiStatus;
        PreviewAiCategoryText.Text  = string.IsNullOrWhiteSpace(record.AiCategory) ? "unknown" : record.AiCategory;
        PreviewAiTagsText.Text      = string.IsNullOrWhiteSpace(record.AiTags) ? "No tags yet" : record.AiTags;
        PreviewApplicationText.Text = string.IsNullOrWhiteSpace(record.ApplicationName) ? "Unknown" : record.ApplicationName;
        PreviewWindowText.Text      = string.IsNullOrWhiteSpace(record.ActiveWindow) ? "Unavailable" : record.ActiveWindow;
        PreviewAiSummaryText.Text   = string.IsNullOrWhiteSpace(record.AiSummary) ? "No summary yet" : record.AiSummary;

        // Show placeholder text while image loads.
        PreviewPlaceholderText.Text       = "Loading…";
        PreviewPlaceholderText.Visibility = Visibility.Visible;
        PreviewImage.Source               = null;

        var fileExists = File.Exists(record.FilePath);
        PreviewOpenImageButton.IsEnabled  = fileExists;
        PreviewRevealButton.IsEnabled     = fileExists;
    }

    private async Task LoadPreviewImageAsync(ScreenshotRecord record)
    {
        _previewLoadCts?.Cancel();
        _previewLoadCts?.Dispose();
        _previewLoadCts = new CancellationTokenSource();
        var token = _previewLoadCts.Token;

        // Show thumbnail immediately for snappy feel.
        if (!string.IsNullOrWhiteSpace(record.ThumbnailPath) && File.Exists(record.ThumbnailPath))
        {
            try
            {
                var thumb = await Task.Run(() => CreateBitmap(record.ThumbnailPath, 160), token);
                PreviewImage.Source               = thumb;
                PreviewPlaceholderText.Visibility = Visibility.Collapsed;
            }
            catch { /* fall through to full load */ }
        }

        if (!File.Exists(record.FilePath)) return;

        // Serve from cache if available.
        if (_previewCache.TryGetValue(record.FilePath, out var cached))
        {
            PreviewImage.Source               = cached;
            PreviewPlaceholderText.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            var full = await Task.Run(() => CreateBitmap(record.FilePath, 900), token);
            token.ThrowIfCancellationRequested();

            if (_selectedScreenshot?.Id != record.Id) return;

            PreviewImage.Source               = full;
            PreviewPlaceholderText.Visibility = Visibility.Collapsed;
            AddToPreviewCache(record.FilePath, full);
        }
        catch (OperationCanceledException) { /* new selection cancelled this */ }
        catch
        {
            if (_selectedScreenshot?.Id == record.Id)
            {
                PreviewImage.Source               = null;
                PreviewPlaceholderText.Text       = "Preview unavailable";
                PreviewPlaceholderText.Visibility = Visibility.Visible;
            }
        }
    }

    private static BitmapImage CreateBitmap(string path, int decodePixelWidth)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption    = BitmapCacheOption.OnLoad;
        image.CreateOptions  = BitmapCreateOptions.IgnoreImageCache;
        image.DecodePixelWidth = decodePixelWidth;
        image.UriSource      = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void AddToPreviewCache(string path, BitmapSource image)
    {
        if (_previewCache.ContainsKey(path))
        {
            _previewCache[path] = image;
            _previewCacheOrder.Remove(path);
            _previewCacheOrder.AddFirst(path);
            return;
        }

        _previewCache[path] = image;
        _previewCacheOrder.AddFirst(path);

        while (_previewCacheOrder.Count > PreviewCacheLimit)
        {
            var last = _previewCacheOrder.Last;
            if (last is null) break;
            _previewCache.Remove(last.Value);
            _previewCacheOrder.RemoveLast();
        }
    }

    // -- Preview actions ---------------------------------------------------
    private void PreviewOpenImage_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreenshot is null || !File.Exists(_selectedScreenshot.FilePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_selectedScreenshot.FilePath)
        {
            UseShellExecute = true
        });
    }

    private void PreviewReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreenshot is null || !File.Exists(_selectedScreenshot.FilePath)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "explorer.exe", $"/select,\"{_selectedScreenshot.FilePath}\"")
        {
            UseShellExecute = true
        });
    }

    private void PreviewCopyPath_Click(object sender, RoutedEventArgs e)
        => System.Windows.Clipboard.SetText(_selectedScreenshot?.FilePath ?? string.Empty);

    private void PreviewClose_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedScreenshot is not null)
            _selectedScreenshot.IsSelected = false;

        _selectedScreenshot = null;
        _previewLoadCts?.Cancel();

        // Collapse the preview column — no empty space remains.
        PreviewColumn.Width       = new GridLength(0);
        PreviewPanel.Visibility   = Visibility.Collapsed;

        PreviewImage.Source               = null;
        PreviewPlaceholderText.Text       = "Loading…";
        PreviewPlaceholderText.Visibility = Visibility.Visible;
        PreviewFileNameText.Text          = string.Empty;
        PreviewFilePathText.Text          = string.Empty;
        PreviewCreatedAtText.Text         = string.Empty;
        PreviewFileSizeText.Text          = string.Empty;
        PreviewOcrStatusText.Text         = string.Empty;
        PreviewAiStatusText.Text          = string.Empty;
        PreviewAiCategoryText.Text        = string.Empty;
        PreviewAiTagsText.Text            = string.Empty;
        PreviewApplicationText.Text       = string.Empty;
        PreviewWindowText.Text            = string.Empty;
        PreviewAiSummaryText.Text         = string.Empty;
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

    private void BindRecentBuckets(List<ScreenshotRecord> recent)
    {
        var now = DateTime.Now;
        var today = now.Date;
        var startOfWeek = today.AddDays(-(int)((7 + (today.DayOfWeek - DayOfWeek.Monday)) % 7));

        var todayItems = new List<ScreenshotRecord>();
        var yesterdayItems = new List<ScreenshotRecord>();
        var weekItems = new List<ScreenshotRecord>();
        var olderItems = new List<ScreenshotRecord>();

        foreach (var item in recent.OrderByDescending(ScreenshotRepository.GetBestTimestamp))
        {
            var ts = ScreenshotRepository.GetBestTimestamp(item).ToLocalTime();
            if (ts.Date == today)
            {
                todayItems.Add(item);
            }
            else if (ts.Date == today.AddDays(-1))
            {
                yesterdayItems.Add(item);
            }
            else if (ts.Date >= startOfWeek)
            {
                weekItems.Add(item);
            }
            else
            {
                olderItems.Add(item);
            }
        }

        RecentTodayList.ItemsSource = todayItems;
        RecentYesterdayList.ItemsSource = yesterdayItems;
        RecentWeekList.ItemsSource = weekItems;
        RecentOlderList.ItemsSource = olderItems;

        RecentTodaySection.Visibility = todayItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentYesterdaySection.Visibility = yesterdayItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentWeekSection.Visibility = weekItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentOlderSection.Visibility = olderItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            if (_quickSearchOverlay.Owner is null)
            {
                _quickSearchOverlay.Owner = this;
            }
            _quickSearchOverlay.SetSearchMode(_searchMode, notifyOwner: false);
            _quickSearchOverlay.Open();
            handled = true;
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public void EnsureHotkeyRegistration()
    {
        var helper = new WindowInteropHelper(this);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        if (!_wndProcAttached)
        {
            var source = HwndSource.FromHwnd(handle);
            source?.AddHook(WndProc);
            _wndProcAttached = true;
        }

        if (!_hotkeyRegistered)
        {
            _hotkeyRegistered = RegisterHotKey(handle, HotkeyId, ModControl | ModShift, VkSpace);
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreDashboard()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Maximized;
        Activate();
    }

    private void ExitApp()
    {
        _allowClose = true;
        DisposeTrayIcon();
        System.Windows.Application.Current.Shutdown();
    }

    private void DisposeTrayIcon()
    {
        if (_trayDisposed)
        {
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayDisposed = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void WindowMinimize_Click(object sender, RoutedEventArgs e) => HideToTray();
    private void WindowMaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }
    private void WindowClose_Click(object sender, RoutedEventArgs e) => HideToTray();
}



