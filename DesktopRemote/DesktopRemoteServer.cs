using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VdaHubServerController.DesktopRemote;

public sealed record DesktopInputRequest(
    string? Type,
    double? X,
    double? Y,
    int? Dx,
    int? Dy,
    string? Text,
    string? Key,
    string? Button,
    string? Hotkey
);

public sealed record DesktopLaunchRequest(
    string? Command,
    string? Args
);

public sealed class DesktopRemoteServer
{
    private readonly DesktopScreenService _screen;
    private readonly DesktopInputService _input;
    private readonly DesktopAppManagerService _apps;
    private WebApplication? _app;

    public DesktopRemoteServer(
        DesktopScreenService screen,
        DesktopInputService input,
        DesktopAppManagerService apps)
    {
        _screen = screen;
        _input = input;
        _apps = apps;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var hostBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = AppContext.BaseDirectory
        });

        hostBuilder.Logging.ClearProviders();
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Windows desktop owner is unavailable.");
        using var process = Process.GetCurrentProcess();
        var sessionId = checked((uint)process.SessionId);
        var pipeSecurity = DesktopPipe.CreateSecurity(owner);
        hostBuilder.WebHost.UseNamedPipes(options =>
        {
            // The signed-in user and the LocalSystem service are different Windows identities.
            options.CurrentUserOnly = false;
            options.PipeSecurity = pipeSecurity;
        });
        hostBuilder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = 128 * 1024;
            kestrel.ListenNamedPipe(DesktopPipe.Name(sessionId, owner));
        });

        _app = hostBuilder.Build();
        _app.Use(async (context, next) =>
        {
            if (context.Features.Get<IConnectionNamedPipeFeature>() is null)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (DesktopPipe.WTSGetActiveConsoleSessionId() != sessionId ||
                !DesktopStationHelper.IsInteractiveDesktopActive())
            {
                context.Response.StatusCode = StatusCodes.Status423Locked;
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XFrameOptions = "DENY";
            await next();
        });

        // Desktop information and frame capture
        _app.MapGet("/api/desktop/info", () =>
        {
            var (w, h) = _screen.GetScreenMetrics();
            return Results.Ok(new
            {
                machineName = Environment.MachineName,
                userName = Environment.UserName,
                osVersion = Environment.OSVersion.VersionString,
                screen = new { width = w, height = h }
            });
        });

        _app.MapGet("/api/desktop/snapshot.jpg", (int? q, float? s) =>
        {
            int quality = q ?? 75;
            float scale = s ?? 1.0f;
            var bytes = _screen.CaptureJpeg(quality, scale);
            return Results.File(bytes, "image/jpeg");
        });

        // Input and application controls
        _app.MapPost("/api/desktop/input", (DesktopInputRequest req) =>
        {
            if (req == null || string.IsNullOrWhiteSpace(req.Type))
                return Results.BadRequest(new { error = "Geçersiz giriş isteği." });

            switch (req.Type.ToLowerInvariant())
            {
                case "move":
                    if (req.X.HasValue && req.Y.HasValue)
                        _input.MoveNormalized(req.X.Value, req.Y.Value);
                    else if (req.Dx.HasValue && req.Dy.HasValue)
                        _input.MoveRelative(req.Dx.Value, req.Dy.Value);
                    break;

                case "click":
                    _input.Click(req.X, req.Y);
                    break;

                case "double_click":
                    _input.DoubleClick(req.X, req.Y);
                    break;

                case "right_click":
                    _input.RightClick(req.X, req.Y);
                    break;

                case "middle_click":
                    _input.MiddleClick(req.X, req.Y);
                    break;

                case "down":
                    _input.MouseDown(req.Button ?? "left");
                    break;

                case "up":
                    _input.MouseUp(req.Button ?? "left");
                    break;

                case "scroll":
                    _input.Scroll(req.Dy ?? 120);
                    break;

                case "text":
                    if (!string.IsNullOrEmpty(req.Text))
                        _input.TypeText(req.Text);
                    break;

                case "key":
                    if (!string.IsNullOrEmpty(req.Key))
                        _input.SendKey(req.Key);
                    break;

                case "hotkey":
                    if (!string.IsNullOrEmpty(req.Hotkey))
                        _input.SendHotkey(req.Hotkey);
                    break;

                default:
                    return Results.BadRequest(new { error = $"Desteklenmeyen giriş tipi: {req.Type}" });
            }

            return Results.Ok(new { success = true });
        });

        _app.MapGet("/api/desktop/apps", () =>
        {
            var apps = _apps.GetRunningApplications();
            return Results.Ok(new { count = apps.Count, apps });
        });

        _app.MapPost("/api/desktop/apps/{pid:int}/focus", (int pid) =>
        {
            bool ok = _apps.FocusApplication(pid);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "Pencere bulunamadı veya odaklanamadı." });
        });

        _app.MapPost("/api/desktop/apps/{pid:int}/close", (int pid) =>
        {
            bool ok = _apps.CloseApplication(pid);
            return ok ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = "Uygulama sonlandırılamadı." });
        });

        _app.MapPost("/api/desktop/launch", (DesktopLaunchRequest req) =>
        {
            if (req.Args is not null || DesktopAppManagerService.QuickAppTarget(req.Command) is null)
                return Results.BadRequest(new { error = "Geçersiz program kısayolu." });

            bool ok = _apps.LaunchQuickApp(req.Command);
            return ok ? Results.Ok(new { success = true, command = req.Command }) : Results.BadRequest(new { error = "Program başlatılamadı." });
        });

        // Clipboard sync and direct paste
        _app.MapPost("/api/desktop/clipboard", async (HttpContext context) =>
        {
            try
            {
                using var reader = new StreamReader(context.Request.Body);
                string body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("text", out var textProp))
                {
                    string text = textProp.GetString() ?? string.Empty;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            Clipboard.SetText(text);
                        }
                        catch { }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join(1000);

                    bool shouldPaste = doc.RootElement.TryGetProperty("paste", out var pasteProp) && pasteProp.GetBoolean();
                    if (shouldPaste)
                    {
                        Thread.Sleep(50);
                        _input.SendHotkey("ctrl_v");
                    }

                    return Results.Ok(new { success = true, pasted = shouldPaste });
                }
            }
            catch { }
            return Results.BadRequest(new { error = "Geçersiz pano isteği." });
        });

        _app.MapGet("/api/desktop/clipboard", () =>
        {
            string clipboardText = string.Empty;
            try
            {
                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Clipboard.ContainsText())
                            clipboardText = Clipboard.GetText();
                    }
                    catch { }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                thread.Join(1000);
            }
            catch { }
            return Results.Ok(new { text = clipboardText });
        });

        // Desktop and display actions
        _app.MapPost("/api/desktop/power", (DesktopPowerRequest req) =>
        {
            if (req.Action == "lock")
            {
                _input.SendHotkey("lock");
                return Results.Ok(new { success = true, message = "Windows kilitlendi." });
            }
            if (req.Action == "display_off" || req.Action == "monitor_off")
            {
                _input.SendHotkey("monitor_off");
                return Results.Ok(new { success = true, message = "Monitör uykuya alındı." });
            }
            if (req.Action == "show_desktop")
            {
                _input.SendHotkey("win_d");
                return Results.Ok(new { success = true, message = "Masaüstüne dönüldü." });
            }
            return Results.BadRequest(new { error = "Geçersiz güç eylemi." });
        });

        await _app.StartAsync(ct);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app is null) return;

        await _app.StopAsync(ct);
        await _app.DisposeAsync();
        _app = null;
    }
}

public sealed record DesktopPowerRequest(string Action);
