using System.Management;
using VdaHubServerController.Models;

namespace VdaHubServerController.Services;

public static class WatchedServiceMonitor
{
    public static List<WatchedServiceSnapshot> Collect(AgentControlSettings settings)
    {
        var now = DateTime.UtcNow;
        var names = settings.IsValid() ? settings.ServiceNames : [];
        if (names.Length == 0) return [];
        var result = names.ToDictionary(name => name, name => new WatchedServiceSnapshot { Name = name, CheckedAtUtc = now }, StringComparer.OrdinalIgnoreCase);
        try
        {
            // No paths, arguments or service-control operations; filter exact names locally.
            using var query = new ManagementObjectSearcher(new ManagementScope("root\\CIMV2"),
                new ObjectQuery("SELECT Name, DisplayName, State, StartMode FROM Win32_Service"),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) });
            using var services = query.Get();
            foreach (ManagementObject service in services)
            {
                using (service)
                {
                    var name = service["Name"]?.ToString() ?? "";
                    if (!result.TryGetValue(name, out var item)) continue;
                    var displayName = service["DisplayName"]?.ToString() ?? name;
                    result[name] = item with
                    {
                        Status = "success",
                        Installed = true,
                        DisplayName = displayName[..Math.Min(displayName.Length, 120)],
                        State = service["State"]?.ToString() switch
                        {
                            "Running" => "running",
                            "Stopped" => "stopped",
                            "Paused" => "paused",
                            "Start Pending" or "Continue Pending" => "starting",
                            "Stop Pending" => "stopping",
                            _ => "unknown"
                        },
                        StartMode = service["StartMode"]?.ToString() switch
                        {
                            "Auto" => "automatic",
                            "Manual" => "manual",
                            "Disabled" => "disabled",
                            _ => "unknown"
                        }
                    };
                }
            }
            foreach (var name in names.Where(name => result[name].Installed is null))
                result[name] = result[name] with { Status = "success", Installed = false };
        }
        catch (Exception error) when (error is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or
            InvalidOperationException or System.Security.SecurityException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            // A partial/failed enumeration cannot prove that a service is missing.
            return names.Select(name => new WatchedServiceSnapshot { Name = name, CheckedAtUtc = now }).ToList();
        }
        return names.Select(name => result[name]).ToList();
    }
}
