using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>
    /// The one place this program writes into somebody's game folder. Everything here is about what it REFUSES:
    /// a manifest is installed whole or not at all, and never a byte outside the addon's own folder.
    /// </summary>
    public class AddonInstallerTests
    {
        private static string Sha(string content)
        {
            using (SHA256 sha = SHA256.Create())
            {
                var hex = new StringBuilder(64);
                foreach (byte b in sha.ComputeHash(Encoding.UTF8.GetBytes(content))) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }

        private static AddonFile File_(string name, string content) =>
            new AddonFile { Name = name, Content = content, Sha256 = Sha(content) };

        private const string Toc = "## Interface: 16001\n## Title: Crucible\n## Version: 0.9.0\n\nLogic.lua\nData.lua\n";
        private const string EmptyData = "-- GENERATED\nns.baked = {\n}\n";

        /// <summary>The toc of the addon before the rename, as every published version carried it (0.1.0 to 0.18.0).</summary>
        internal const string OldToc = "## Interface: 16001\n## Title: Tallybook\n"
            + "## Notes: Auction house price notebook. Read-only: never posts, bids, buys or cancels. Scans only when you ask.\n"
            + "## Author: jztiger\n## Version: 0.18.0\n## SavedVariables: TallybookDB\n\nLogic.lua\nData.lua\n";
        private const string OldPrices = "-- the old folder's prices\nns.baked = { prices = { [2589] = 170 } }\n";
        private const string NoToc = "(no toc)";

        private static AddonManifest Good(params AddonFile[] extra)
        {
            var files = new List<AddonFile> { File_("Crucible.toc", Toc), File_("Logic.lua", "local _, ns = ...\nns.L = {}\n"), File_("Data.lua", EmptyData) };
            files.AddRange(extra);
            return new AddonManifest { Version = "0.9.0", Files = files };
        }

        /// <summary>An AddOns folder with another addon beside ours, so a stray write shows up.</summary>
        private static (string addons, string target) AddOns(TempDir dir, bool installed)
        {
            string addons = Path.Combine(dir.Path, "Interface", "AddOns");
            Directory.CreateDirectory(Path.Combine(addons, "SomeoneElse"));
            System.IO.File.WriteAllText(Path.Combine(addons, "SomeoneElse", "Other.lua"), "not ours");
            string target = Path.Combine(addons, "Crucible");
            if (installed)
            {
                Directory.CreateDirectory(target);
                System.IO.File.WriteAllText(Path.Combine(target, "Crucible.toc"), Toc.Replace("0.9.0", "0.8.0"));
                System.IO.File.WriteAllText(Path.Combine(target, "Logic.lua"), "old logic\n");
                System.IO.File.WriteAllText(Path.Combine(target, "Gone.lua"), "dropped by the new version\n");
                System.IO.File.WriteAllText(Path.Combine(target, "Data.lua"), "-- the player's own prices\nns.baked = { prices = { [2589] = 160 } }\n");
            }
            return (addons, target);
        }

        /// <summary>A Tallybook folder beside the target: the addon as it was installed before the rename, unless told otherwise.</summary>
        private static string OldFolder(string addons, string toc = OldToc, string? data = OldPrices)
        {
            string old = Path.Combine(addons, "Tallybook");
            Directory.CreateDirectory(old);
            if (toc != NoToc) System.IO.File.WriteAllText(Path.Combine(old, "Tallybook.toc"), toc);
            System.IO.File.WriteAllText(Path.Combine(old, "Logic.lua"), "old logic\n");
            if (data != null) System.IO.File.WriteAllText(Path.Combine(old, "Data.lua"), data);
            return old;
        }

        /// <summary>Every file in a folder with its text, to show it was not touched.</summary>
        private static string Snapshot(string folder) =>
            string.Join("\n", Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal).Select(f => Path.GetFileName(f) + "=" + System.IO.File.ReadAllText(f)));

        private static void NothingElseTouched(string addons, params string[] alsoThere)
        {
            Assert.Equal("not ours", System.IO.File.ReadAllText(Path.Combine(addons, "SomeoneElse", "Other.lua")));
            string[] left = Directory.GetDirectories(addons).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            Assert.All(left, n => Assert.True(n == "SomeoneElse" || n == "Crucible" || alsoThere.Contains(n), "left behind: " + n));
        }

        [Fact]
        public void A_fresh_install_writes_every_file_and_creates_the_folder()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);

            AddonInstaller.Install(target, Good());

            Assert.Equal(new[] { "Crucible.toc", "Data.lua", "Logic.lua" }, Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(Toc, System.IO.File.ReadAllText(Path.Combine(target, "Crucible.toc")));
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(target));
            NothingElseTouched(addons);
        }

        [Fact]
        public void An_update_replaces_the_code_KEEPS_THE_PLAYERS_PRICES_and_drops_what_the_new_version_removed()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: true);
            string prices = System.IO.File.ReadAllText(Path.Combine(target, "Data.lua"));

            AddonInstaller.Install(target, Good());

            Assert.Equal("local _, ns = ...\nns.L = {}\n", System.IO.File.ReadAllText(Path.Combine(target, "Logic.lua")));
            Assert.Equal(prices, System.IO.File.ReadAllText(Path.Combine(target, "Data.lua"))); // NOT the manifest's empty one
            Assert.False(System.IO.File.Exists(Path.Combine(target, "Gone.lua")));
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(target));
            NothingElseTouched(addons);
        }

        [Theory]
        [InlineData("../evil.lua")]
        [InlineData("..\\evil.lua")]
        [InlineData("sub/Logic.lua")]
        [InlineData("sub\\Logic.lua")]
        [InlineData("C:\\windows\\evil.lua")]
        [InlineData("/etc/passwd")]
        [InlineData("Logic.exe")]
        [InlineData("Logic.lua.bak")]
        [InlineData(".hidden.lua")]
        [InlineData("")]
        [InlineData(".lua")]
        [InlineData("Logic .lua")]
        [InlineData("Logic\0.lua")]
        public void A_name_that_is_not_a_plain_addon_file_is_refused_whole(string name)
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: true);
            string before = System.IO.File.ReadAllText(Path.Combine(target, "Logic.lua"));

            AddonManifest m = Good(File_(name, "payload"));
            Assert.NotNull(AddonManifest.Reject(m));
            Assert.ThrowsAny<Exception>(() => AddonInstaller.Install(target, m));

            Assert.Equal(before, System.IO.File.ReadAllText(Path.Combine(target, "Logic.lua")));
            NothingElseTouched(addons);
        }

        [Fact]
        public void A_manifest_that_does_not_add_up_is_refused_whole()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: true);
            string before = System.IO.File.ReadAllText(Path.Combine(target, "Logic.lua"));

            var bad = new List<AddonManifest>
            {
                new AddonManifest { Version = "0.9.0", Files = new List<AddonFile>() },                    // nothing in it
                new AddonManifest { Version = "0.9.0", Files = new List<AddonFile> { File_("Logic.lua", "x") } }, // no .toc
                Good(File_("Second.toc", Toc)),                                                            // two .toc files
                Good(File_("Logic.lua", "a different Logic")),                                             // a duplicate name
            };
            var tampered = Good();
            tampered.Files[1].Content = "swapped after the hash was taken";                                 // hash does not match
            bad.Add(tampered);
            var big = Good();
            big.Files[1].Content = new string('x', AddonInstaller.MaxFileBytes + 1);
            big.Files[1].Sha256 = Sha(big.Files[1].Content);
            bad.Add(big);                                                                                   // one file too large
            var many = Good();
            for (int i = 0; i < AddonInstaller.MaxFiles; i++) many.Files.Add(File_("Filler" + i + ".lua", "x"));
            bad.Add(many);                                                                                  // too many files
            bad.Add(new AddonManifest { Version = "", Files = Good().Files });                              // no version

            foreach (AddonManifest m in bad)
            {
                Assert.NotNull(AddonManifest.Reject(m));
                Assert.ThrowsAny<Exception>(() => AddonInstaller.Install(target, m));
            }
            Assert.NotNull(AddonManifest.Reject(null)); // no manifest at all is refused too

            Assert.Equal(before, System.IO.File.ReadAllText(Path.Combine(target, "Logic.lua")));
            Assert.Equal(4, Directory.GetFiles(target).Length); // still the four it had
            NothingElseTouched(addons);
        }

        [Fact]
        public void A_refusal_leaves_no_half_written_folder_behind()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: true);
            try { AddonInstaller.Install(target, Good(File_("../evil.lua", "x"))); } catch (Exception) { }
            Assert.DoesNotContain(Directory.GetDirectories(addons), d => Path.GetFileName(d)!.Contains(".new-") || Path.GetFileName(d)!.Contains(".old-"));
            NothingElseTouched(addons);
        }

        [Fact]
        public void The_installed_version_is_read_from_the_toc_and_is_null_when_there_is_no_addon()
        {
            using var dir = new TempDir();
            var (_, target) = AddOns(dir, installed: true);
            Assert.Equal("0.8.0", AddonInstaller.InstalledVersion(target));
            Assert.Null(AddonInstaller.InstalledVersion(Path.Combine(dir.Path, "nowhere")));
            System.IO.File.WriteAllText(Path.Combine(target, "Crucible.toc"), "## Title: Crucible\n");
            Assert.Null(AddonInstaller.InstalledVersion(target));
        }

        [Fact]
        public void A_good_manifest_is_not_refused()
        {
            Assert.Null(AddonManifest.Reject(Good()));
        }

        // The rename (2026-10-04): the addon was Tallybook, in Interface\AddOns\Tallybook. Remove these at 1.0.0 with the code.

        [Fact]
        public void A_first_install_beside_our_old_Tallybook_folder_carries_its_Data_lua_across_and_removes_it()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);
            string old = OldFolder(addons);

            string said = AddonInstaller.Install(target, Good());

            Assert.Equal(OldPrices, System.IO.File.ReadAllText(Path.Combine(target, "Data.lua"))); // NOT the manifest's empty one
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(target));
            Assert.False(Directory.Exists(old));
            Assert.Contains("removed the old Tallybook folder", said);
            NothingElseTouched(addons);
        }

        [Fact]
        public void An_update_keeps_the_Crucible_folders_own_Data_lua_and_still_removes_the_old_folder()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: true);
            string mine = System.IO.File.ReadAllText(Path.Combine(target, "Data.lua"));
            string old = OldFolder(addons);

            AddonInstaller.Install(target, Good());

            Assert.Equal(mine, System.IO.File.ReadAllText(Path.Combine(target, "Data.lua")));
            Assert.False(Directory.Exists(old));
            NothingElseTouched(addons);
        }

        [Fact]
        public void Our_old_folder_with_no_Data_lua_is_still_removed_and_the_manifests_empty_one_stays()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);
            string old = OldFolder(addons, data: null);

            AddonInstaller.Install(target, Good());

            Assert.Equal(EmptyData, System.IO.File.ReadAllText(Path.Combine(target, "Data.lua")));
            Assert.False(Directory.Exists(old));
            NothingElseTouched(addons);
        }

        [Theory]
        [InlineData(NoToc)]                                                                                   // nothing says whose it is
        [InlineData("")]
        [InlineData("## Interface: 16001\n## Title: Tallybook\n## Notes: A tally of your kills.\n## SavedVariables: TallybookDB\n")] // somebody else's Tallybook
        [InlineData("## Interface: 16001\n## Title: Tallybook\n## Notes: Auction house price notebook.\n## SavedVariables: TallyDB\n")] // our words, not our saved variable
        [InlineData("## Interface: 16001\n## Title: Tallybook\n## SavedVariables: TallybookDB\n")]           // our saved variable alone is not enough
        public void A_Tallybook_folder_whose_toc_is_not_ours_is_never_touched_and_nothing_is_taken_from_it(string toc)
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);
            string old = OldFolder(addons, toc);
            string before = Snapshot(old);

            string said = AddonInstaller.Install(target, Good());

            Assert.Equal(before, Snapshot(old));
            Assert.Equal(EmptyData, System.IO.File.ReadAllText(Path.Combine(target, "Data.lua")));
            Assert.DoesNotContain("Tallybook", said);
            NothingElseTouched(addons, "Tallybook");
        }

        [Fact]
        public void A_Tallybook_folder_holding_only_a_Crucible_toc_is_not_ours_to_remove()
        {
            // What an old tray app may leave behind when a server hands it the renamed addon. The game does not load it.
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);
            string old = OldFolder(addons, NoToc);
            System.IO.File.WriteAllText(Path.Combine(old, "Crucible.toc"), Toc);
            string before = Snapshot(old);

            AddonInstaller.Install(target, Good());

            Assert.Equal(before, Snapshot(old));
            NothingElseTouched(addons, "Tallybook");
        }

        [Fact]
        public void A_refused_manifest_leaves_the_old_folder_exactly_as_it_was()
        {
            using var dir = new TempDir();
            var (addons, target) = AddOns(dir, installed: false);
            string old = OldFolder(addons);
            string before = Snapshot(old);

            Assert.ThrowsAny<Exception>(() => AddonInstaller.Install(target, Good(File_("../evil.lua", "x"))));

            Assert.Equal(before, Snapshot(old));
            Assert.False(Directory.Exists(target));
            NothingElseTouched(addons, "Tallybook");
        }

        [Fact]
        public void The_old_folder_is_retired_only_beside_the_Crucible_folder()
        {
            using var dir = new TempDir();
            var (addons, _) = AddOns(dir, installed: false);
            string old = OldFolder(addons);
            string before = Snapshot(old);

            AddonInstaller.Install(Path.Combine(addons, "Elsewhere"), Good());

            Assert.Equal(before, Snapshot(old));
            Assert.Equal(EmptyData, System.IO.File.ReadAllText(Path.Combine(addons, "Elsewhere", "Data.lua")));
            NothingElseTouched(addons, "Tallybook", "Elsewhere");
        }
    }
}
