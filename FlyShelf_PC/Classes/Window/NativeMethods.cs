// Copyright © 2024-2026 The FlyShelf Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Text;

namespace FlyShelf.Classes;

/// <summary>
/// Centralized class for all P/Invoke declarations and unmanaged code imports.
/// </summary>
public static partial class NativeMethods
{
    #region Constants

    // Window Styles
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_CHILD = 0x40000000;
    internal const int WS_POPUP = unchecked((int)0x80000000);
    internal const int WS_EX_NOACTIVATE = 0x08000000;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;
    internal const int WS_EX_APPWINDOW = 0x00040000;

    // SetWindowPos Flags
    internal const int HWND_TOPMOST = -1;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_HIDEWINDOW = 0x0080;
    internal const uint SWP_ASYNCWINDOWPOS = 0x4000;
    internal const uint SWP_NOACTIVATE = 0x0010;

    // Monitor Flags
    internal const int MONITOR_DEFAULTTONEAREST = 2;
    internal const int MONITORINFOF_PRIMARY = 1;
    internal const int S_OK = 0;

    // DWM Attributes
    internal const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal static int DWMWA_COLOR_NONE => unchecked((int)0xFFFFFFFE); // Always fully invisible
    internal const int DWMWA_COLOR_DARK_GRAY = 0x002D2D2D;
    internal const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_SYSTEMBACKDROP_TYPE = 38; // Win11 22H2+ (Build 22621): 0=Auto, 1=None, 2=Mica, 3=Acrylic, 4=MicaAlt
    internal const int DWMWCP_ROUND = 2; // Force rounded corners on all devices/VMs

    // Keyboard Hook
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SETTINGCHANGE = 0x001A;

    // Mouse Hook (for click-to-release arrow navigation)
    internal const int WH_MOUSE_LL = 14;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_RBUTTONDOWN = 0x0204;
    internal const int WM_MBUTTONDOWN = 0x0207;

    // Shell Hook Messages
    internal const int HSHELL_APPCOMMAND = 12;

    // App Command Messages
    internal const int APPCOMMAND_VOLUME_MUTE = 8;
    internal const int APPCOMMAND_VOLUME_DOWN = 9;
    internal const int APPCOMMAND_VOLUME_UP = 10;
    internal const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    internal const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    internal const int APPCOMMAND_MEDIA_STOP = 13;
    internal const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;
    internal const int FAPPCOMMAND_KEY = 0x0000;

    // Keyboard event flags
    internal const int KEYEVENTF_KEYUP = 0x0002;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_V = 0x56;
    internal const int VK_MENU = 0x12; // Alt key

    // Mouse event flags
    internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    internal const uint MOUSEEVENTF_LEFTUP = 0x0004;

    // WinEvent constants
    internal const uint WINEVENT_OUTOFCONTEXT = 0;
    internal const uint EVENT_SYSTEM_FOREGROUND = 0x0003;

    // Window style extras
    internal const int WS_EX_LAYERED = 0x00080000;
    internal const uint LWA_ALPHA = 0x02;
    internal const int WS_VISIBLE = 0x10000000;
    internal const int DWMWA_CLOAK = 13;
    internal const int DWMWA_CLOAKED = 14;

    // Clipboard
    internal const int WM_CLIPBOARDUPDATE = 0x031D;

    // GetAncestor flags
    internal const uint GA_ROOTOWNER = 3;

    // Process / Token access rights
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;

    // Console attach
    internal const int ATTACH_PARENT_PROCESS = -1;

    // Power throttling
    internal const int ProcessPowerThrottling = 4;
    internal const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    internal const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    #endregion

    #region Enums

    public enum MonitorFromWindowFlags : uint
    {
        DEFAULTTONULL = 0,
        DEFAULTTOPRIMARY = 1,
        DEFAULTTONEAREST = 2,
    }

    public enum MonitorDpiType
    {
        MDT_EFFECTIVE_DPI = 0,
        MDT_ANGULAR_DPI = 1,
        MDT_RAW_DPI = 2,
        MDT_DEFAULT
    }

