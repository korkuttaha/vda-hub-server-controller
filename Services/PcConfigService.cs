using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public sealed class PcConfigService
{
    private static readonly object Gate = new();
    private readonly string _path;
    private PcAgentConfig _current;

    public PcAgentConfig Current => _current;
    public string ConfigFilePath => _path;

    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "VDAKor",
        "PcAgent");

    public PcConfigService(string? customPath = null)
    {
        _path = customPath ?? Path.Combine(EnsureProtectedInstallDirectory(), "config.json");
        _current = Load();
    }

    public static string EnsureProtectedInstallDirectory()
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
        return InstallDirectory;
    }

    public PcAgentConfig Load()
    {
        lock (Gate)
        {
            if (File.Exists(_path))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<PcAgentConfig>(
                        File.ReadAllText(_path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (loaded is not null)
                    {
                        _current = loaded;
                        return _current;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[PcConfig] Config okunamadı: {ex.Message}");
                }
            }

            _current = new PcAgentConfig();
            return _current;
        }
    }

    public void Save()
    {
        lock (Gate)
        {
            EnsureProtectedInstallDirectory();
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(_current, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, _path, true);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }

    public void Reset()
    {
        lock (Gate)
        {
            _current = new PcAgentConfig();
            try
            {
                if (File.Exists(_path)) File.Delete(_path);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[PcConfig] Config silinemedi: {ex.Message}");
            }
        }
    }
}
