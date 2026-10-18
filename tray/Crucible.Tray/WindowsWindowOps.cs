using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Crucible.Tray
{
    /// <summary>
    /// IWindowOps over Win32 - exactly the calls decision D3 permits (spec 2026-10-15 §6.4): a top-level window's process
    /// image path through a query-limited handle, to recognise WoW's windows under the folder the person picked; for those
    /// windows alone, the handle to read the title, rect and minimised/maximised state, strip or restore the border and
    /// move it (ruling S-R19); and the monitors. No hooks, no memory, no input. Every failure carries its Win32 error
    /// back to the core.
    /// </summary>
    internal sealed class WindowsWindowOps : IWindowOps
    {
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        private const long WS_CAPTION = 0x00C00000L, WS_THICKFRAME = 0x00040000L, WS_MINIMIZEBOX = 0x00020000L, WS_MAXIMIZEBOX = 0x00010000L, WS_SYSMENU = 0x00080000L;
        private const long WS_EX_DLGMODALFRAME = 0x00000001L, WS_EX_CLIENTEDGE = 0x00000200L, WS_EX_STATICEDGE = 0x00020000L, WS_EX_WINDOWEDGE = 0x00000100L;
        private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x00000001;
        private const uint MONITORINFOF_PRIMARY = 1;
        private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

        /// <summary>Border styles we take off to span, and put back to restore; the original values are kept per window.</summary>
        private readonly Dictionary<IntPtr, (long style, long ex)> original = new Dictionary<IntPtr, (long, long)>();

        public IReadOnlyList<DisplayInfo> Displays()
        {
            var list = new List<DisplayInfo>();
            IntPtr previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
                {
                    var info = new MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(MONITORINFOEX)) };
                    if (!GetMonitorInfoW(monitor, ref info)) return true;
                    string name = info.szDevice;
                    var device = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
                    string id = EnumDisplayDevicesW(name, 0, ref device, EDD_GET_DEVICE_INTERFACE_NAME) && device.DeviceID.Length > 0 ? device.DeviceID : name;
                    list.Add(new DisplayInfo
                    {
                        Id = id, Name = name, IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        Bounds = ToRect(info.rcMonitor), WorkArea = ToRect(info.rcWork),
                    });
                    return true;
                }, IntPtr.Zero);
            }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            return list;
        }

        /// <summary>
        /// Ruling S-R19 (notice v7: "it looks at which program each open window belongs to, and nothing else about them"). Per
        /// window, in this order: is it visible, its process, that process's exe path, and the core's program rule on the path.
        /// Only a window whose program the rule accepts has anything else read: its title, then its rect, style and
        /// minimised/maximised state. The rule runs inside the native EnumWindows callback, so an exception from it counts as
        /// "not WoW" and never escapes the callback.
        /// </summary>
        public IReadOnlyList<WindowInfo> TopLevelWindows(Func<string, bool> isWowProgram)
        {
            var list = new List<WindowInfo>();
            IntPtr previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                EnumWindows((hwnd, lparam) =>
                {
                    if (!IsWindowVisible(hwnd)) return true;
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    string exe = ImagePath(pid);
                    if (exe.Length == 0 || !Accepts(isWowProgram, exe)) return true;
                    var title = new StringBuilder(256);
                    GetWindowTextW(hwnd, title, title.Capacity);
                    if (title.Length == 0) return true;
                    if (!GetWindowRect(hwnd, out RECT r)) return true;
                    long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                    // Ruling P2-R13: a minimised window's rect is where Windows parked it (-32000,-32000); the core never
                    // remembers it and refuses to span it.
                    list.Add(new WindowInfo
                    {
                        Handle = hwnd, ExePath = exe, Title = title.ToString(), Bounds = ToRect(r), HasCaption = (style & WS_CAPTION) == WS_CAPTION,
                        Minimized = IsIconic(hwnd), Maximized = IsZoomed(hwnd),
                    });
                    return true;
                }, IntPtr.Zero);
            }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
            return list;
        }

        /// <summary>The core's program rule, called from inside the native callback: nothing it throws may escape there.</summary>
        private static bool Accepts(Func<string, bool> isWowProgram, string exe)
        {
            try { return isWowProgram(exe); }
            catch (Exception) { return false; }   // not WoW
        }

        /// <summary>The process's exe path through the least access Windows allows - the one process read D3 permits.</summary>
        private static string ImagePath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                int size = 1024;
                var sb = new StringBuilder(size);
                return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : "";
            }
            finally { CloseHandle(h); }
        }

        public bool StripBorder(IntPtr handle, out int error)
        {
            if (!ReadLong(handle, GWL_STYLE, out long style, out error) || !ReadLong(handle, GWL_EXSTYLE, out long ex, out error)) return false;
            if (!original.ContainsKey(handle)) original[handle] = (style, ex);
            return Set(handle, style & ~(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU),
                ex & ~(WS_EX_DLGMODALFRAME | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE | WS_EX_WINDOWEDGE), out error);
        }

        /// <summary>
        /// Puts the border back by ADDING the caption bits, never by replaying a remembered style: after a tray restart
        /// the only style this process ever saw may be the stripped one (part 2 Task 4 review). The remembered extended
        /// style is restored when known; otherwise the window keeps its current one. Once restored, the window's entry is
        /// forgotten, so the memory holds only windows that are stripped now.
        /// </summary>
        public bool RestoreBorder(IntPtr handle, out int error)
        {
            if (!ReadLong(handle, GWL_STYLE, out long style, out error)) return false;
            style |= WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU;
            long ex;
            if (original.TryGetValue(handle, out (long style, long ex) o)) ex = o.ex;
            else if (!ReadLong(handle, GWL_EXSTYLE, out ex, out error)) return false;
            if (!Set(handle, style, ex, out error)) return false;
            original.Remove(handle);
            return true;
        }

        /// <summary>
        /// One style read that can fail. GWL_STYLE is never 0 on a live window (it is at least visible), so 0 there is a
        /// failed read - a dead handle - whatever the last error says. GWL_EXSTYLE may legitimately be 0, so it failed only
        /// when it is 0 AND the last error (cleared just before) is set - the documented test.
        /// </summary>
        private static bool ReadLong(IntPtr handle, int index, out long value, out int error)
        {
            SetLastError(0);
            value = GetWindowLongPtr(handle, index).ToInt64();
            error = value == 0 ? Marshal.GetLastWin32Error() : 0;
            if (value == 0 && (index == GWL_STYLE || error != 0)) return false;
            error = 0;
            return true;
        }

        private static bool Set(IntPtr handle, long style, long ex, out int error)
        {
            // SetWindowLongPtr returns the previous value, which may legitimately be 0: it failed only when it returns 0
            // AND the last error (cleared just before) is set - the documented test.
            if (!SetLong(handle, GWL_STYLE, style, out error)) return false;
            if (!SetLong(handle, GWL_EXSTYLE, ex, out error)) return false;
            // SWP_FRAMECHANGED without a move: the new style takes effect now, so the next Move is measured against it.
            if (!SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOSIZE | SWP_NOMOVE))
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            error = 0;
            return true;
        }

        private static bool SetLong(IntPtr handle, int index, long value, out int error)
        {
            SetLastError(0);
            if (SetWindowLongPtr(handle, index, new IntPtr(value)) == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                if (error != 0) return false;
            }
            error = 0;
            return true;
        }

        public bool Move(IntPtr handle, Rect to, out int error)
        {
            IntPtr previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try
            {
                if (SetWindowPos(handle, IntPtr.Zero, to.Left, to.Top, to.Width, to.Height, SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED)) { error = 0; return true; }
                error = Marshal.GetLastWin32Error();
                return false;
            }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        public Rect? BoundsOf(IntPtr handle)
        {
            IntPtr previous = SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            try { return GetWindowRect(handle, out RECT r) ? ToRect(r) : (Rect?)null; }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }

        public void Focus(IntPtr handle) { SetForegroundWindow(handle); }

        /// <summary>For the selftest output only: names and rects, ids cut to their instance segment (<see cref="ShortId"/>), no paths.</summary>
        public static string Describe(IReadOnlyList<DisplayInfo> displays)
        {
            var sb = new StringBuilder();
            foreach (DisplayInfo d in displays)
            {
                string id = ShortId(d.Id);
                sb.Append(d.Name).Append(d.IsPrimary ? " (primary) " : " ").Append(d.Bounds).Append(" work ").Append(d.WorkArea).Append(" id ").Append(id).Append('\n');
            }
            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>Ruling S-R8: a monitor interface id ends in the monitor class GUID, the same for every display
        /// (\\?\DISPLAY#DEL40F5#5&amp;2b2a3f7c&amp;0&amp;UID4352#{e6f07b5f-...}), so show its instance segment - the one before the
        /// GUID - at most 24 characters (the end, where the UID is), in ASCII.</summary>
        private static string ShortId(string id)
        {
            string s = id;
            int guid = s.LastIndexOf("#{", StringComparison.Ordinal);
            if (guid > 0)
            {
                s = s.Substring(0, guid);
                s = s.Substring(s.LastIndexOf('#') + 1);
            }
            return s.Length > 24 ? "..." + s.Substring(s.Length - 24) : s;
        }

        private static Rect ToRect(RECT r) => new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

        // GetWindowLongPtrW / SetWindowLongPtrW exist in 64-bit user32 only; a 32-bit process uses the Long forms.
        private static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
        private static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));

        [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevicesW(string device, uint devNum, ref DISPLAY_DEVICE displayDevice, uint flags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lparam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] private static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder exe, ref int size);
        // CloseHandle and SetLastError touch only this process: its own query handle and its own thread's error value.
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
    }
}
