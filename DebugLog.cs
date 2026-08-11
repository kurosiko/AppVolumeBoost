namespace AppVolumeBoost;

internal static class DebugLog
{
    private static readonly object Sync = new();
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AppVolumeBoost",
        "debug.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                System.IO.File.AppendAllText(Path, $"{DateTime.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
