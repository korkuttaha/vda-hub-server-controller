using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VdaHubServerController.DesktopRemote;

public sealed class DesktopInputService
{
    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    private const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;

    private const uint KEYEVENTF_KEYDOWN = 0x0000;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    private const int HWND_BROADCAST = 0xFFFF;
    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_MONITORPOWER = 0xF170;

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;

    public void MoveNormalized(double normX, double normY)
    {
        normX = Math.Clamp(normX, 0.0, 1.0);
        normY = Math.Clamp(normY, 0.0, 1.0);

        int screenWidth = GetSystemMetrics(SM_CXSCREEN);
        int screenHeight = GetSystemMetrics(SM_CYSCREEN);

        int targetX = (int)(normX * screenWidth);
        int targetY = (int)(normY * screenHeight);

        SetCursorPos(targetX, targetY);
    }

    public void MoveRelative(int dx, int dy)
    {
        if (GetCursorPos(out var pt))
        {
            SetCursorPos(pt.X + dx, pt.Y + dy);
        }
    }

    public void Click(double? normX = null, double? normY = null)
    {
        if (normX.HasValue && normY.HasValue)
            MoveNormalized(normX.Value, normY.Value);

        SendMouseEvents(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
    }

    public void DoubleClick(double? normX = null, double? normY = null)
    {
        if (normX.HasValue && normY.HasValue)
            MoveNormalized(normX.Value, normY.Value);

        SendMouseEvents(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
        Thread.Sleep(50);
        SendMouseEvents(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
    }

    public void RightClick(double? normX = null, double? normY = null)
    {
        if (normX.HasValue && normY.HasValue)
            MoveNormalized(normX.Value, normY.Value);

        SendMouseEvents(MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP);
    }

    public void MiddleClick(double? normX = null, double? normY = null)
    {
        if (normX.HasValue && normY.HasValue)
            MoveNormalized(normX.Value, normY.Value);

        SendMouseEvents(MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP);
    }

    public void MouseDown(string button = "left")
    {
        uint flag = button.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTDOWN,
            "middle" => MOUSEEVENTF_MIDDLEDOWN,
            _ => MOUSEEVENTF_LEFTDOWN
        };
        SendMouseEvents(flag);
    }

    public void MouseUp(string button = "left")
    {
        uint flag = button.ToLowerInvariant() switch
        {
            "right" => MOUSEEVENTF_RIGHTUP,
            "middle" => MOUSEEVENTF_MIDDLEUP,
            _ => MOUSEEVENTF_LEFTUP
        };
        SendMouseEvents(flag);
    }

    public void Scroll(int deltaY)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = MOUSEEVENTF_WHEEL,
                    mouseData = (uint)deltaY
                }
            }
        };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    public void TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>();
        foreach (char c in text)
        {
            // Key down with unicode
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE
                    }
                }
            });

            // Key up
            inputs.Add(new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP
                    }
                }
            });
        }

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public void SendKey(string keyName)
    {
        ushort vk = keyName.ToLowerInvariant() switch
        {
            "enter" or "return" => 0x0D,
            "backspace" or "back" => 0x08,
            "tab" => 0x09,
            "escape" or "esc" => 0x1B,
            "space" => 0x20,
            "delete" or "del" => 0x2E,
            "up" or "arrowup" => 0x26,
            "down" or "arrowdown" => 0x28,
            "left" or "arrowleft" => 0x25,
            "right" or "arrowright" => 0x27,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "win" or "windows" => 0x5B,
            _ => 0
        };

        if (vk == 0) return;

        SendKeyVk(vk);
    }

    public void SendHotkey(string hotkey)
    {
        switch (hotkey.ToLowerInvariant())
        {
            case "win_l" or "lock":
                LockWorkStation();
                break;
            case "vol_mute" or "volume_mute":
                SendKeyVk(0xAD); // VK_VOLUME_MUTE
                break;
            case "vol_down" or "volume_down":
                SendKeyVk(0xAE); // VK_VOLUME_DOWN
                break;
            case "vol_up" or "volume_up":
                SendKeyVk(0xAF); // VK_VOLUME_UP
                break;
            case "media_next":
                SendKeyVk(0xB0); // VK_MEDIA_NEXT_TRACK
                break;
            case "media_prev":
                SendKeyVk(0xB1); // VK_MEDIA_PREV_TRACK
                break;
            case "media_play" or "media_play_pause":
                SendKeyVk(0xB3); // VK_MEDIA_PLAY_PAUSE
                break;
            case "monitor_off" or "display_sleep":
                SendMessage(new IntPtr(HWND_BROADCAST), WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2));
                break;
            case "win_d" or "show_desktop":
                SendKeyCombo(0x5B, 0x44); // Win + D
                break;
            case "alt_tab":
                SendKeyCombo(0x12, 0x09); // Alt + Tab
                break;
            case "ctrl_c" or "copy":
                SendKeyCombo(0x11, 0x43); // Ctrl + C
                break;
            case "ctrl_v" or "paste":
                SendKeyCombo(0x11, 0x56); // Ctrl + V
                break;
            case "ctrl_z" or "undo":
                SendKeyCombo(0x11, 0x5A); // Ctrl + Z
                break;
            case "ctrl_a" or "select_all":
                SendKeyCombo(0x11, 0x41); // Ctrl + A
                break;
            case "alt_f4" or "close_window":
                SendKeyCombo(0x12, 0x73); // Alt + F4
                break;
        }
    }

    private void SendKeyVk(ushort vk)
    {
        var inputs = new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYDOWN } }
            },
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } }
            }
        };
        SendInput(2, inputs, Marshal.SizeOf<INPUT>());
    }

    private void SendKeyCombo(ushort modifierVk, ushort keyVk)
    {
        var inputs = new[]
        {
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = modifierVk, dwFlags = KEYEVENTF_KEYDOWN } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = keyVk, dwFlags = KEYEVENTF_KEYDOWN } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = keyVk, dwFlags = KEYEVENTF_KEYUP } } },
            new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = modifierVk, dwFlags = KEYEVENTF_KEYUP } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendMouseEvents(params uint[] flags)
    {
        var inputs = new INPUT[flags.Length];
        for (int i = 0; i < flags.Length; i++)
        {
            inputs[i] = new INPUT
            {
                type = INPUT_MOUSE,
                u = new INPUTUNION
                {
                    mi = new MOUSEINPUT { dwFlags = flags[i] }
                }
            };
        }
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
