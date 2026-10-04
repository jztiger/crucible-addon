using System;
using Microsoft.Win32;

namespace Crucible.Tray
{
    /// <summary>"Start with Windows": one value under the current user's Run key - visible in Task Manager's
    /// Startup tab, removed by switching it off. Nothing machine-wide, no service, no scheduled task.</summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Crucible";
        /// <summary>The value Tallybook, this program's old name, wrote there. Remove at 1.0.0.</summary>
        private const string OldValueName = "Tallybook";

        public static void Apply(bool on)
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null) return;
                if (on) key.SetValue(ValueName, "\"" + Paths.Exe + "\"");
                else key.DeleteValue(ValueName, false);
            }
        }

        /// <summary>
        /// Removes Tallybook's own entry, whatever the switch says, so an old copy of this program does not start beside
        /// it at the next sign-in. Only a value that starts a Tallybook.exe - another program's "Tallybook" is left alone.
        /// </summary>
        public static void ForgetOldName()
        {
            using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key?.GetValue(OldValueName) is string command
                    && command.IndexOf("Tallybook.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                    key.DeleteValue(OldValueName, false);
            }
        }
    }
}
