using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
    string Command,
    string? Args
);

public sealed class DesktopRemoteServer
{
    private readonly DesktopScreenService _screen;
    private readonly DesktopInputService _input;
    private readonly DesktopAppManagerService _apps;
    private readonly int _port;
    private readonly string? _apiKey;
    private WebApplication? _app;

    public DesktopRemoteServer(
        DesktopScreenService screen,
        DesktopInputService input,
        DesktopAppManagerService apps,
        int port = 5088,
        string? apiKey = null)
    {
        _screen = screen;
        _input = input;
        _apps = apps;
        _port = port;
        _apiKey = apiKey;
    }

    public int Port => _port;
    public bool IsRunning => _app != null;

    public async Task StartAsync(CancellationToken ct = default)
    {
        var hostBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>()
        });

        hostBuilder.Logging.ClearProviders();
        hostBuilder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(_port);
        });

        hostBuilder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
            {
                policy.AllowAnyOrigin()
                      .AllowAnyMethod()
                      .AllowAnyHeader();
            });
        });

        _app = hostBuilder.Build();
        _app.UseCors();

        // Authentication Middleware / Filter
        _app.Use(async (context, next) =>
        {
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                // Bypass auth for web favicon / root if query token matches
                string? token = context.Request.Query["token"];
                if (string.IsNullOrEmpty(token) && context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey))
                {
                    token = headerKey.ToString();
                }
                if (string.IsNullOrEmpty(token) && context.Request.Headers.TryGetValue("Authorization", out var authHeader))
                {
                    var val = authHeader.ToString();
                    if (val.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = val["Bearer ".Length..].Trim();
                }

                // If not matched and path starts with /api/, reject
                if (context.Request.Path.StartsWithSegments("/api") &&
                    !string.Equals(token, _apiKey, StringComparison.Ordinal))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await context.Response.WriteAsJsonAsync(new { error = "Yetkisiz erişim. Geçerli VDAKor token sağlayın." });
                    return;
                }
            }

            await next();
        });

        // 1. Web Controller UI
        _app.MapGet("/", async context =>
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(GetEmbeddedWebClientHtml());
        });

        // 2. Info
        _app.MapGet("/api/desktop/info", () =>
        {
            var (w, h) = _screen.GetScreenMetrics();
            return Results.Ok(new
            {
                machineName = Environment.MachineName,
                userName = Environment.UserName,
                osVersion = Environment.OSVersion.VersionString,
                screen = new { width = w, height = h },
                port = _port,
                hasAuth = !string.IsNullOrWhiteSpace(_apiKey)
            });
        });

        // 3. Snapshot JPG
        _app.MapGet("/api/desktop/snapshot.jpg", (int? q, float? s) =>
        {
            int quality = q ?? 75;
            float scale = s ?? 1.0f;
            var bytes = _screen.CaptureJpeg(quality, scale);
            return Results.File(bytes, "image/jpeg");
        });

        // 4. Live MJPEG Stream
        _app.MapGet("/api/desktop/stream", async (HttpContext context, int? fps, int? q, float? s) =>
        {
            int targetFps = fps ?? 25;
            int quality = q ?? 65;
            float scale = s ?? 0.75f;
            await _screen.StreamMjpegAsync(context, targetFps, quality, scale, context.RequestAborted);
        });

        // 5. Input
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

        // 6. Running Apps
        _app.MapGet("/api/desktop/apps", () =>
        {
            var apps = _apps.GetRunningApplications();
            return Results.Ok(new { count = apps.Count, apps });
        });

        // 7. Focus App
        _app.MapPost("/api/desktop/apps/{pid:int}/focus", (int pid) =>
        {
            bool ok = _apps.FocusApplication(pid);
            return ok ? Results.Ok(new { success = true }) : Results.NotFound(new { error = "Pencere bulunamadı veya odaklanamadı." });
        });

        // 8. Close App
        _app.MapPost("/api/desktop/apps/{pid:int}/close", (int pid, bool? force) =>
        {
            bool ok = _apps.CloseApplication(pid, force ?? false);
            return ok ? Results.Ok(new { success = true }) : Results.BadRequest(new { error = "Uygulama sonlandırılamadı." });
        });

        // 9. Launch App
        _app.MapPost("/api/desktop/launch", (DesktopLaunchRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Command))
                return Results.BadRequest(new { error = "Komut veya program yolu boş olamaz." });

            bool ok = _apps.LaunchApplication(req.Command, req.Args);
            return ok ? Results.Ok(new { success = true, command = req.Command }) : Results.BadRequest(new { error = "Program başlatılamadı." });
        });

        // 10. Clipboard Sync & Direct Paste
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

        // 11. Lock / Display Off / System Actions
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
        Console.WriteLine($"[VDAKor Desktop Remote] Sunucu aktif: http://0.0.0.0:{_port}");
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app != null)
        {
            await _app.StopAsync(ct);
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private static string GetEmbeddedWebClientHtml()
    {
        return """
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no">
    <title>VDAKor Desktop Remote</title>
    <style>
        :root {
            --primary: #3b82f6;
            --primary-dark: #1d4ed8;
            --bg: #0f172a;
            --card-bg: #1e293b;
            --text: #f8fafc;
            --text-dim: #94a3b8;
            --danger: #ef4444;
            --success: #10b981;
            --border: #334155;
        }
        * { box-sizing: border-box; margin: 0; padding: 0; }
        body {
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif;
            background: var(--bg);
            color: var(--text);
            display: flex;
            flex-direction: column;
            height: 100vh;
            overflow: hidden;
            touch-action: manipulation;
        }
        header {
            background: var(--card-bg);
            border-bottom: 1px solid var(--border);
            padding: 10px 16px;
            display: flex;
            align-items: center;
            justify-content: space-between;
            z-index: 10;
        }
        .logo { font-weight: 700; font-size: 1.1rem; color: #fff; display: flex; align-items: center; gap: 8px; }
        .logo span { color: var(--primary); }
        .status-badge { font-size: 0.75rem; background: #065f46; color: #34d399; padding: 3px 8px; border-radius: 999px; }

        #screen-container {
            flex: 1;
            position: relative;
            background: #000;
            display: flex;
            align-items: center;
            justify-content: center;
            overflow: hidden;
        }
        #desktop-stream {
            max-width: 100%;
            max-height: 100%;
            object-fit: contain;
            cursor: crosshair;
            user-select: none;
            -webkit-user-drag: none;
        }
        .controls-panel {
            background: var(--card-bg);
            border-top: 1px solid var(--border);
            padding: 10px 14px;
            display: flex;
            flex-direction: column;
            gap: 8px;
            z-index: 10;
            max-height: 45vh;
            overflow-y: auto;
        }
        .btn-row {
            display: flex;
            gap: 6px;
            flex-wrap: wrap;
            align-items: center;
        }
        .btn {
            background: #334155;
            color: #fff;
            border: none;
            padding: 8px 12px;
            border-radius: 8px;
            font-size: 0.85rem;
            font-weight: 600;
            cursor: pointer;
            display: inline-flex;
            align-items: center;
            gap: 5px;
            touch-action: manipulation;
        }
        .btn:hover { background: #475569; }
        .btn:active { transform: scale(0.97); }
        .btn-primary { background: var(--primary); }
        .btn-primary:hover { background: var(--primary-dark); }
        .btn-danger { background: var(--danger); }
        .text-input-group {
            display: flex;
            gap: 6px;
            width: 100%;
        }
        input[type="text"] {
            flex: 1;
            background: #0f172a;
            border: 1px solid var(--border);
            color: #fff;
            padding: 8px 12px;
            border-radius: 8px;
            font-size: 0.9rem;
        }
        input[type="text"]:focus { outline: none; border-color: var(--primary); }

        /* Modal Sheet */
        .sheet {
            display: none;
            position: fixed;
            bottom: 0; left: 0; right: 0;
            background: var(--card-bg);
            border-top: 2px solid var(--primary);
            border-radius: 16px 16px 0 0;
            padding: 16px;
            max-height: 70vh;
            overflow-y: auto;
            z-index: 100;
            box-shadow: 0 -10px 25px rgba(0,0,0,0.5);
        }
        .sheet.active { display: block; }
        .sheet-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 12px;
        }
        .app-item {
            display: flex;
            align-items: center;
            justify-content: space-between;
            background: #0f172a;
            border: 1px solid var(--border);
            padding: 8px 12px;
            border-radius: 8px;
            margin-bottom: 6px;
        }
        .app-title { font-size: 0.85rem; font-weight: 500; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 65%; }
        .app-actions { display: flex; gap: 6px; }
        .app-actions button { padding: 4px 8px; font-size: 0.75rem; border-radius: 6px; }
    </style>
</head>
<body>
    <header>
        <div class="logo">🖥️ VDAKor <span>Remote</span></div>
        <div id="status" class="status-badge">● Canlı</div>
    </header>

    <div id="screen-container">
        <img id="desktop-stream" src="/api/desktop/stream?fps=20&quality=65&scale=0.75" alt="Canlı Masaüstü" />
    </div>

    <div class="controls-panel">
        <div class="btn-row">
            <button class="btn" onclick="sendMouseClick('left')">🖱️ Sol Tık</button>
            <button class="btn" onclick="sendMouseClick('right')">🖱️ Sağ Tık</button>
            <button class="btn" onclick="sendMouseClick('double')">⚡ Çift Tık</button>
            <button class="btn" onclick="sendScroll(-240)">▲ Yukarı</button>
            <button class="btn" onclick="sendScroll(240)">▼ Aşağı</button>
            <button class="btn btn-primary" onclick="openAppsSheet()">📱 Programlar</button>
            <button class="btn" onclick="sendHotkey('win_d')">🪟 Masaüstü</button>
        </div>

        <div class="text-input-group">
            <input type="text" id="type-box" placeholder="PC'ye yazı yaz veya komut gir..." onkeydown="if(event.key==='Enter') sendTypedText()" />
            <button class="btn btn-primary" onclick="sendTypedText()">Yaz</button>
            <button class="btn" onclick="sendKey('enter')">↵ Enter</button>
            <button class="btn" onclick="sendKey('backspace')">⌫ Sil</button>
        </div>
    </div>

    <!-- Apps Modal Sheet -->
    <div id="apps-sheet" class="sheet">
        <div class="sheet-header">
            <h3>⚡ Açık Programlar & Başlatıcı</h3>
            <button class="btn btn-danger" onclick="closeAppsSheet()">✕ Kapat</button>
        </div>

        <div style="margin-bottom: 14px;">
            <div style="font-size:0.8rem; color:var(--text-dim); margin-bottom:6px;">HIZLI BAŞLAT:</div>
            <div class="btn-row">
                <button class="btn" onclick="launchApp('chrome')">🌐 Chrome</button>
                <button class="btn" onclick="launchApp('spotify')">🎵 Spotify</button>
                <button class="btn" onclick="launchApp('steam')">🎮 Steam</button>
                <button class="btn" onclick="launchApp('calc')">🔢 Hesap Makinesi</button>
                <button class="btn" onclick="launchApp('notepad')">📝 Not Defteri</button>
                <button class="btn" onclick="launchApp('explorer')">📁 Dosyalar</button>
            </div>
        </div>

        <div style="display:flex; gap:6px; margin-bottom:12px;">
            <input type="text" id="custom-app-input" placeholder="Özel program veya yol (örn: code, cmd, link)" />
            <button class="btn btn-primary" onclick="launchCustomApp()">Başlat</button>
        </div>

        <div style="font-size:0.8rem; color:var(--text-dim); margin-bottom:8px;">AÇIK PENCERELER:</div>
        <div id="apps-list">Yükleniyor...</div>
    </div>

    <script>
        const img = document.getElementById('desktop-stream');

        // Touch & Click Mapping
        img.addEventListener('click', (e) => {
            const rect = img.getBoundingClientRect();
            const normX = (e.clientX - rect.left) / rect.width;
            const normY = (e.clientY - rect.top) / rect.height;
            sendInput({ type: 'click', x: normX, y: normY });
        });

        // Context menu on long press / right click on canvas
        img.addEventListener('contextmenu', (e) => {
            e.preventDefault();
            const rect = img.getBoundingClientRect();
            const normX = (e.clientX - rect.left) / rect.width;
            const normY = (e.clientY - rect.top) / rect.height;
            sendInput({ type: 'right_click', x: normX, y: normY });
        });

        async function sendInput(data) {
            try {
                await fetch('/api/desktop/input', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(data)
                });
            } catch (err) {
                console.error('Input hatası:', err);
            }
        }

        function sendMouseClick(btn) {
            if (btn === 'left') sendInput({ type: 'click' });
            else if (btn === 'right') sendInput({ type: 'right_click' });
            else if (btn === 'double') sendInput({ type: 'double_click' });
        }

        function sendScroll(delta) {
            sendInput({ type: 'scroll', dy: delta });
        }

        function sendKey(key) {
            sendInput({ type: 'key', key: key });
        }

        function sendHotkey(hotkey) {
            sendInput({ type: 'hotkey', hotkey: hotkey });
        }

        function sendTypedText() {
            const box = document.getElementById('type-box');
            const val = box.value;
            if (!val) return;
            sendInput({ type: 'text', text: val });
            box.value = '';
        }

        // Apps Sheet
        async function openAppsSheet() {
            document.getElementById('apps-sheet').classList.add('active');
            await loadApps();
        }

        function closeAppsSheet() {
            document.getElementById('apps-sheet').classList.remove('active');
        }

        async function loadApps() {
            const list = document.getElementById('apps-list');
            list.innerHTML = 'Yükleniyor...';
            try {
                const res = await fetch('/api/desktop/apps');
                const data = await res.json();
                if (!data.apps || data.apps.length === 0) {
                    list.innerHTML = '<div style="color:var(--text-dim); padding:10px;">Açık pencere bulunamadı.</div>';
                    return;
                }
                list.innerHTML = data.apps.map(app => `
                    <div class="app-item">
                        <div class="app-title" title="${app.windowTitle}">${app.windowTitle}</div>
                        <div class="app-actions">
                            <button class="btn btn-primary" onclick="focusApp(${app.processId})">Öne Getir</button>
                            <button class="btn btn-danger" onclick="closeApp(${app.processId})">Kapat</button>
                        </div>
                    </div>
                `).join('');
            } catch (err) {
                list.innerHTML = '<div style="color:var(--danger); padding:10px;">Hata oluştu.</div>';
            }
        }

        async function focusApp(pid) {
            await fetch(`/api/desktop/apps/${pid}/focus`, { method: 'POST' });
            closeAppsSheet();
        }

        async function closeApp(pid) {
            if (confirm('Bu programı kapatmak istediğinize emin misiniz?')) {
                await fetch(`/api/desktop/apps/${pid}/close`, { method: 'POST' });
                await loadApps();
            }
        }

        async function launchApp(cmd) {
            await fetch('/api/desktop/launch', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ command: cmd })
            });
            closeAppsSheet();
        }

        async function launchCustomApp() {
            const input = document.getElementById('custom-app-input');
            const val = input.value.trim();
            if (!val) return;
            await launchApp(val);
            input.value = '';
        }
    </script>
</body>
</html>
""";
    }
}

public sealed record DesktopPowerRequest(string Action);
