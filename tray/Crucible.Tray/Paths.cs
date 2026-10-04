using System;
using System.IO;
using System.Reflection;

namespace Crucible.Tray
{
    /// <summary>The only places outside the game folder this program reads or writes.</summary>
    internal static class Paths
    {
        private static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        /// <summary>%APPDATA%\Crucible</summary>
        public static string Home => Path.Combine(AppData, "Crucible");
        public static string Config => Path.Combine(Home, "config.json");
        public static string Sent => Path.Combine(Home, "sent.txt");
        public static string Log => Path.Combine(Home, "log.txt");

        /// <summary>
        /// %APPDATA%\Tallybook\config.json: this program's config under its old name. Read once, only when there is no
        /// Crucible config yet, and never written or deleted. Remove at 1.0.0 (and the file with it).
        /// </summary>
        public static string OldConfig => Path.Combine(AppData, "Tallybook", "config.json");

        public static string Exe => Assembly.GetExecutingAssembly().Location;
        /// <summary>The member's download, as saved beside the exe. Imported once, then deleted.</summary>
        public static string Download => Path.Combine(Path.GetDirectoryName(Exe) ?? ".", "crucible.config.json");
    }
}
