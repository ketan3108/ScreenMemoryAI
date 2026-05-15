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

        // Always show the dashboard when the app starts so command-line runs
        // have visible feedback instead of appearing to "do nothing".
        mainWindow.Show();
    }
}
