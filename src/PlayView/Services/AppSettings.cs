using System.IO;
using System.Text.Json;

namespace PlayView.Services;

public class OttServiceConfig
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public class AppSettings
{
    public string ScrcpyPath { get; set; } = "";
    public bool ShowSidebar { get; set; } = true;
    public List<OttServiceConfig> Services { get; set; } = OttCatalog.DefaultServices();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PlayView");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static string DataDir => Dir;
    public static string ProfilesRoot => Path.Combine(Dir, "Profiles");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (loaded != null) return loaded;
            }
        }
        catch
        {
            // Corrupt settings — fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
