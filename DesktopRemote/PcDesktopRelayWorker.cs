using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VdaHubServerController.Services;

namespace VdaHubServerController.DesktopRemote;

public sealed class PcDesktopRelayWorker(PcConfigService config, ILogger<PcDesktopRelayWorker> logger) : BackgroundService
{
    private const int MessageLimit = 128 * 1024;
    private const int FrameLimit = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly HttpMessageInvoker HubTransport = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false
    });

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    internal static string PipeName(int sessionId) => $"VDAKor.PcDesktop.{sessionId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.FromSeconds(10);
            try
            {
                var settings = config.Load();
                var sessionId = unchecked((int)WTSGetActiveConsoleSessionId());
                if (sessionId > 0 && PcEnrollmentService.IsEnrolled(settings))
                {
                    using var local = CreateDesktopClient(sessionId);
                    if (await HasDesktopAsync(local, stoppingToken))
                    {
                        using var socket = new ClientWebSocket();
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                        socket.Options.SetRequestHeader("X-API-Key", settings.ApiKey);
                        var hub = new Uri(settings.HubBaseUrl);
                        var uri = new UriBuilder(new Uri(hub,
                            $"/api/computers/bridge/{Uri.EscapeDataString(settings.ComputerId)}/desktop"))
                        {
                            Scheme = "wss",
                            Port = hub.Port
                        }.Uri;

                        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                        {
                            connect.CancelAfter(TimeSpan.FromSeconds(10));
                            await socket.ConnectAsync(uri, HubTransport, connect.Token);
                        }

                        delay = TimeSpan.FromSeconds(1);
                        await RunConnectionAsync(socket, local, sessionId, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogDebug("Desktop relay disconnected ({ErrorType}).", ex.GetType().Name);
            }

            await Task.Delay(delay, stoppingToken);
        }
    }

    private static HttpClient CreateDesktopClient(int sessionId) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (_, ct) =>
        {
            var pipe = new NamedPipeClientStream(".", PipeName(sessionId), PipeDirection.InOut,
                PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Anonymous);
            try
            {
                await pipe.ConnectAsync(1200, ct);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }
    })
    {
        BaseAddress = new Uri("http://localhost"),
        Timeout = TimeSpan.FromSeconds(4)
    };

    private static async Task<bool> HasDesktopAsync(HttpClient local, CancellationToken ct)
    {
        using var response = await local.GetAsync("/api/desktop/info", ct);
        return response.IsSuccessStatusCode;
    }

    private static async Task RunConnectionAsync(WebSocket socket, HttpClient local, int sessionId, CancellationToken stoppingToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var sendLock = new SemaphoreSlim(1, 1);
        var ct = lifetime.Token;
        var transport = ServeViewerAsync(socket, local, sendLock, ct);
        var desktop = WatchDesktopAsync(local, sessionId, ct);
        await Task.WhenAny(transport, desktop);
        await lifetime.CancelAsync();
        try
        {
            await Task.WhenAll(transport, desktop);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Either peer disconnected or the interactive Windows session ended.
        }
    }

    private static async Task WatchDesktopAsync(HttpClient local, int sessionId, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(ct) &&
               unchecked((int)WTSGetActiveConsoleSessionId()) == sessionId && await HasDesktopAsync(local, ct))
        {
        }
    }

    private static async Task ServeViewerAsync(WebSocket socket, HttpClient local, SemaphoreSlim sendLock, CancellationToken ct)
    {
        var first = await ReceiveTextAsync(socket, ct);
        if (first != "start") return;

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var input = ReceiveRequestsAsync(socket, local, sendLock, session.Token);
        var pictures = SendFramesAsync(socket, local, sendLock, session.Token);
        await Task.WhenAny(input, pictures);
        await session.CancelAsync();
        await Task.WhenAll(input, pictures);
    }

    private static async Task SendFramesAsync(WebSocket socket, HttpClient local, SemaphoreSlim sendLock, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(125));
        do
        {
            using var capture = CancellationTokenSource.CreateLinkedTokenSource(ct);
            capture.CancelAfter(local.Timeout);
            using var response = await local.GetAsync(
                "/api/desktop/snapshot.jpg?q=60&s=0.6", HttpCompletionOption.ResponseHeadersRead, capture.Token);
            if (response.StatusCode == HttpStatusCode.Locked) return;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > FrameLimit) continue;
            await response.Content.LoadIntoBufferAsync(FrameLimit, capture.Token);
            var picture = await response.Content.ReadAsByteArrayAsync(capture.Token);
            await SendAsync(socket, sendLock, picture, WebSocketMessageType.Binary, ct);
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private static async Task ReceiveRequestsAsync(WebSocket socket, HttpClient local, SemaphoreSlim sendLock, CancellationToken ct)
    {
        while (await ReceiveTextAsync(socket, ct) is { } text)
        {
            using var json = JsonDocument.Parse(text);
            var request = json.RootElement.Deserialize<ViewerRequest>(Json);
            if (request is null || request.Id < 1 || request.Action is null) return;

            var (code, body) = await DispatchAsync(local, request, ct);
            var response = JsonSerializer.SerializeToUtf8Bytes(new { request.Id, status = code, body }, Json);
            if (response.Length > MessageLimit)
                response = JsonSerializer.SerializeToUtf8Bytes(new { request.Id, status = 413 }, Json);
            await SendAsync(socket, sendLock, response, WebSocketMessageType.Text, ct);
        }
    }

    private static async Task<(int Code, JsonElement? Body)> DispatchAsync(HttpClient local, ViewerRequest request, CancellationToken ct)
    {
        var action = request.Action;
        var pid = request.ProcessId;
        var path = action switch
        {
            "info" => "/api/desktop/info",
            "apps" => "/api/desktop/apps",
            "input" when IsDesktopInput(request.Body) => "/api/desktop/input",
            "focus" when pid > 0 => $"/api/desktop/apps/{pid}/focus",
            "close" when pid > 0 => $"/api/desktop/apps/{pid}/close",
            "clipboardRead" or "clipboardWrite" => "/api/desktop/clipboard",
            "power" => "/api/desktop/power",
            "launch" when IsQuickLaunch(request.Body) => "/api/desktop/launch",
            _ => null
        };
        if (path is null) return (400, null);

        var method = action is "info" or "apps" or "clipboardRead" ? HttpMethod.Get : HttpMethod.Post;
        using var message = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (method == HttpMethod.Post && request.Body is { ValueKind: JsonValueKind.Object } body)
            message.Content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await local.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Content.Headers.ContentType?.MediaType != "application/json")
                return ((int)response.StatusCode, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            await response.Content.LoadIntoBufferAsync(MessageLimit, timeout.Token);
            var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsByteArrayAsync(timeout.Token), Json);
            return ((int)response.StatusCode, result);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested &&
            ex is OperationCanceledException or HttpRequestException or IOException or JsonException)
        {
            return (503, null);
        }
    }

    private static bool IsQuickLaunch(JsonElement? body) =>
        body is { ValueKind: JsonValueKind.Object } value &&
        !value.TryGetProperty("args", out _) &&
        value.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String &&
        command.GetString() is "chrome" or "spotify" or "steam" or "calc" or "notepad" or "explorer";

    private static bool IsDesktopInput(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } value ||
            !value.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return false;

        return type.GetString() switch
        {
            "move" or "click" or "right_click" or "middle_click" or "double_click" or "scroll" or "key" or "hotkey" => true,
            "text" => value.TryGetProperty("text", out var text) &&
                      text.ValueKind == JsonValueKind.String && text.GetString()!.Length <= 4096,
            _ => false
        };
    }

    private static async Task SendAsync(
        WebSocket socket, SemaphoreSlim sendLock, byte[] data, WebSocketMessageType type, CancellationToken ct)
    {
        await sendLock.WaitAsync(ct);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            await socket.SendAsync(data.AsMemory(), type, true, timeout.Token);
        }
        finally
        {
            sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            using var message = new MemoryStream();
            ValueWebSocketReceiveResult read;
            do
            {
                read = await socket.ReceiveAsync(buffer.AsMemory(), ct);
                if (read.MessageType == WebSocketMessageType.Close) return null;
                if (read.MessageType != WebSocketMessageType.Text || message.Length + read.Count > MessageLimit)
                    throw new InvalidDataException("Invalid desktop request.");
                message.Write(buffer, 0, read.Count);
            } while (!read.EndOfMessage);
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private sealed record ViewerRequest(int Id, string? Action, int ProcessId, JsonElement? Body);
}
