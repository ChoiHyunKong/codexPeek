using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CodexPeek;

public sealed partial class WidgetWindow
{
    private bool verifyingWorkArea;
    private void AttachWorkAreaGuard()
    {
        if (verifyFolder is not null && !verifyingWorkArea) return;
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WorkAreaHook);
    }
    private IntPtr WorkAreaHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x001A || message == 0x007E) // Work-area / display configuration changed.
            Dispatcher.BeginInvoke(new Action(ClampToWorkArea));
        if (message != 0x0046 || lParam == IntPtr.Zero || WindowState == WindowState.Minimized) return IntPtr.Zero;
        var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
        const uint NoSize = 1, NoMove = 2;
        if ((position.Flags & (NoSize | NoMove)) == (NoSize | NoMove) || !GetWindowRect(hwnd, out var current)) return IntPtr.Zero;
        int x = (position.Flags & NoMove) != 0 ? current.Left : position.X;
        int y = (position.Flags & NoMove) != 0 ? current.Top : position.Y;
        int width = (position.Flags & NoSize) != 0 ? current.Right - current.Left : position.Width;
        int height = (position.Flags & NoSize) != 0 ? current.Bottom - current.Top : position.Height;
        if (width <= 0 || height <= 0) return IntPtr.Zero;
        var desired = new DesktopRect(x, y, width, height);
        var fitted = FitToMonitor(desired);
        if (desired == fitted) return IntPtr.Zero;
        position.X = fitted.X; position.Y = fitted.Y; position.Width = fitted.Width; position.Height = fitted.Height;
        position.Flags &= ~(NoSize | NoMove); // Keep the existing Z order and pin preference.
        Marshal.StructureToPtr(position, lParam, false);
        return IntPtr.Zero;
    }
    private static DesktopRect FitToMonitor(DesktopRect desired)
    {
        var rect = new RECT { Left = desired.X, Top = desired.Y, Right = desired.X + desired.Width, Bottom = desired.Y + desired.Height };
        var monitor = MonitorFromRect(ref rect, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return desired;
        return DesktopPlacement.Fit(desired, new DesktopRect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top));
    }
    private void ClampToWorkArea()
    {
        if ((verifyFolder is not null && !verifyingWorkArea) || closed || WindowState == WindowState.Minimized) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        var before = new DesktopRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        var after = FitToMonitor(before);
        if (before != after) SetWindowPos(hwnd, IntPtr.Zero, after.X, after.Y, after.Width, after.Height, 0x14); // NOZORDER | NOACTIVATE
    }
    private async Task VerifyWorkAreaAsync()
    {
        verifyingWorkArea = true; AttachWorkAreaGuard();
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var rect)) throw new Exception("Cannot inspect test window bounds");
        var monitor = MonitorFromRect(ref rect, 2);
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new Exception("Cannot inspect monitor work area");
        if (!SetWindowPos(hwnd, IntPtr.Zero, info.Work.Left + 20, info.Work.Bottom - 190, 300, 200, 0x14)) throw new Exception("Cannot move test window");
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (!GetWindowRect(hwnd, out var actual) || actual.Bottom > info.Work.Bottom || actual.Top < info.Work.Top)
            throw new Exception("Native work-area guard allowed taskbar overlap");
        if (Topmost) throw new Exception("Work-area guard changed pin preference");
    }
    [StructLayout(LayoutKind.Sequential)] private struct WINDOWPOS { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
