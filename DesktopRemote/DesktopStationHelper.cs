using System.Runtime.InteropServices;

namespace VdaHubServerController.DesktopRemote;

public static class DesktopStationHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr handle, int index, System.Text.StringBuilder name, int length, out int needed);

    public static bool IsInteractiveDesktopActive()
    {
        const uint desktopReadObjects = 0x0001;
        const int userObjectName = 2;
        var desktop = OpenInputDesktop(0, false, desktopReadObjects);
        if (desktop == IntPtr.Zero) return false;

        try
        {
            var name = new System.Text.StringBuilder(128);
            return GetUserObjectInformation(desktop, userObjectName, name, name.Capacity * sizeof(char), out _) &&
                name.ToString().Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenDesktop(string lpszDesktop, int dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll")]
    internal static extern bool CloseDesktop(IntPtr hDesktop);

    public static IntPtr GetDefaultDesktopHandle()
    {
        try
        {
            const int desktopReadObjects = 0x0001;
            return OpenDesktop("Default", 0, false, desktopReadObjects);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
