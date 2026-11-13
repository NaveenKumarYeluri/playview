using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PlayView.Views;

/// <summary>
/// Hosts an external window (scrcpy) as a child inside the WPF pane.
/// </summary>
public class MirrorHost : HwndHost
{
    public IntPtr ChildHwnd { get; private set; }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        ChildHwnd = Native.CreateWindowExW(0, "STATIC", null,
            (int)(Native.WS_CHILD | Native.WS_VISIBLE), 0, 0, 1, 1,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        return new HandleRef(this, ChildHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Native.DestroyWindow(hwnd.Handle);
        ChildHwnd = IntPtr.Zero;
    }
}
