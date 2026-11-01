using System.Runtime.InteropServices;

namespace VdaHubServerController.DesktopRemote;

public static class DesktopStationHelper
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetThreadDesktop(IntPtr hDesktop);

    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private const uint DESKTOP_ALL = 0x01FF;

    public static void AttachToDefaultDesktop()
    {
        try
        {
            var hDesk = OpenDesktop("Default", 0, false, DESKTOP_ALL);
            if (hDesk != IntPtr.Zero)
            {
                SetThreadDesktop(hDesk);
            }
        }
        catch
        {
            // Non-critical fallback
        }
    }

    public static IntPtr GetDefaultDesktopHandle()
    {
        try
        {
            return OpenDesktop("Default", 0, false, DESKTOP_ALL);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
