using System.Windows;
using System.Windows.Input;
using System.Windows.Forms;
using ScreenMemory.AI.App.Services;

namespace ScreenMemory.AI.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettingsService _settingsService = new();

    public SettingsWindow()
    {
        InitializeComponent();
        LoadFolders();
        UpdateFolderListState();
    }

    private void LoadFolders()
    {
        var settings = _settingsService.Load();

        FoldersList.Items.Clear();

        foreach (var folder in settings.WatchedFolders)
        {
            if (!FoldersList.Items.Contains(folder))
                FoldersList.Items.Add(folder);
        }
    }

    private void SaveFolders()
    {
        var settings = new AppSettingsData
        {
            WatchedFolders = FoldersList.Items
                .Cast<string>()
                .Distinct()
                .ToList()
        };

        _settingsService.Save(settings);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose screenshot folder",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            if (!FoldersList.Items.Contains(dialog.SelectedPath))
            {
                FoldersList.Items.Add(dialog.SelectedPath);
                SaveFolders();
                UpdateFolderListState();
            }
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem != null)
        {
            FoldersList.Items.Remove(FoldersList.SelectedItem);
            SaveFolders();
            UpdateFolderListState();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        SaveFolders();
        Close();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void UpdateFolderListState()
    {
        var hasFolders = FoldersList.Items.Count > 0;

        FoldersList.Visibility = hasFolders
            ? Visibility.Visible
            : Visibility.Collapsed;

        EmptyState.Visibility = hasFolders
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
