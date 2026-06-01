using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

public partial class MainWindow : Window
{
    private IntPtr _currentMonitor = IntPtr.Zero;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            _currentMonitor = GetCurrentMonitor();
        };

        LocationChanged += (_, _) =>
        {
            var newMonitor = GetCurrentMonitor();

            if (newMonitor != _currentMonitor)
            {
                _currentMonitor = newMonitor;
                OnScreenChanged(newMonitor);
            }
        };
    }

    private IntPtr GetCurrentMonitor()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        return MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    }

    private void OnScreenChanged(IntPtr monitorHandle)
    {
        var info = new MONITORINFO
        {
            cbSize = Marshal.SizeOf<MONITORINFO>()
        };

        if (!GetMonitorInfo(monitorHandle, ref info))
            return;

        MessageBox.Show(
            $"Moved to monitor\n" +
            $"Monitor: {info.rcMonitor.Left}, {info.rcMonitor.Top}, {info.rcMonitor.Right}, {info.rcMonitor.Bottom}\n" +
            $"WorkArea: {info.rcWork.Left}, {info.rcWork.Top}, {info.rcWork.Right}, {info.rcWork.Bottom}");
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
