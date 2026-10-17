namespace WaveSync;

/// <summary>Tiny append-only log in %APPDATA%\WaveSync\log.txt (rotated at ~1 MB).</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppConfig.Folder, "log.txt");

    public static void Info(string message) => Write("INFO ", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Folder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > 1_000_000)
                {
                    File.Copy(FilePath, FilePath + ".old", overwrite: true);
                    File.Delete(FilePath);
                }

                File.AppendAllText(FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
