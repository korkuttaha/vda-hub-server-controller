using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VdaHubServerController.DesktopRemote;

public sealed record DesktopAppInfo(
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    string Handle,
    bool IsActive
);

public sealed class DesktopAppManagerService
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumDesktopWindows(IntPtr hDesktop, EnumWindowsProc lpfn, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const uint WM_CLOSE = 0x0010;
    private const int SW_RESTORE = 9;

    public List<DesktopAppInfo> GetRunningApplications()
    {
        var apps = new List<DesktopAppInfo>();
        var seenPids = new HashSet<int>();
        var foregroundHwnd = GetForegroundWindow();

        bool Callback(IntPtr hWnd, IntPtr _)
        {
            if (!IsWindowVisible(hWnd))
                return true;

            // Skip tool windows
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                return true;

            // Check if cloaked by Windows DWM (virtual desktop / background UWP)
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int isCloaked, sizeof(int)) == 0 && isCloaked != 0)
                return true;

            var sb = new StringBuilder(256);
            int len = GetWindowText(hWnd, sb, 256);
            if (len <= 0)
                return true;

            string title = sb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(title))
                return true;

            // Filter out system shell components
            if (title is "Program Manager" or "Windows Input Experience" or "Settings" or "Default IME" or "MSCTFIME UI")
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == 0) return true;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                string procName = proc.ProcessName;

                // Avoid duplicating entries for child windows of the same process
                if (seenPids.Add((int)pid))
                {
                    bool isActive = (hWnd == foregroundHwnd);
                    apps.Add(new DesktopAppInfo(
                        (int)pid,
                        procName,
                        title,
                        hWnd.ToString("X"),
                        isActive
                    ));
                }
            }
            catch
            {
                // Process terminated or inaccessible
            }

            return true;
        }

        var hDesk = DesktopStationHelper.GetDefaultDesktopHandle();
        if (hDesk != IntPtr.Zero)
        {
            try { EnumDesktopWindows(hDesk, Callback, IntPtr.Zero); }
            finally { DesktopStationHelper.CloseDesktop(hDesk); }
        }
        else
        {
            EnumWindows(Callback, IntPtr.Zero);
        }

        return apps.OrderByDescending(a => a.IsActive).ThenBy(a => a.WindowTitle).ToList();
    }

    public bool FocusApplication(int processId)
    {
        var targetHwnd = FindMainWindowForProcess(processId);
        if (targetHwnd == IntPtr.Zero) return false;

        ShowWindowAsync(targetHwnd, SW_RESTORE);
        return SetForegroundWindow(targetHwnd);
    }

    public bool CloseApplication(int processId, bool force = false)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            if (force)
            {
                proc.Kill();
                return true;
            }

            var targetHwnd = FindMainWindowForProcess(processId);
            if (targetHwnd != IntPtr.Zero)
            {
                PostMessage(targetHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public bool LaunchApplication(string commandOrPath, string? arguments = null)
    {
        if (string.IsNullOrWhiteSpace(commandOrPath)) return false;

        string normalized = commandOrPath.Trim().ToLowerInvariant();
        string target = normalized switch
        {
            "chrome" or "google chrome" => "chrome.exe",
            "edge" or "microsoft edge" => "msedge.exe",
            "browser" => "https://google.com",
            "spotify" => "spotify.exe",
            "steam" => "steam.exe",
            "calc" or "calculator" => "calc.exe",
            "notepad" => "notepad.exe",
            "terminal" or "cmd" => "cmd.exe",
            "powershell" or "pwsh" => "powershell.exe",
            "explorer" or "files" => "explorer.exe",
            "taskmgr" or "task manager" => "taskmgr.exe",
            _ => commandOrPath.Trim()
        };

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = target,
                Arguments = arguments ?? "",
                UseShellExecute = true
            };
            Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DesktopAppManager] Başlatma hatası ({target}): {ex.Message}");
            return false;
        }
    }

    private static IntPtr FindMainWindowForProcess(int processId)
    {
        IntPtr found = IntPtr.Zero;
        var hDesk = DesktopStationHelper.GetDefaultDesktopHandle();

        bool CheckHwnd(IntPtr hWnd, IntPtr _)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == processId && IsWindowVisible(hWnd))
            {
                var sb = new StringBuilder(256);
                if (GetWindowText(hWnd, sb, 256) > 0)
                {
                    found = hWnd;
                    return false; // Stop search
                }
            }
            return true;
        }

        if (hDesk != IntPtr.Zero)
        {
            try { EnumDesktopWindows(hDesk, CheckHwnd, IntPtr.Zero); }
            finally { DesktopStationHelper.CloseDesktop(hDesk); }
        }
        else
            EnumWindows(CheckHwnd, IntPtr.Zero);

        return found;
    }
}