    internal enum AccentState
    {
        ACCENT_DISABLED = 0,
        ACCENT_ENABLE_GRADIENT = 1,
        ACCENT_ENABLE_TRANSPARENTGRADIENT = 2,
        ACCENT_ENABLE_BLURBEHIND = 3,
        ACCENT_ENABLE_ACRYLICBLURBEHIND = 4,
        ACCENT_INVALID_STATE = 5
    }

    internal enum WindowCompositionAttribute
    {
        WCA_ACCENT_POLICY = 19
    }

    internal enum QUERY_USER_NOTIFICATION_STATE
    {
        QUNS_NOT_PRESENT = 1,
        QUNS_BUSY = 2,
        QUNS_RUNNING_D3D_FULL_SCREEN = 3,
        QUNS_PRESENTATION_MODE = 4,
        QUNS_ACCEPTS_NOTIFICATIONS = 5,
        QUNS_QUIET_TIME = 6,
        QUNS_APP = 7
    }

    [Flags]
    internal enum DisplayDeviceStateFlags : int
    {
        AttachedToDesktop = 0x1,
        MultiDriver = 0x2,
        PrimaryDevice = 0x4,
        MirroringDriver = 0x8,
        VGACompatibleDevice = 0x10,
        RemovableDevice = 0x20,
        ModesPruned = 0x8000000,
        Remote = 0x4000000,
        Disconnect = 0x2000000
    }

    #endregion

    #region Structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
        public RECT rcDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DISPLAY_DEVICE
    {
        [MarshalAs(UnmanagedType.U4)]
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        [MarshalAs(UnmanagedType.U4)]
        public DisplayDeviceStateFlags StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AccentPolicy
    {
        public AccentState AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    #endregion

    #region Additional Structs

    [StructLayout(LayoutKind.Sequential)]
    internal struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    #endregion

    #region Delegates

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    internal delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT lprcMonitor, IntPtr dwData);
    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);
    internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>Callback delegate for SetWinEventHook (foreground window change tracking).</summary>
    internal delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    #endregion

