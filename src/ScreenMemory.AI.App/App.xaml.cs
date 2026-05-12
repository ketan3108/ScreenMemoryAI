using System.Windows;
using System.Windows.Interop;

namespace ScreenMemory.AI.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        var helper = new WindowInteropHelper(mainWindow);
        helper.EnsureHandle();
        mainWindow.EnsureHotkeyRegistration();

        if (mainWindow.Settings.ShowDashboardOnStartup)
        {
            mainWindow.Show();
        }
    }
}
