using System.Text.Json;
using System.IO;

namespace AppVolumeBoost;

public sealed class ProfileStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AppVolumeBoost",
        "profiles.json");

    public Dictionary<string, double> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json)
                   ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public void Save(IEnumerable<AudioAppViewModel> apps)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            var values = apps
                .GroupBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().BoostDb, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(_path, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Profiles are a convenience. Audio control should still work if saving is unavailable.
        }
    }
}