    #region user32.dll

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsChild(IntPtr hWndParent, IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static partial int GetWindowLong(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr hMonitor);

    // DllImport instead of LibraryImport for SetWindowPos because for some reason it functions differently when using LibraryImport,
    // causing windows to not be topmost and it to be hidden unless you focus on the taskbar.
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(IntPtr hWnd, int hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    internal static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [LibraryImport("user32.dll")]
    internal static partial void keybd_event(byte virtualKey, byte scanCode, uint flags, IntPtr extraInfo);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterShellHookWindow(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeregisterShellHookWindow(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterWindowMessage(string lpString);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    internal static partial IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(IntPtr hhk);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT lpPoint);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetCursorPos(int X, int Y);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr MonitorFromPoint(POINT pt, MonitorFromWindowFlags dwFlags);
    
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr GetForegroundWindow();

    /// <summary>Retrieves the status of the specified virtual key at call time.</summary>
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    /// <summary>Retrieves a handle to the window that contains the specified point.</summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPoint(POINT Point);

    /// <summary>Retrieves a handle to the window at the specified physical screen coordinates.</summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr WindowFromPhysicalPoint(POINT Point);

    /// <summary>Retrieves a handle to the ancestor of the specified window.</summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>Brings the specified window to the foreground.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Brings the specified window to the top of the Z order.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(IntPtr hWnd);

    /// <summary>Defines or redefines a system-wide hot key.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>Frees a hot key previously registered by RegisterHotKey.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>Sets the layered window attributes (alpha, color key).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    /// <summary>64-bit safe GetWindowLongPtr for extended window data.</summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    /// <summary>64-bit safe SetWindowLongPtr for extended window data.</summary>
    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>Sets an event hook function for a range of events.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    /// <summary>Removes an event hook installed by SetWinEventHook.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWinEvent(IntPtr hWinEventHook);

    /// <summary>Places the calling window in the clipboard viewer chain to receive WM_CLIPBOARDUPDATE.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(IntPtr hwnd);

    /// <summary>Removes the calling window from the clipboard viewer chain.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(IntPtr hwnd);

    /// <summary>GetWindowThreadProcessId overload that returns the process ID via out parameter.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Attaches or detaches the input processing of one thread to another.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    /// <summary>Determines whether the specified window handle identifies an existing window.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr hWnd);

    /// <summary>Determines the visibility state of the specified window.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    /// <summary>Synthesizes a mouse motion or button event.</summary>
    [LibraryImport("user32.dll")]
    internal static partial void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>Copies the text of the specified window's title bar into a buffer.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    /// <summary>Invalidates the client area of the specified window.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, [MarshalAs(UnmanagedType.Bool)] bool bErase);

    /// <summary>Updates the specified rectangle or region in a window's client area.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    /// <summary>Destroys an icon and frees any memory the icon occupied.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>Synthesizes keystrokes, mouse motions, and button clicks.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // SendInput structs
    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT { public uint type; public INPUTUNION u; }
    [StructLayout(LayoutKind.Explicit)]
    internal struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    internal const uint INPUT_KEYBOARD = 1;

    #endregion

    #region gdi32.dll

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [LibraryImport("gdi32.dll")]
    internal static partial int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr hObject);

    #endregion

    #region dwmapi.dll

    [DllImport("dwmapi.dll", PreserveSig = true)]
    internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    /// <summary>DwmGetWindowAttribute overload for int output (e.g. DWMWA_CLOAKED).</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    #endregion

    #region shcore.dll

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(IntPtr hMonitor, MonitorDpiType dpiType, out uint dpiX, out uint dpiY);

    #endregion

    #region kernel32.dll

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr GetModuleHandle(string lpModuleName);

    /// <summary>Determines whether the calling process is being debugged by a user-mode debugger.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsDebuggerPresent();

    /// <summary>Determines whether the specified process is being debugged (remote debugger).</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CheckRemoteDebuggerPresent(IntPtr hProcess, [MarshalAs(UnmanagedType.Bool)] out bool isDebuggerPresent);

    /// <summary>Opens an existing local process object for access.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint processAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint processId);

    /// <summary>Closes an open object handle.</summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr hObject);

    /// <summary>Returns the process identifier of the calling process.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentProcessId();

    /// <summary>Returns the thread identifier of the calling thread.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    /// <summary>Attaches the calling process to the console of the specified process.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(int dwProcessId);

    /// <summary>Retrieves the window handle of the console associated with the calling process.</summary>
    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetConsoleWindow();

    /// <summary>Sets the minimum and maximum working set sizes for the specified process.</summary>
    [DllImport("kernel32.dll", EntryPoint = "SetProcessWorkingSetSize", SetLastError = true)]
    internal static extern int SetProcessWorkingSetSize(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    /// <summary>Flushes all inactive pages from the working set of the specified process to disk/pagefile.</summary>
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(IntPtr hProcess);

    /// <summary>Sets information for the specified process (e.g. power throttling).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessInformation(
        IntPtr hProcess,
        int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation,
        uint ProcessInformationSize
    );

    #endregion

    #region shell32.dll

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out QUERY_USER_NOTIFICATION_STATE pquns);

    /// <summary>Creates an ITEMIDLIST from a file-system path.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr ILCreateFromPath([MarshalAs(UnmanagedType.LPWStr)] string pszPath);

    /// <summary>Frees an ITEMIDLIST allocated by the Shell.</summary>
    [LibraryImport("shell32.dll")]
    internal static partial void ILFree(IntPtr pidl);

    /// <summary>Opens a Windows Explorer folder window with specified items selected.</summary>
    [DllImport("shell32.dll")]
    internal static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder,
        uint cidl,
        IntPtr[] apidl,
        uint dwFlags);

    /// <summary>Retrieves information about a file system object (icon, type name).</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    /// <summary>Sends an appbar message to the system (taskbar query).</summary>
    [DllImport("shell32.dll")]
    internal static extern IntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [StructLayout(LayoutKind.Sequential)]
    internal struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    #endregion

    #region advapi32.dll

