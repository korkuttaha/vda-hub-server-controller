using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VdaHubServerController.DesktopRemote;

internal static class DesktopPipe
{
    private const PipeAccessRights ClientAccess = PipeAccessRights.ReadWrite | PipeAccessRights.ReadPermissions;

    [DllImport("kernel32.dll")]
    internal static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out SafeAccessTokenHandle token);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint sessionId);

    internal static SecurityIdentifier? GetConsoleUser(uint sessionId)
    {
        if (sessionId is 0 or uint.MaxValue || !WTSQueryUserToken(sessionId, out var token))
            return null;

        using var consoleToken = token;
        using var identity = new WindowsIdentity(consoleToken.DangerousGetHandle());
        return identity.User;
    }

    internal static string Name(uint sessionId, SecurityIdentifier owner) =>
        $"VDAKor.PcDesktop.{sessionId}.{owner.Value}";

    internal static PipeSecurity CreateSecurity(SecurityIdentifier owner)
    {
        var security = new PipeSecurity();
        security.SetOwner(owner);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(owner, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), ClientAccess, AccessControlType.Allow));
        return security;
    }

    internal static async Task<NamedPipeClientStream> ConnectAsync(
        uint sessionId, SecurityIdentifier owner, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", Name(sessionId, owner), ClientAccess,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous, HandleInheritability.None);
        try
        {
            await pipe.ConnectAsync(1200, ct);
            // The name selects a candidate. Verify the connected handle belongs to the expected session and owner.
            if (!GetNamedPipeServerSessionId(pipe.SafePipeHandle, out var serverSession) ||
                serverSession != sessionId ||
                !owner.Equals(pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))))
                throw new IOException("The desktop pipe does not belong to the active Windows session.");

            return pipe;
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }
    }
}
