using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class ConfigService
{
    private static readonly object LockObj = new();
    private readonly string _configFilePath;
    private AppConfig _currentConfig;

    public AppConfig Current => _currentConfig;

    public ConfigService(string? customPath = null)
    {
        _configFilePath = customPath ?? DetermineConfigPath();
        _currentConfig = Load();
    }

    private static string DetermineConfigPath()
    {
        string localPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
        try
        {
            // Test write permissions in local folder
            string testFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".perm_test");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return localPath;
        }
        catch
        {
            // If running in Program Files or protected directory, use ProgramData
            string appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VdaHubServerController"
            );
            Directory.CreateDirectory(appData);
            return Path.Combine(appData, "config.json");
        }
    }

    public string ConfigFilePath => _configFilePath;

    public AppConfig Load()
    {
        lock (LockObj)
        {
            if (File.Exists(_configFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_configFilePath);
                    var config = JsonSerializer.Deserialize<AppConfig>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip
                    });
                    if (config != null)
                    {
                        _currentConfig = config;
                        return _currentConfig;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ConfigService] Config okuma hatası: {ex.Message}. Varsayılan oluşturuluyor.");
                }
            }

            _currentConfig = new AppConfig();
            Save(_currentConfig);
            return _currentConfig;
        }
    }

    public void Save(AppConfig? config = null)
    {
        lock (LockObj)
        {
            if (config != null)
            {
                _currentConfig = config;
            }

            string dir = Path.GetDirectoryName(_configFilePath)!;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            string json = JsonSerializer.Serialize(_currentConfig, options);
            File.WriteAllText(_configFilePath, json);
        }
    }
}
