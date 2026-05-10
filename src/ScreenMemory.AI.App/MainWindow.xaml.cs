using System.Windows;
using ScreenMemory.AI.App.Views;

namespace ScreenMemory.AI.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var settingsWindow = new SettingsWindow
        {
            Owner = this
        };

        settingsWindow.ShowDialog();
    }
}