private void EnsureWindowFitsMonitor(IntPtr monitorHandle)
{
    var info = new MONITORINFO
    {
        cbSize = Marshal.SizeOf<MONITORINFO>()
    };

    if (!GetMonitorInfo(monitorHandle, ref info))
        return;

    double monitorWidth = info.rcWork.Right - info.rcWork.Left;
    double monitorHeight = info.rcWork.Bottom - info.rcWork.Top;

    if (Width > monitorWidth)
        Width = monitorWidth;

    if (Height > monitorHeight)
        Height = monitorHeight;

    if (Left < info.rcWork.Left)
        Left = info.rcWork.Left;

    if (Top < info.rcWork.Top)
        Top = info.rcWork.Top;

    if (Left + Width > info.rcWork.Right)
        Left = info.rcWork.Right - Width;

    if (Top + Height > info.rcWork.Bottom)
        Top = info.rcWork.Bottom - Height;
}
