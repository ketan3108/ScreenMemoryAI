using System.Windows;
using System.Windows.Input;
using ScreenMemory.AI.Core.Data;
using ScreenMemory.AI.Core.Models;

namespace ScreenMemory.AI.App.Views;

public partial class QuickSearchOverlay : Window
{
    private readonly ScreenshotRepository _repository;
    private CancellationTokenSource? _searchCts;
    private readonly Action<ScreenshotRecord> _openRecord;
    private readonly Action<ScreenshotRecord> _revealRecord;
    private const double BaseHeight = 170;
    private const double RowHeight = 76;
    private const double MinOverlayHeight = 220;
    private const double MaxOverlayHeight = 640;

    public QuickSearchOverlay(
        ScreenshotRepository repository,
        Action<ScreenshotRecord> openRecord,
        Action<ScreenshotRecord> revealRecord)
    {
        InitializeComponent();
        _repository = repository;
        _openRecord = openRecord;
        _revealRecord = revealRecord;
    }

    public void Open()
    {
        var workArea = SystemParameters.WorkArea;
        MaxHeight = Math.Max(MinOverlayHeight, workArea.Height - 40);
        Height = Math.Min(Height, MaxHeight);
        Left = workArea.Left + ((workArea.Width - Width) / 2);
        Top = workArea.Top + 20;

        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
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

        List<ScreenshotRecord> results;
        try
        {
            results = string.IsNullOrWhiteSpace(query)
                ? _repository.GetRecent(10)
                : await Task.Run(() => _repository.SearchHybrid(query, 10), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested)
        {
            return;
        }

        ResultsList.ItemsSource = results;
        if (results.Count > 0)
        {
            ResultsList.SelectedIndex = 0;
        }

        UpdateOverlaySize(results.Count);
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (ResultsList.Items.Count == 0) return;
            ResultsList.SelectedIndex = Math.Min(ResultsList.SelectedIndex + 1, ResultsList.Items.Count - 1);
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Up)
        {
            if (ResultsList.Items.Count == 0) return;
            ResultsList.SelectedIndex = Math.Max(ResultsList.SelectedIndex - 1, 0);
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            e.Handled = true;
        }
    }

    private void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void OpenSelected()
    {
        if (ResultsList.SelectedItem is ScreenshotRecord record)
        {
            _openRecord(record);
            Hide();
        }
    }

    private void UpdateOverlaySize(int resultCount)
    {
        var target = BaseHeight + (Math.Min(resultCount, 10) * RowHeight);
        var maxAllowed = Math.Min(MaxOverlayHeight, MaxHeight);
        Height = Math.Max(MinOverlayHeight, Math.Min(maxAllowed, target));
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ScreenshotRecord record)
        {
            _openRecord(record);
            Hide();
        }
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ScreenshotRecord record)
        {
            _revealRecord(record);
            Hide();
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }
}
