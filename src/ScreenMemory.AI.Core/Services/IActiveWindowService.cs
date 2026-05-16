namespace ScreenMemory.AI.Core.Services;

public sealed class WindowContext
{
    public DateTime CapturedAt { get; set; } = DateTime.UtcNow;

    public string WindowTitle { get; set; } = string.Empty;

    public string ProcessName { get; set; } = string.Empty;

    public string MainWindowTitle { get; set; } = string.Empty;

    public string ApplicationName { get; set; } = string.Empty;

    public bool HasError { get; set; }

    public string? ErrorMessage { get; set; }

    public string ToSearchableText() => $"{ApplicationName} {WindowTitle} {ProcessName}".Trim();
}

public interface IActiveWindowService
{
    WindowContext Capture();
}
