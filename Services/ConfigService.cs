using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public class ConfigService
{
    private static readonly object LockObj = new();
    private readonly string _configFilePath;
    private AppConfig _currentConfig;

    public AppConfig Current => _currentConfig;
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VDAKor",
        "ServerAgent");

    public ConfigService(string? customPath = null)
    {
        _configFilePath = customPath ?? DetermineConfigPath();
        _currentConfig = Load();
    }

    private static string DetermineConfigPath()
    {
        EnsureProtectedInstallDirectory();
        var securePath = Path.Combine(InstallDirectory, "config.json");
        if (File.Exists(securePath)) return securePath;

        var candidates = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "VdaHubServerController",
                "config.json")
        };

        foreach (var source in candidates)
        {
            if (!File.Exists(source) ||
                Path.GetFullPath(source).Equals(Path.GetFullPath(securePath), StringComparison.OrdinalIgnoreCase))
                continue;
            File.Copy(source, securePath, overwrite: false);
            break;
        }

        return securePath;
    }

    public static void EnsureProtectedInstallDirectory()
    {
        Directory.CreateDirectory(InstallDirectory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        foreach (var sidValue in new[] { "S-1-5-18", "S-1-5-32-544" })
        {
            var sid = new SecurityIdentifier(sidValue);
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        new DirectoryInfo(InstallDirectory).SetAccessControl(security);
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
            if (config != null) _currentConfig = config;

            EnsureProtectedInstallDirectory();
            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(_currentConfig, options);
            var temporary = _configFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json);
                File.Move(temporary, _configFilePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
