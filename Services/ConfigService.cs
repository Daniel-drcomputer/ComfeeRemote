using System.IO;
using System.Text.Json;
using ComfeeRemote.Models;

namespace ComfeeRemote.Services;

public static class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true
    };

    public static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var text = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<AppConfig>(text, Options);
                if (config is not null)
                    return config;
            }
        }
        catch
        {
            // Bei kaputter config.json einfach Defaults verwenden.
        }

        var defaults = new AppConfig();
        Save(defaults);
        return defaults;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
        }
        catch
        {
            // Die App kann trotzdem mit den Defaultwerten laufen.
        }
    }
}
