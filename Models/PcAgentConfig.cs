namespace VdaHubServerController.Models;

public sealed class PcAgentConfig
{
    public string ComputerId { get; set; } = string.Empty;
    public string ComputerName { get; set; } = Environment.MachineName;
    public string HubBaseUrl { get; set; } = "https://vddashbrd.runasp.net";
    public string ApiKey { get; set; } = string.Empty;
}
