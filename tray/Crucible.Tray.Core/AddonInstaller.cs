using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Crucible.Tray
{
    /// <summary>
    /// The only writer into the game folder besides Data.lua. It writes a whole addon or none of it: everything goes
    /// into a temp folder beside the target, is read back and re-hashed there, and only then takes the target's
    /// place. A failure leaves exactly what was there before, and no temp folder.
    ///
    /// The rename (2026-10-04, remove at 1.0.0): the addon was Tallybook, in Interface\AddOns\Tallybook. Installing
    /// the Crucible folder retires that one - its Data.lua comes across when the new folder has none of its own, and
    /// the old folder is removed - but ONLY when its Tallybook.toc is ours. Somebody else's folder is never touched.
    /// </summary>
    public static class AddonInstaller
    {
        public const int MaxFiles = 40;
        public const int MaxFileBytes = 1024 * 1024;
        public const int MaxTotalBytes = 8 * 1024 * 1024;

        /// <summary>The addon's folder before the rename. Remove at 1.0.0.</summary>
        public const string OldFolderName = "Tallybook";

        /// <summary>The player's own prices live here; an update must never throw them away.</summary>
        private const string DataFile = "Data.lua";
        private static readonly Regex VersionLine = new Regex(@"^## Version:\s*(\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        // Two lines every published Tallybook.toc carried, 0.1.0 to 0.18.0, and both are needed: the saved variable
        // alone could be anybody's addon of the same name; the words of the Notes line are ours.
        private static readonly Regex OldSavedVariables = new Regex(@"^## SavedVariables:\s*TallybookDB\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex OldNotes = new Regex(@"^## Notes:\s*Auction house price notebook\.", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        /// <summary>What is installed at <paramref name="addonFolder"/>, or null when there is no addon there.</summary>
        public static string? InstalledVersion(string addonFolder)
        {
            try
            {
                string toc = Path.Combine(addonFolder, GameFolders.TocName);
                if (!File.Exists(toc)) return null;
                Match m = VersionLine.Match(File.ReadAllText(toc, Encoding.UTF8));
                return m.Success ? m.Groups[1].Value : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// Installs <paramref name="manifest"/> at <paramref name="addonFolder"/>, whole or not at all. Throws with
        /// a plain reason when the manifest is refused or the swap cannot be made. Returns a line for the log.
        /// </summary>
        public static string Install(string addonFolder, AddonManifest manifest)
        {
            string? why = AddonManifest.Reject(manifest);
            if (why != null) throw new InvalidOperationException("the addon was refused: " + why);

            string parent = Path.GetDirectoryName(Path.GetFullPath(addonFolder))
                ?? throw new InvalidOperationException("the addon folder has no parent");
            string name = Path.GetFileName(Path.GetFullPath(addonFolder));
            string stamp = Guid.NewGuid().ToString("N").Substring(0, 8);
            string staged = Path.Combine(parent, name + ".new-" + stamp);
            string aside = Path.Combine(parent, name + ".old-" + stamp);
            bool had = Directory.Exists(addonFolder);
            string? old = OldFolderBeside(addonFolder);

            try
            {
                Directory.CreateDirectory(staged);
                foreach (AddonFile f in manifest.Files)
                {
                    // Combine only ever sees a name Reject has already approved; this is the second lock.
                    string path = Path.Combine(staged, f.Name);
                    if (Path.GetDirectoryName(Path.GetFullPath(path)) != Path.GetFullPath(staged))
                        throw new InvalidOperationException("the addon was refused: \"" + f.Name + "\" would land outside the folder");
                    File.WriteAllText(path, f.Content, new UTF8Encoding(false));
                }
                // Read back what is actually on the disk, not what we meant to write.
                foreach (AddonFile f in manifest.Files)
                {
                    if (AddonManifest.Hash(File.ReadAllText(Path.Combine(staged, f.Name), Encoding.UTF8)) != f.Sha256.ToLowerInvariant())
                        throw new IOException("\"" + f.Name + "\" did not survive being written");
                }
                // The player's own prices come across an update - or, the first time, from our old Tallybook folder;
                // only a fresh install with neither keeps the manifest's empty one.
                string installedData = Path.Combine(addonFolder, DataFile);
                string? oldData = old == null ? null : Path.Combine(old, DataFile);
                if (had && File.Exists(installedData)) File.Copy(installedData, Path.Combine(staged, DataFile), true);
                else if (oldData != null && File.Exists(oldData)) File.Copy(oldData, Path.Combine(staged, DataFile), true);
            }
            catch (Exception)
            {
                Discard(staged);
                throw;
            }

            if (had)
            {
                Directory.Move(addonFolder, aside);
                try
                {
                    Directory.Move(staged, addonFolder);
                }
                catch (Exception)
                {
                    Directory.Move(aside, addonFolder); // put back what was there
                    Discard(staged);
                    throw;
                }
                Discard(aside);
            }
            else
            {
                try
                {
                    Directory.Move(staged, addonFolder);
                }
                catch (Exception)
                {
                    Discard(staged);
                    throw;
                }
            }
            string done = (had ? "updated" : "installed") + " the addon, version " + manifest.Version + ", " + manifest.Files.Count + " files";
            return old == null ? done : done + "; " + Retire(old, stamp);
        }

        /// <summary>
        /// The Tallybook folder beside <paramref name="addonFolder"/>, when that is the Crucible folder and the old one's
        /// Tallybook.toc is ours; otherwise null. Remove at 1.0.0.
        /// </summary>
        private static string? OldFolderBeside(string addonFolder)
        {
            string full = Path.GetFullPath(addonFolder);
            if (!string.Equals(Path.GetFileName(full), GameFolders.AddonName, StringComparison.OrdinalIgnoreCase)) return null;
            string? parent = Path.GetDirectoryName(full);
            if (parent == null) return null;
            string old = Path.Combine(parent, OldFolderName);
            return IsOurOldFolder(old) ? old : null;
        }

        private static bool IsOurOldFolder(string folder)
        {
            try
            {
                var toc = new FileInfo(Path.Combine(folder, OldFolderName + ".toc"));
                if (!toc.Exists || toc.Length > MaxFileBytes) return false;
                string text = File.ReadAllText(toc.FullName, Encoding.UTF8);
                return OldSavedVariables.IsMatch(text) && OldNotes.IsMatch(text);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Removes our old folder once the new one is in place: moved aside first, so a delete that stops half-way never
        /// leaves a Tallybook folder the game would still load. Never throws - the install itself has worked, and the
        /// returned words say what became of the old folder.
        /// </summary>
        private static string Retire(string old, string stamp)
        {
            if (!IsOurOldFolder(old)) return "left the old Tallybook folder alone: it changed while the addon was installed";
            string aside = old + ".old-" + stamp;
            try
            {
                Directory.Move(old, aside);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return "could not remove the old Tallybook folder: " + e.GetType().Name;
            }
            Discard(aside);
            return Directory.Exists(aside) ? "moved the old Tallybook folder aside but could not delete it" : "removed the old Tallybook folder";
        }

        private static void Discard(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
