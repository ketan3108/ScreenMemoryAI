using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;

namespace ScreenMemory.AI.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        RefreshEmptyState();
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
                RefreshEmptyState();
            }
        }
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FoldersList.SelectedItem != null)
        {
            FoldersList.Items.Remove(FoldersList.SelectedItem);
            RefreshEmptyState();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RefreshEmptyState()
    {
        bool hasItems = FoldersList.Items.Count > 0;
        FoldersList.Visibility  = hasItems ? Visibility.Visible   : Visibility.Collapsed;
        EmptyState.Visibility   = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }
}