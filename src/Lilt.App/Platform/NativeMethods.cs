using System.Runtime.InteropServices;

namespace Lilt.App.Platform;

internal static class NativeMethods
{
    private const int GwlExStyle = -20;
    internal const long WsExTransparent = 0x20;
    internal const long WsExToolWindow = 0x80;
    internal const long WsExAppWindow = 0x40000;
    internal const long WsExLayered = 0x80000;
    internal const long WsExNoActivate = 0x08000000;
    internal const uint SwpNoSize = 0x1;
    internal const uint SwpNoMove = 0x2;
    internal const uint SwpNoZOrder = 0x4;
    internal const uint SwpNoActivate = 0x10;
    internal const int WmMouseActivate = 0x21;
    internal const int WmCancelMode = 0x1F;
    internal const int WmHotkey = 0x312;
    internal const int WmDisplayChange = 0x7E;
    internal const int MaNoActivate = 3;
    internal const uint ModAlt = 0x1;
    internal const uint ModControl = 0x2;
    internal const uint ModShift = 0x4;
    internal const uint ModNoRepeat = 0x4000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }

    internal static IntPtr GetExStyle(IntPtr hwnd) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hwnd, GwlExStyle)
        : new IntPtr(GetWindowLong32(hwnd, GwlExStyle));

    internal static void SetExStyle(IntPtr hwnd, long value)
    {
        if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GwlExStyle, new IntPtr(value));
        else SetWindowLong32(hwnd, GwlExStyle, unchecked((int)value));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReleaseCapture();
    [DllImport("user32.dll")]
    internal static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetCapture();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    internal static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}
