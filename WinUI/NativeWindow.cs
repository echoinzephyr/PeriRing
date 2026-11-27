using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;

namespace BatteryDisplay;

internal static class NativeWindow
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MinMaxInfo
    {
        public Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }
    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data);
    private static readonly SubclassProc minimumSizeProc = MinimumSizeMessage;
    private static readonly Dictionary<IntPtr, (double Width, double Height)> minimumSizes = [];
    private static readonly Dictionary<IntPtr, Action> deviceChangeCallbacks = [];
    private const uint GetMinMaxInfoMessage = 0x0024;
    private const uint DeviceChangeMessage = 0x0219;
    private const nuint MinimumSizeSubclassId = 0x5052;
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, nuint id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMinMaxMessage(IntPtr hwnd, uint message, IntPtr wParam, ref MinMaxInfo info);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowW(string className, string? windowName);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")] private static extern bool IsWindowVisibleNative(IntPtr hwnd);
    public static bool IsVisible(IntPtr hwnd) => IsWindowVisibleNative(hwnd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
    public static void Error(string title, string message) => MessageBoxW(IntPtr.Zero, message, title, 0x10);
    public static bool OwnsForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == Environment.ProcessId;
    }
    public static void Configure(IntPtr hwnd)
    {
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
    }
    public static void SetMinimumSize(IntPtr hwnd, double width, double height)
    {
        // Let the native sizing loop clamp the dragged edge before the window changes.
        // Resizing again from AppWindow.Changed causes left/top drags to move the opposite edge.
        minimumSizes[hwnd] = (width, height);
        if (!SetWindowSubclass(hwnd, minimumSizeProc, MinimumSizeSubclassId, 0))
        {
            minimumSizes.Remove(hwnd);
            throw new InvalidOperationException("无法设置浮层的最小尺寸");
        }
    }
    public static void SetDeviceChangeHandler(IntPtr hwnd, Action onDeviceChanged) => deviceChangeCallbacks[hwnd] = onDeviceChanged;

    private static IntPtr MinimumSizeMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, nuint id, nuint data)
    {
        var result = DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == GetMinMaxInfoMessage && lParam != IntPtr.Zero && minimumSizes.TryGetValue(hwnd, out var minimum))
        {
            var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
            double scale = Scale(hwnd);
            info.MinTrackSize.X = Math.Max(info.MinTrackSize.X, (int)Math.Ceiling(minimum.Width * scale));
            info.MinTrackSize.Y = Math.Max(info.MinTrackSize.Y, (int)Math.Ceiling(minimum.Height * scale));
            Marshal.StructureToPtr(info, lParam, false);
        }
        else if (message == DeviceChangeMessage && (nuint)wParam == 0x0007 && deviceChangeCallbacks.TryGetValue(hwnd, out var callback))
        {
            callback();
        }
        else if (message == 0x0082) // WM_NCDESTROY
        {
            minimumSizes.Remove(hwnd);
            deviceChangeCallbacks.Remove(hwnd);
            RemoveWindowSubclass(hwnd, minimumSizeProc, id);
        }
        return result;
    }
    internal static Windows.Graphics.SizeInt32 MinimumTrackingSize(IntPtr hwnd)
    {
        var info = new MinMaxInfo();
        SendMinMaxMessage(hwnd, GetMinMaxInfoMessage, IntPtr.Zero, ref info);
        return new(info.MinTrackSize.X, info.MinTrackSize.Y);
    }
    public static void SetDark(IntPtr hwnd, bool dark)
    {
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int));
    }
    public static void Show(IntPtr hwnd) { ShowWindow(hwnd, 5); SetForegroundWindow(hwnd); }
    public static void Hide(IntPtr hwnd) => ShowWindow(hwnd, 0);
    public static double Scale(IntPtr hwnd) => Math.Max(1, GetDpiForWindow(hwnd) / 96d);
    public static int TrayIconSize()
    {
        uint dpi = GetDpiForWindow(FindWindowW("Shell_TrayWnd", null));
        if (dpi == 0) dpi = GetDpiForSystem();
        return Math.Clamp(GetSystemMetricsForDpi(49, dpi == 0 ? 96u : dpi), 16, 64);
    }
    public static void ShowInactive(IntPtr hwnd) => ShowWindow(hwnd, 4);
    public static (int X, int Y) CursorPosition() { GetCursorPos(out var point); return (point.X, point.Y); }
    public static void HideFromSwitchers(AppWindow window, IntPtr hwnd)
    {
        try { window.IsShownInSwitchers = false; }
        catch (Exception ex) when (ex is NotImplementedException or COMException)
        {
            var style = GetWindowLongPtrW(hwnd, -20).ToInt64();
            SetWindowLongPtrW(hwnd, -20, new IntPtr(style | 0x80));
            DebugService.Log("window", "switcher-fallback", new { Type = ex.GetType().Name, ex.HResult });
        }
    }
    public static void NoActivate(IntPtr hwnd)
    {
        var style = GetWindowLongPtrW(hwnd, -20).ToInt64();
        SetWindowLongPtrW(hwnd, -20, new IntPtr(style | 0x08000000 | 0x80));
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);
    public static void Position(AppWindow window, IntPtr hwnd, double width, double height)
    {
        GetCursorPos(out var cursor);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfoW(MonitorFromPoint(cursor, 2), ref info)) return;
        double scale = Math.Max(1, GetDpiForWindow(hwnd) / 96d);
        int w = Math.Min((int)(width * scale), info.Work.Right - info.Work.Left - 24);
        int h = Math.Min((int)(height * scale), info.Work.Bottom - info.Work.Top - 24);
        int x = info.Work.Right - w - 12;
        int y = info.Work.Bottom - h - 12;
        bool atTaskbar = cursor.Y >= info.Work.Bottom || cursor.Y <= info.Work.Top || cursor.X >= info.Work.Right || cursor.X <= info.Work.Left;
        if (atTaskbar) x = Math.Clamp(cursor.X - w / 2, info.Work.Left + 12, info.Work.Right - w - 12);
        if (info.Work.Top > info.Monitor.Top) y = info.Work.Top + 12;
        if (info.Work.Left > info.Monitor.Left) x = info.Work.Left + 12;
        window.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }
}
