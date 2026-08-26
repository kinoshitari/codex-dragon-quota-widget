using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DragonQuotaWidget;

public static class CodexProcessMonitor
{
    public static bool TryGetVisibleWindow(out NativeRect selected)
    {
        selected = default;
        var bestRect = default(NativeRect);
        var processIds = GetHostProcessIds();

        if (processIds.Count == 0) return false;

        long bestArea = 0;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (!processIds.Contains(processId) || !IsWindowVisible(handle) || IsIconic(handle) || !GetWindowRect(handle, out var rect)) return true;
            var area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
            if (area > bestArea)
            {
                bestArea = area;
                bestRect = rect;
            }
            return true;
        }, IntPtr.Zero);

        selected = bestRect;
        return bestArea > 0;
    }

    public static bool HasHostWindow()
    {
        var processIds = GetHostProcessIds();
        if (processIds.Count == 0) return false;

        var found = false;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var processId);
            if (processIds.Contains(processId) && (IsWindowVisible(handle) || IsIconic(handle)))
            {
                found = true;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static HashSet<uint> GetHostProcessIds()
    {
        var processIds = new HashSet<uint>();
        foreach (var processName in new[] { "Codex", "ChatGPT" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try { processIds.Add((uint)process.Id); }
                finally { process.Dispose(); }
            }
        }
        return processIds;
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr handle);
}

[StructLayout(LayoutKind.Sequential)]
public struct NativeRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}
