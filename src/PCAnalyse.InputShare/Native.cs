using System.Runtime.InteropServices;

namespace PCAnalyse.InputShare;

internal static class Native
{
    public const int WhMouseLl = 14;
    public const int WhKeyboardLl = 13;
    public const int WmMouseMove = 0x0200;
    public const int WmLButtonDown = 0x0201;
    public const int WmLButtonUp = 0x0202;
    public const int WmRButtonDown = 0x0204;
    public const int WmRButtonUp = 0x0205;
    public const int WmMButtonDown = 0x0207;
    public const int WmMButtonUp = 0x0208;
    public const int WmMouseWheel = 0x020A;
    public const int WmKeyDown = 0x0100;
    public const int WmKeyUp = 0x0101;
    public const int WmSysKeyDown = 0x0104;
    public const int WmSysKeyUp = 0x0105;
    public const uint LlmhfInjected = 0x00000001;
    public const uint LlkhfInjected = 0x10;
    public const uint InputMouse = 0;
    public const uint InputKeyboard = 1;
    public const uint MouseeventfMove = 0x0001;
    public const uint MouseeventfLeftdown = 0x0002;
    public const uint MouseeventfLeftup = 0x0004;
    public const uint MouseeventfRightdown = 0x0008;
    public const uint MouseeventfRightup = 0x0010;
    public const uint MouseeventfMiddledown = 0x0020;
    public const uint MouseeventfMiddleup = 0x0040;
    public const uint MouseeventfWheel = 0x0800;
    public const uint MouseeventfAbsolute = 0x8000;
    public const uint MouseeventfVirtualdesk = 0x4000;
    public const uint KeyeventfKeyup = 0x0002;
    public const int SmXvirtualscreen = 76;
    public const int SmYvirtualscreen = 77;
    public const int SmCxvirtualscreen = 78;
    public const int SmCyvirtualscreen = 79;

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    public const uint PmRemove = 1;
    public const int VkLButton = 0x01;
    public const int VkRButton = 0x02;
    public const int VkMButton = 0x04;

    public const uint WmQuit = 0x0012;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkLeft = 0x25;
    public const int VkRight = 0x27;
    public const int VkR = 0x52;

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Point Point;
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    public static extern bool ClipCursor(IntPtr rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Msllhookstruct
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Kbdllhookstruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public Mouseinput Mouse;
        [FieldOffset(0)] public Keybdinput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mouseinput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Keybdinput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    public static ScreenBounds VirtualScreen()
    {
        return new ScreenBounds(
            GetSystemMetrics(SmXvirtualscreen),
            GetSystemMetrics(SmYvirtualscreen),
            Math.Max(1, GetSystemMetrics(SmCxvirtualscreen)),
            Math.Max(1, GetSystemMetrics(SmCyvirtualscreen)));
    }
}

internal readonly record struct ScreenBounds(int X, int Y, int Width, int Height)
{
    public int Right => X + Width - 1;
    public int Bottom => Y + Height - 1;
}
