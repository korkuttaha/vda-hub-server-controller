using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VdaHubServerController.DesktopRemote;

using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
var owner = identity.User ?? throw new InvalidOperationException("Test user is unavailable.");
using var process = Process.GetCurrentProcess();
var sessionId = (uint)process.SessionId;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var ct = timeout.Token;

using var deniedIdentity = RestrictedIdentity.Create(identity.AccessToken);
var server = new DesktopRemoteServer(new(), new(), new());
await server.StartAsync(ct);
try
{
    var tcp = IPGlobalProperties.GetIPGlobalProperties();
    Check(!tcp.GetActiveTcpListeners().Any(endpoint => endpoint.Port == 5088),
        "The legacy desktop TCP port must not be listening on the isolated test host.");

    using var local = new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectCallback = async (_, cancellation) => await DesktopPipe.ConnectAsync(sessionId, owner, cancellation)
    }) { BaseAddress = new Uri("http://localhost") };
    using var response = await local.GetAsync("/api/desktop/info", ct);
    Check(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Locked,
        $"The owner must reach the pipe, with the desktop gated on locked or headless hosts; got {response.StatusCode}.");
    if (response.StatusCode == HttpStatusCode.OK)
    {
        using var frame = await local.GetAsync("/api/desktop/snapshot.jpg?q=40&s=0.2", ct);
        var jpeg = await frame.Content.ReadAsByteArrayAsync(ct);
        Check(frame.IsSuccessStatusCode && frame.Content.Headers.ContentType?.MediaType == "image/jpeg" &&
            jpeg.Length > 4 && jpeg[0] == 0xff && jpeg[1] == 0xd8,
            "An unlocked owner must receive a JPEG through the authenticated pipe.");

        using var unknownShortcut = new StringContent(
            """{"command":"vdakor-unknown-test-shortcut"}""", Encoding.UTF8, "application/json");
        using var launch = await local.PostAsync("/api/desktop/launch", unknownShortcut, ct);
        Check(launch.StatusCode == HttpStatusCode.BadRequest,
            "Desktop commands must be limited to the application's own shortcuts.");
    }

    WindowsIdentity.RunImpersonated(deniedIdentity, () =>
    {
        using var pipe = new NamedPipeClientStream(".", DesktopPipe.Name(sessionId, owner), PipeDirection.InOut,
            PipeOptions.None, TokenImpersonationLevel.Anonymous);
        try
        {
            pipe.Connect(1500);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        throw new InvalidOperationException("Windows granted pipe access to a token without an allowed SID.");
    });

    Console.WriteLine($"Desktop transport checks passed: no TCP listener, owner {response.StatusCode}, unauthorized token denied.");
}
finally
{
    await server.StopAsync();
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal static class RestrictedIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(
        SafeAccessTokenHandle existing, uint flags, uint disableCount, IntPtr disable,
        uint deletePrivilegeCount, IntPtr deletePrivileges, uint restrictCount, ref SidAndAttributes restrict,
        out SafeAccessTokenHandle result);

    internal static SafeAccessTokenHandle Create(SafeAccessTokenHandle current)
    {
        // The additional Windows access check has no SID in common with the pipe's allowed accounts.
        var unrelated = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
        var bytes = new byte[unrelated.BinaryLength];
        unrelated.GetBinaryForm(bytes, 0);
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            const uint disableMaxPrivilege = 1;
            var restrict = new SidAndAttributes { Sid = pin.AddrOfPinnedObject() };
            if (!CreateRestrictedToken(current, disableMaxPrivilege, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, ref restrict, out var result))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return result;
        }
        finally
        {
            pin.Free();
        }
    }
}
