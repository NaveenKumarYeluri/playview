using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PlayView.Views;

/// <summary>
/// Hosts an external window (scrcpy) as a child inside the WPF pane.
/// Uses a custom window class with a black background so letterbox bars look
/// like a video player instead of a white form.
/// </summary>
public class MirrorHost : HwndHost
{
    private const string HostClassName = "PlayViewMirrorHost";
    private static bool _classRegistered;

    // Keep the callback delegate alive for the process lifetime (class callback).
    private static readonly Native.WndProc WndProcCallback = DefWindowProcCallback;

    public IntPtr ChildHwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        if (!_classRegistered)
        {
            var windowClass = new Native.WNDCLASS
            {
                Style = 0,
                WndProc = Marshal.GetFunctionPointerForDelegate(WndProcCallback),
                Instance = Native.GetModuleHandleW(null),
                Background = Native.CreateSolidBrush(0x000000), // black
                ClassName = HostClassName,
            };
            Native.RegisterClassW(ref windowClass);
            _classRegistered = true;
        }

        ChildHwnd = Native.CreateWindowExW(0, HostClassName, null,
            (int)(Native.WS_CHILD | Native.WS_VISIBLE), 0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return new HandleRef(this, ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Native.DestroyWindow(hwnd.Handle);
        ChildHwnd = IntPtr.Zero;
    }

    private static IntPtr DefWindowProcCallback(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
        => Native.DefWindowProcW(hWnd, message, wParam, lParam);
}
