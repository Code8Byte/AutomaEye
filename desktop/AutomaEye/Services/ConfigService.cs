using System;
using System.IO;
using System.Text.Json;
using AutomaEye.Models;

namespace AutomaEye.Services;

/// <summary>Loads/saves the single global config.json. Secrets (API keys) live here, never inside a project.json that might get pushed to a shared GitHub repo.</summary>
public static class ConfigService
{
    private static readonly string Path_ = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AutomaEye", "config.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private static AppConfig? _current;

    public static AppConfig Current => _current ??= Load();

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(Path_))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Path_)) ?? new AppConfig();
        }
        catch { /* fall through to defaults - a corrupt config file shouldn't block startup */ }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        _current = config;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        File.WriteAllText(Path_, JsonSerializer.Serialize(config, Opts));
    }
}
