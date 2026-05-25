namespace FluxoPrimeCore;

public static class AppLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluxoPrimeMaui", "logs");

    public static void Info(string message) => Write("INFO", message);
    public static void Error(Exception ex, string context) => Write("ERROR", $"{context}: {ex.Message}");
    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogPath);
            var logFile = Path.Combine(LogPath, $"app_{DateTime.Now:yyyy-MM-dd}.log");
            var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] {message}";
            File.AppendAllText(logFile, line + Environment.NewLine);
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch { }
    }
}