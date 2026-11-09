namespace Sluice.App.ViewModels;

internal static class Format
{
    public static string Bytes(long b) => b switch
    {
        < 1024 => $"{b} B",
        < 1024 * 1024 => $"{b / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{b / 1048576.0:F1} MB",
        _ => $"{b / 1073741824.0:F2} GB",
    };

    public static string Rate(double bytesPerSecond) => bytesPerSecond < 1 ? "0 B/s" : Bytes((long)bytesPerSecond) + "/s";

    public static string Duration(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}h {t.Minutes:D2}m"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:D2}s" : $"{t.TotalSeconds:F0}s";
}
