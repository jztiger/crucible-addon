using System;
using System.Runtime.InteropServices;

namespace Crucible.Tray
{
    /// <summary>
    /// Ruling S-R9: the process is DPI-unaware, so window coordinates are virtualised (the owner's 4K at 150 %: physical
    /// 3840 = logical 2560). Inside this scope the calling thread is Per-Monitor V2, so a top-level window created and
    /// placed here takes physical coordinates - the ones <see cref="WindowsWindowOps.Displays"/> reports - and keeps that
    /// context for its life. Dispose puts the thread's previous context back. Before Windows 10 1607 there is no such call:
    /// the scope does nothing and coordinates stay logical.
    /// </summary>
    internal static class DpiScope
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        public static IDisposable PerMonitorV2()
        {
            try { return new Restore(SetThreadDpiAwarenessContext(PerMonitorAwareV2)); }
            catch (EntryPointNotFoundException) { return new Restore(IntPtr.Zero); }
        }

        private sealed class Restore : IDisposable
        {
            private IntPtr previous;
            public Restore(IntPtr previous) { this.previous = previous; }
            public void Dispose()
            {
                if (previous == IntPtr.Zero) return;
                SetThreadDpiAwarenessContext(previous);
                previous = IntPtr.Zero;
            }
        }

        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    }
}
