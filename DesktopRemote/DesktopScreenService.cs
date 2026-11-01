using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http;

namespace VdaHubServerController.DesktopRemote;

public sealed class DesktopScreenService
{
    private static readonly ImageCodecInfo JpegCodec;

    static DesktopScreenService()
    {
        JpegCodec = ImageCodecInfo.GetImageEncoders()
            .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    public (int Width, int Height) GetScreenMetrics()
    {
        DesktopStationHelper.AttachToDefaultDesktop();
        int w = GetSystemMetrics(SM_CXSCREEN);
        int h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0 || h <= 0)
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            w = bounds.Width;
            h = bounds.Height;
        }
        return (w, h);
    }

    public byte[] CaptureJpeg(int quality = 70, float scale = 1.0f, bool drawCursor = true)
    {
        DesktopStationHelper.AttachToDefaultDesktop();

        quality = Math.Clamp(quality, 10, 100);
        scale = Math.Clamp(scale, 0.2f, 1.0f);

        int srcWidth = GetSystemMetrics(SM_CXSCREEN);
        int srcHeight = GetSystemMetrics(SM_CYSCREEN);
        if (srcWidth <= 0 || srcHeight <= 0)
        {
            var bounds = Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
            srcWidth = bounds.Width;
            srcHeight = bounds.Height;
        }

        using var screenBmp = new Bitmap(srcWidth, srcHeight, PixelFormat.Format32bppRgb);
        using (var g = Graphics.FromImage(screenBmp))
        {
            g.CopyFromScreen(0, 0, 0, 0, new Size(srcWidth, srcHeight), CopyPixelOperation.SourceCopy);

            if (drawCursor)
            {
                DrawCursorOverlay(g, 0, 0);
            }
        }

        Bitmap outputBmp = screenBmp;
        bool isScaled = Math.Abs(scale - 1.0f) > 0.05f;
        if (isScaled)
        {
            int targetWidth = (int)(srcWidth * scale);
            int targetHeight = (int)(srcHeight * scale);
            outputBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppRgb);
            using var gScale = Graphics.FromImage(outputBmp);
            gScale.InterpolationMode = InterpolationMode.Bilinear;
            gScale.DrawImage(screenBmp, 0, 0, targetWidth, targetHeight);
        }

        try
        {
            using var ms = new MemoryStream();
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);

            outputBmp.Save(ms, JpegCodec, encoderParams);
            return ms.ToArray();
        }
        finally
        {
            if (isScaled)
            {
                outputBmp.Dispose();
            }
        }
    }

    public async Task StreamMjpegAsync(
        HttpContext context,
        int targetFps = 25,
        int quality = 65,
        float scale = 0.75f,
        CancellationToken ct = default)
    {
        targetFps = Math.Clamp(targetFps, 1, 60);
        int delayMs = 1000 / targetFps;

        var response = context.Response;
        response.ContentType = "multipart/x-mixed-replace; boundary=--frame";
        response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Connection"] = "close";

        var boundaryBytes = System.Text.Encoding.ASCII.GetBytes("\r\n--frame\r\n");

        while (!ct.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            byte[] jpeg;
            try
            {
                jpeg = CaptureJpeg(quality, scale);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Stream Error] {ex.Message}");
                await Task.Delay(200, ct);
                continue;
            }

            var header = $"Content-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n";
            var headerBytes = System.Text.Encoding.ASCII.GetBytes(header);

            try
            {
                await response.Body.WriteAsync(boundaryBytes, ct);
                await response.Body.WriteAsync(headerBytes, ct);
                await response.Body.WriteAsync(jpeg, ct);
                await response.Body.FlushAsync(ct);
            }
            catch
            {
                // Client disconnected
                break;
            }

            sw.Stop();
            int remaining = delayMs - (int)sw.ElapsedMilliseconds;
            if (remaining > 0)
            {
                try
                {
                    await Task.Delay(remaining, ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static void DrawCursorOverlay(Graphics g, int screenOffsetX, int screenOffsetY)
    {
        try
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
            if (GetCursorInfo(out ci) && (ci.flags & CURSOR_SHOWING) != 0 && ci.hCursor != IntPtr.Zero)
            {
                int cursorX = ci.ptScreenPos.X - screenOffsetX;
                int cursorY = ci.ptScreenPos.Y - screenOffsetY;

                var hdc = g.GetHdc();
                try
                {
                    DrawIconEx(hdc, cursorX, cursorY, ci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
        }
        catch
        {
            // Non-critical overlay failure
        }
    }
}
