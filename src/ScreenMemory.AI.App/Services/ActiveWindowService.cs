using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScreenMemory.AI.Core.Services;

namespace ScreenMemory.AI.App.Services;

public sealed class ActiveWindowService : IActiveWindowService
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    public WindowContext Capture()
    {
        var context = new WindowContext
        {
            CapturedAt = DateTime.UtcNow
        };

        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero)
            {
                context.ApplicationName = "Unknown";
                return context;
            }

            var titleBuilder = new StringBuilder(512);
            var titleLength = GetWindowText(handle, titleBuilder, titleBuilder.Capacity);
            context.WindowTitle = titleLength > 0 ? titleBuilder.ToString() : string.Empty;

            if (GetWindowThreadProcessId(handle, out var processId) == 0 || processId == 0)
            {
                context.ApplicationName = CleanTitle(context.WindowTitle);
                return context;
            }

            try
            {
                using var process = Process.GetProcessById((int)processId);
                context.ProcessName = process.ProcessName;
                context.MainWindowTitle = process.MainWindowTitle;
                context.ApplicationName = ExtractAppName(context.WindowTitle, context.ProcessName);
            }
            catch
            {
                context.ProcessName = "System";
                context.ApplicationName = CleanTitle(context.WindowTitle);
            }
        }
        catch (Exception ex)
        {
            context.HasError = true;
            context.ErrorMessage = ex.Message;
        }

        return context;
    }

    private static string ExtractAppName(string title, string processName)
    {
        var knownApps = new (string Process, string Name)[]
        {
            ("Code", "VS Code"),
            ("devenv", "Visual Studio"),
            ("chrome", "Chrome"),
            ("msedge", "Edge"),
            ("firefox", "Firefox"),
            ("notepad++", "Notepad++"),
            ("winword", "Word"),
            ("excel", "Excel"),
            ("powerpnt", "PowerPoint"),
            ("outlook", "Outlook"),
            ("teams", "Teams"),
            ("slack", "Slack"),
            ("discord", "Discord"),
            ("zoom", "Zoom"),
            ("cmd", "Command Prompt"),
            ("WindowsTerminal", "Terminal"),
            ("powershell", "PowerShell"),
            ("ssms", "SQL Server Studio")
        };

        foreach (var (process, name) in knownApps)
        {
            if (processName.Contains(process, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        var cleanedTitle = CleanTitle(title);
        if (cleanedTitle.Length is >= 2 and <= 50)
        {
            return cleanedTitle;
        }

        return string.IsNullOrWhiteSpace(processName)
            ? "Unknown"
            : char.ToUpperInvariant(processName[0]) + processName[1..];
    }

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "Unknown";
        }

        var result = title.Trim();
        var suffixes = new[]
        {
            " - Google Chrome",
            " - Microsoft Edge",
            " - Mozilla Firefox",
            " - Visual Studio",
            " (Administrator)",
            " [Running]"
        };

        foreach (var suffix in suffixes)
        {
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length].Trim();
            }
        }

        var dashIndex = Math.Max(result.IndexOf(" - ", StringComparison.Ordinal), result.IndexOf(" – ", StringComparison.Ordinal));
        return dashIndex > 0 ? result[..dashIndex].Trim() : result;
    }
}
