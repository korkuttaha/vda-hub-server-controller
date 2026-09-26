namespace VdaHubServerController.Models;

public class AppConfig
{
    public string ServerId { get; set; } = Guid.NewGuid().ToString();
    public string ServerName { get; set; } = Environment.MachineName;
    public int CheckIntervalMinutes { get; set; } = 5;

    public HubSettings Hub { get; set; } = new();
    public BrevoSettings Brevo { get; set; } = new();
}

public class HubSettings
{
    public bool Enabled { get; set; } = false;
    public string HubApiUrl { get; set; } = "https://your-vda-hub.com/api/v1/servers/report";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 15;
}

public class BrevoSettings
{
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty; // Brevo API v3 key: xkeysib-...
    public string SenderEmail { get; set; } = "alert@yourdomain.com";
    public string SenderName { get; set; } = "VDA Hub Controller";
    public string RecipientEmails { get; set; } = "admin@yourdomain.com"; // Comma-separated
    public string DailyReportTime { get; set; } = "09:00"; // HH:mm in 24-hour format
    public bool SendCriticalAlertImmediately { get; set; } = true;
    public int WarningThresholdPercent { get; set; } = 80;
    public int CriticalThresholdPercent { get; set; } = 90;

    // Internal state to avoid duplicate daily reports
    public string LastDailyReportDate { get; set; } = string.Empty; // Format: "yyyy-MM-dd"
    public DateTime? LastCriticalAlertSentAt { get; set; }
}
