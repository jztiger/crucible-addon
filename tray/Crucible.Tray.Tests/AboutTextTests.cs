using System;
using System.Threading.Tasks;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>
    /// Help > About Crucible (owner 2026-10-18, spec 2026-10-17 section 11): what the window says is decided in the core - the two
    /// version lines and the newest entries of the changelog - and the addon version the server offers rides on the cycle's report.
    /// </summary>
    public class AboutTextTests
    {
        // Three entries in the file's own shape: a header, a blank line, then "version - text" with a 9-space hanging indent.
        // The versions are not substrings of one another, so a "does not contain" cannot pass by accident.
        private const string ThreeEntries =
            "Crucible changelog - newest first.\n"
            + "\n"
            + "3.0.0 - the third thing.\n"
            + "         It goes on over a second line.\n"
            + "2.0.0 - the second thing.\n"
            + "1.0.0 - the first thing.\n";

        [Fact] public void VersionsSayWhenNothingHasBeenCheckedYet() =>
            Assert.Contains("Crucible tray app 0.100.0 (newest not checked yet)", AboutText.Versions("0.100.0", null, "0.99.8", "0.99.8"));

        [Fact] public void VersionsSayWhenTheAddonIsNotInstalled() =>
            Assert.Contains("Crucible addon not installed", AboutText.Versions("0.100.0", "0.100.0", null, "0.99.8"));

        [Fact] public void VersionsAreTwoLinesWithTheNewestVersionsBeside()
        {
            Assert.Equal("Crucible tray app 0.100.0 (newest 0.100.1)\nCrucible addon 0.99.8 (newest 0.99.9)",
                AboutText.Versions("0.100.0", "0.100.1", "0.99.8", "0.99.9"));
            Assert.Equal("Crucible tray app 0.100.0 (newest not checked yet)\nCrucible addon not installed (newest not checked yet)",
                AboutText.Versions("0.100.0", null, null, null));
        }

        [Fact] public void ReleaseNotesKeepTheNewestEntriesOnly()
        {
            string notes = AboutText.ReleaseNotes(ThreeEntries, 2);

            // The first two entries as the file has them - the indented line stays with its entry - and no third.
            Assert.Equal("3.0.0 - the third thing.\n         It goes on over a second line.\n2.0.0 - the second thing.", notes);
            Assert.DoesNotContain("1.0.0", notes);
        }

        [Fact] public void ReleaseNotesLeaveTheHeaderOut() =>
            Assert.DoesNotContain("newest first", AboutText.ReleaseNotes(ThreeEntries, 2));

        [Theory]
        [InlineData(3)]    // exactly as many as there are
        [InlineData(10)]   // more than there are
        public void ReleaseNotesGiveEveryEntryWhenThereAreNoMoreThanAsked(int asked)
        {
            string all = AboutText.ReleaseNotes(ThreeEntries, asked);

            // From the fixture's own first line to its own last one, nothing else: not the header, not the file's final newline.
            Assert.StartsWith("3.0.0 - the third thing.", all, StringComparison.Ordinal);
            Assert.Contains("\n2.0.0 - the second thing.\n", all, StringComparison.Ordinal);
            Assert.EndsWith("\n1.0.0 - the first thing.", all, StringComparison.Ordinal);
        }

        [Fact] public void ReleaseNotesReadAChangelogWithWindowsLineEndings()
        {
            string notes = AboutText.ReleaseNotes(ThreeEntries.Replace("\n", "\r\n"), 2);

            Assert.DoesNotContain("\r", notes);
            Assert.Equal(AboutText.ReleaseNotes(ThreeEntries, 2), notes);
        }

        [Theory]
        [InlineData("", 5)]
        [InlineData("a header and nothing under it\n", 5)]
        [InlineData("3.0.0 - the third thing.\n", 0)]
        [InlineData("3.0.0 - the third thing.\n", -1)]
        public void ReleaseNotesOfNothingAreNothing(string changelog, int entries) => Assert.Equal("", AboutText.ReleaseNotes(changelog, entries));

        [Fact] public async Task TheCycleReportCarriesTheOfferedAddonVersion()
        {
            using var w = new World { AddonVersion = "0.99.8" };

            CycleReport r = await w.Cycle.RunAsync(false);

            Assert.Equal("0.99.8", r.AddonOffered);
        }

        [Fact] public async Task TheOfferedAddonVersionIsReportedWhenItIsAlreadyInstalled()
        {
            using var w = new World();   // 0.9.0 is installed and 0.9.0 is on offer

            CycleReport r = await w.Cycle.RunAsync(false);

            Assert.Null(r.AddonInstalled);
            Assert.Equal("0.9.0", r.AddonOffered);
        }

        [Fact] public async Task TheOfferedAddonVersionIsReportedWithUpdatesSwitchedOff()
        {
            // The manifest is read on the same cadence with updates off (it is where the product folders come from), and a
            // member who keeps the addon by hand still wants to see how far behind it is.
            using var w = new World { AddonVersion = "0.99.8" };
            w.Config.KeepAddonUpToDate = false;

            CycleReport r = await w.Cycle.RunAsync(false);

            Assert.Null(r.AddonInstalled);
            Assert.Equal("0.99.8", r.AddonOffered);
        }

        [Fact] public async Task APassThatReadNoManifestReportsNoOfferedAddonVersion()
        {
            // The shell keeps the last version it was told; a null here means "nothing learned this pass", never "none on offer".
            using var w = new World { AddonVersion = "0.99.8" };
            Assert.Equal("0.99.8", (await w.Cycle.RunAsync(false)).AddonOffered);

            w.Now = w.Now.AddMinutes(5);
            Assert.Null((await w.Cycle.RunAsync(false)).AddonOffered);    // not asked again so soon

            Assert.Null((await w.Cycle.RunAsync(true)).AddonOffered);     // asked, and answered 304 Not Modified
        }

        [Fact] public async Task AnAddonTheServerCannotOfferIsNotReportedAsOffered()
        {
            using var w = new World { AddonStatus = 503 };

            CycleReport r = await w.Cycle.RunAsync(false);

            Assert.Null(r.AddonOffered);
        }
    }
}
