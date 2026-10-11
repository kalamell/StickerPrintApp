namespace StickerPrintApp;

/// <summary>Appends log lines to %AppData%\StickerPrintApp\logs\yyyy-MM-dd.log.</summary>
internal static class AppLog
{
    private static readonly object Gate = new();

    public static string Format(string message) => $"[{DateTime.Now:HH:mm:ss}] {message}";

    public static void Write(string line)
    {
        try
        {
            var dir = Path.Combine(AppSettings.Folder, "logs");
            lock (Gate)
            {
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break printing.
        }
    }
}
