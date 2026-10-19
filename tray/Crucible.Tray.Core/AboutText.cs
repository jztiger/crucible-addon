using System.Text.RegularExpressions;

namespace Crucible.Tray
{
    /// <summary>
    /// Help > About Crucible (owner 2026-10-18, spec 2026-10-17 section 11): the words the window shows, decided here so they
    /// are unit-tested - the window itself is glue. Pure: no files, no WinForms. Lines are separated by "\n"; the window turns
    /// them into what its text boxes want.
    /// </summary>
    public static class AboutText
    {
        /// <summary>Said for a newest version nobody has told us yet: not asked yet, or the server did not answer.</summary>
        public const string NotCheckedYet = "not checked yet";
        /// <summary>Said in place of the addon's version when the game folder holds no addon.</summary>
        public const string NotInstalled = "not installed";
        /// <summary>How many of the changelog's newest entries the window shows.</summary>
        public const int ReleaseNoteEntries = 10;

        // An entry of shared/CHANGELOG.txt begins at the left margin with its version ("0.100.0 - ..."); the lines indented under
        // it belong to it. Whatever comes before the first entry - the file's header line - is not an entry.
        private static readonly Regex EntryStart = new Regex(@"^[0-9]+(?:\.[0-9]+)+(?=[ \t]|$)", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>
        /// The About tab: two lines, the tray app and the addon, each as installed here with the newest version the server has
        /// on offer beside it. A newest version still null is "not checked yet"; an addon that is not installed says so.
        /// </summary>
        public static string Versions(string trayInstalled, string? trayOffered, string? addonInstalled, string? addonOffered) =>
            "Crucible tray app " + trayInstalled + " (newest " + (trayOffered ?? NotCheckedYet) + ")\n"
            + "Crucible addon " + (addonInstalled ?? NotInstalled) + " (newest " + (addonOffered ?? NotCheckedYet) + ")";

        /// <summary>
        /// The Release notes tab: the newest <paramref name="entries"/> entries of the changelog, in its own order (newest
        /// first), each with its indented lines, as written. The header line is left out. A changelog with no entry in it, or
        /// <paramref name="entries"/> of none, gives "".
        /// </summary>
        public static string ReleaseNotes(string changelog, int entries)
        {
            if (entries <= 0) return "";
            string text = changelog.Replace("\r\n", "\n");   // a Windows checkout may hold the file with CRLF
            MatchCollection starts = EntryStart.Matches(text);
            if (starts.Count == 0) return "";
            int from = starts[0].Index;
            int to = entries < starts.Count ? starts[entries].Index : text.Length;
            return text.Substring(from, to - from).TrimEnd();
        }
    }
}