    /// <summary>Opens the access token associated with a process.</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    /// <summary>Retrieves a specified type of information about an access token.</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr TokenHandle,
        int TokenInformationClass,
        IntPtr TokenInformation,
        int TokenInformationLength,
        out int ReturnLength);

    #endregion

    #region combase.dll

    /// <summary>Gets the activation factory for the specified WinRT runtime class.</summary>
    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    /// <summary>Activates a WinRT runtime class instance.</summary>
    [DllImport("combase.dll", PreserveSig = false)]
    internal static extern void RoActivateInstance(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        out IntPtr instance);

    #endregion

    #region winmm.dll

    /// <summary>Requests a minimum timer resolution (improves rendering timer precision).</summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod", SetLastError = true)]
    internal static partial uint TimeBeginPeriod(uint uMilliseconds);

    /// <summary>Clears a previously set minimum timer resolution.</summary>
    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod", SetLastError = true)]
    internal static partial uint TimeEndPeriod(uint uMilliseconds);

    #endregion

    #region Virtual Desktop COM

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b")]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    internal class VirtualDesktopManager { }

    [DllImport("shell32.dll", SetLastError = true)]
    internal static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string appId);

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("4CE81583-1E4C-4632-A621-07A53543148F")]
    internal interface IVirtualDesktopPinnedApps
    {
        [PreserveSig]
        int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId, out int isPinned);

        [PreserveSig]
        int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [PreserveSig]
        int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);

        [PreserveSig]
        int IsViewPinned(IntPtr applicationView, out int isPinned);

        [PreserveSig]
        int PinView(IntPtr applicationView);

        [PreserveSig]
        int UnpinView(IntPtr applicationView);
    }
    #endregion

    #region Backdrop and Blur Utilities

    public static bool ShouldUseBlur()
    {
        try
        {
            // Must be Windows 11 (Build 22000) or higher
            if (Environment.OSVersion.Version.Major < 10 || 
                (Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Build < 22000))
            {
                return false;
            }

            // Check if transparency is enabled in registry
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                if (key != null)
                {
                    var val = key.GetValue("EnableTransparency");
                    if (val is int intVal)
                    {
                        return intVal == 1;
                    }
                }
            }
        }
        catch { } // Best-effort: failure is acceptable
        return true; // Default to true if anything fails
    }

    public static void EnableCustomAcrylic(IntPtr hwnd, uint tintColor)
    {
        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = tintColor // Format: AABBGGRR
        };

        int size = Marshal.SizeOf(accent);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void DisableCustomAcrylic(IntPtr hwnd)
    {
        var accent = new AccentPolicy
        {
            AccentState = AccentState.ACCENT_DISABLED
        };

        int size = Marshal.SizeOf(accent);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WindowCompositionAttribute.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void ApplyWindowBackdropAndBackground(System.Windows.Window window, System.Windows.Controls.Grid? rootGrid = null)
    {
        if (window == null) return;

        // Defer DWM attribute application to SourceInitialized or Loaded when HWND is guaranteed to be valid
        if (new System.Windows.Interop.WindowInteropHelper(window).Handle == IntPtr.Zero)
        {
            window.SourceInitialized += (s, ev) => ApplyWindowBackdropAndBackgroundInternal(window, rootGrid);
        }
        else
        {
            ApplyWindowBackdropAndBackgroundInternal(window, rootGrid);
        }

        // Hook window activation, deactivation and state changes to prevent Windows 11 DWM from resetting our custom dark gray border
        window.Activated -= Window_BorderResetHandler;
        window.Activated += Window_BorderResetHandler;
        window.Deactivated -= Window_BorderResetHandler;
        window.Deactivated += Window_BorderResetHandler;
        window.StateChanged -= Window_BorderResetHandler;
        window.StateChanged += Window_BorderResetHandler;
    }

    private static void ApplyWindowBackdropAndBackgroundInternal(System.Windows.Window window, System.Windows.Controls.Grid? rootGrid)
    {
        bool enableBlur = SettingsManager.Current.EnableBlurBehind && ShouldUseBlur();
        bool isLight = SettingsManager.Current.ColorScheme == 1;

        // Apply DWM Immersive Dark Mode attribute — adapt to light/dark ColorScheme
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Force rounded corners on ALL devices (VMs, older Win11 builds, etc.)
                int cornerPref = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPref, sizeof(int));

                int darkValue = isLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkValue, sizeof(int));

                if (!enableBlur)
                {
                    // Solid dark/light title bar when blur is off
                    int dwmColor = isLight ? ((245 << 16) | (246 << 8) | 248) : ((26 << 16) | (18 << 8) | 18);
                    DwmSetWindowAttribute(hwnd, 35, ref dwmColor, sizeof(int)); // DWMWA_CAPTION_COLOR
                }
                else
                {
                    // Force a neutral solid titlebar baseline (0x00202020 dark gray / 0x00F5F6F8 light gray)
                    // to override DWM accent coloring titlebar bleeding under system red accent preferences.
                    int darkCaption = isLight ? 0x00F5F6F8 : 0x00202020;
                    DwmSetWindowAttribute(hwnd, 35, ref darkCaption, sizeof(int));
                }

                // Override active window border color to prevent accent border leakage
                                    int borderColor = DWMWA_COLOR_NONE;
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
            }
        }
        catch { } // Best-effort: failure is acceptable

        // Set backdrop and background based on active theme and blur setting
        if (window is MicaWPF.Controls.MicaWindow micaWin)
        {
            string mode = SettingsManager.Current.ThemeDisplayMode ?? "desktop";
            bool blurEnabled = SettingsManager.Current.EnableBlurBehind && ShouldUseBlur();

            if (blurEnabled && window is not MainWindow)
            {
                // Utility windows (HubWindow, EmojiPickerWindow, etc.) ALWAYS get Mica blur.
                // MicaWPF works fine for these since they're initialized once.
                micaWin.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Mica;
                var tintColor = isLight
                    ? System.Windows.Media.Color.FromArgb(200, 243, 243, 243)
                    : System.Windows.Media.Color.FromArgb(200, 32, 32, 32);
                var tintBrush = new System.Windows.Media.SolidColorBrush(tintColor);
                tintBrush.Freeze();
                micaWin.Background = tintBrush;
                if (rootGrid != null) rootGrid.Background = null;
            }
            else if (blurEnabled && mode == "mica" && window is MainWindow)
            {
                // MainWindow Mica mode — v3.0.0 proven approach
                micaWin.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.Tabbed;
                micaWin.Background = System.Windows.Media.Brushes.Transparent;
                if (rootGrid != null) rootGrid.Background = null;
            }
            else if (blurEnabled && mode == "glass" && window is MainWindow)
            {
                // MainWindow glass mode — v3.0.0 proven approach:
                // Disable MicaWPF backdrop, set transparent background, 
                // then apply acrylic via legacy SetWindowCompositionAttribute API.
                micaWin.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                micaWin.Background = System.Windows.Media.Brushes.Transparent;
                if (rootGrid != null) rootGrid.Background = null;

                var hwndVal = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwndVal != IntPtr.Zero)
                {
                    EnableCustomAcrylic(hwndVal, 0x22242424);
                }
            }
            else
            {
                // Solid background fallback — use Windows 11 standard grey tones
                micaWin.SystemBackdropType = MicaWPF.Core.Enums.BackdropType.None;
                var bgColor = isLight
                    ? System.Windows.Media.Color.FromRgb(243, 243, 243)
                    : System.Windows.Media.Color.FromRgb(32, 32, 32);
                var darkBg = new System.Windows.Media.SolidColorBrush(bgColor);
                darkBg.Freeze();
                micaWin.Background = darkBg;
                if (rootGrid != null) rootGrid.Background = darkBg;
            }
        }
        else
        {
            var bgColor = isLight
                ? System.Windows.Media.Color.FromRgb(243, 243, 243)
                : System.Windows.Media.Color.FromRgb(32, 32, 32);
            var bgBrush = new System.Windows.Media.SolidColorBrush(bgColor);
            bgBrush.Freeze();
            window.Background = bgBrush;
            if (rootGrid != null) rootGrid.Background = bgBrush;
        }
    }

    private static void Window_BorderResetHandler(object? sender, System.EventArgs e)
    {
        if (sender is System.Windows.Window window)
        {
            // Set DWM border color synchronously to prevent flashing
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    bool isLight = SettingsManager.Current.ColorScheme == 1;
                                        int borderColor = DWMWA_COLOR_NONE;
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                }
            }
            catch { } // Best-effort: failure is acceptable

            // Defer setting to Send priority so it runs immediately after the activation message is processed by DefWindowProc
            window.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero)
                    {
                        bool isLight = SettingsManager.Current.ColorScheme == 1;
                                            int borderColor = DWMWA_COLOR_NONE;
                        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderColor, sizeof(int));
                    }
                }
                catch { } // Best-effort: failure is acceptable
            }, System.Windows.Threading.DispatcherPriority.Send);
        }
    }

    #endregion
}
