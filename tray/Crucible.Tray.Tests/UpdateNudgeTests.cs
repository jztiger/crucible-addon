using System;
using System.IO;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>
    /// M112: a newer tray app on offer is shown - a balloon once per new version, a tooltip line, a badge, a menu line
    /// to the fixed download page - and never fetched (C3).
    /// </summary>
    public class UpdateNudgeTests
    {
        [Fact]
        public void Nothing_newer_on_offer_shows_nothing()
        {
            var nudge = new UpdateNudge("0.4.0", null);
            Assert.False(nudge.Offer("0.4.0"));
            Assert.Null(nudge.Available);
            Assert.Null(nudge.TooltipLine);
            Assert.Null(nudge.MenuText);
            Assert.False(nudge.Offer("0.3.9"));
            Assert.Null(nudge.Available);
        }

        [Fact]
        public void A_new_version_shows_a_balloon_once_and_stays_in_the_tooltip_and_the_menu()
        {
            var nudge = new UpdateNudge("0.4.0", null);
            Assert.True(nudge.Offer("0.4.1"));
            Assert.Equal("0.4.1", nudge.Available);
            Assert.Equal("0.4.1", nudge.Nudged); // for the config, so a restart does not show the balloon again
            Assert.Equal("Update available: 0.4.1", nudge.TooltipLine);
            Assert.Contains("0.4.1", nudge.MenuText);
            Assert.Contains("0.4.1", nudge.BalloonText);
            Assert.Equal("Download Crucible 0.4.1 (opens the download page)", nudge.MenuText);
            Assert.StartsWith("Crucible 0.4.1 is ready to download.", nudge.BalloonText);
            Assert.DoesNotContain("Tallybook", nudge.BalloonText);

            Assert.False(nudge.Offer("0.4.1")); // the next day's check: the same version, no second balloon
            Assert.Equal("0.4.1", nudge.Available);
            Assert.Equal("Update available: 0.4.1", nudge.TooltipLine);
        }

        [Fact]
        public void After_a_restart_the_version_already_nudged_gets_no_balloon_but_is_still_shown()
        {
            var nudge = new UpdateNudge("0.4.0", "0.4.1");
            Assert.False(nudge.Offer("0.4.1"));
            Assert.Equal("0.4.1", nudge.Available);
            Assert.Equal("Update available: 0.4.1", nudge.TooltipLine);
        }

        [Fact]
        public void A_later_version_gets_its_own_balloon_and_a_withdrawn_one_none()
        {
            var nudge = new UpdateNudge("0.4.0", "0.4.1");
            Assert.True(nudge.Offer("0.5.0"));
            Assert.Equal("0.5.0", nudge.Nudged);
            Assert.False(nudge.Offer("0.4.2")); // the server went back to an older build: shown, not ballooned again
            Assert.Equal("0.4.2", nudge.Available);
            Assert.Equal("0.5.0", nudge.Nudged);
        }

        [Fact]
        public void No_answer_from_the_server_keeps_what_was_known()
        {
            var nudge = new UpdateNudge("0.4.0", null);
            nudge.Offer("0.4.1");
            Assert.False(nudge.Offer(null));
            Assert.Equal("0.4.1", nudge.Available);
        }

        [Fact]
        public void Once_updated_the_nudge_is_gone()
        {
            var nudge = new UpdateNudge("0.4.1", "0.4.1"); // the new build, after the person installed it
            Assert.False(nudge.Offer("0.4.1"));
            Assert.Null(nudge.Available);
            Assert.Null(nudge.TooltipLine);
        }

        [Theory]
        [InlineData("")]
        [InlineData("0.4")]
        [InlineData("..\\evil")]
        [InlineData("0.4.1 ")]
        [InlineData("0.4.1\n")]
        public void A_remembered_version_that_is_not_one_counts_as_none(string remembered)
        {
            var nudge = new UpdateNudge("0.4.0", remembered);
            Assert.Null(nudge.Nudged);
            Assert.True(nudge.Offer("0.4.1"));
        }

        [Theory]
        [InlineData("https://crucible.example.com", "https://crucible.example.com/setup")]
        [InlineData("https://crucible.example.com/", "https://crucible.example.com/setup")]
        [InlineData("http://127.0.0.1:8443", "http://127.0.0.1:8443/setup")]
        public void The_download_page_is_the_website_from_the_settings_file_plus_setup(string ui, string expected)
        {
            Assert.Equal(expected, UpdateNudge.DownloadPage(ui));
        }

        [Fact]
        public void The_remembered_version_survives_a_round_trip_and_a_hand_edited_one_does_not()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            var p = new FakeProtector();
            TrayConfig c = ServerClientTests.Config();
            Assert.Equal("", c.NudgedVersion);
            c.NudgedVersion = "0.4.1";
            ConfigStore.Save(path, c, p);
            Assert.Equal("0.4.1", ConfigStore.Load(path, p)!.NudgedVersion);

            File.WriteAllText(path, File.ReadAllText(path).Replace("\"0.4.1\"", "\"C:\\\\x\""));
            TrayConfig? back = ConfigStore.Load(path, p);
            Assert.NotNull(back);
            Assert.Equal("", back!.NudgedVersion);
        }
    }

    /// <summary>The tooltip: Windows takes at most 63 characters, and the update line, when there is one, goes first.</summary>
    public class TrayTextTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Up_to_date_says_when_it_last_sent_and_how_old_the_prices_are()
        {
            Assert.Equal("Crucible - up to date. Sent 3m ago; prices 2h ago",
                TrayText.Tooltip(TrayState.Ok, "", Now.AddMinutes(-3), Now.AddHours(-2), null, Now));
            Assert.Equal("Crucible - up to date", TrayText.Tooltip(TrayState.Ok, "", null, null, null, Now));
        }

        [Fact]
        public void Any_other_state_says_its_reason()
        {
            Assert.Equal("Crucible - Paused", TrayText.Tooltip(TrayState.Paused, "Paused", null, null, null, Now));
        }

        [Fact]
        public void An_update_is_the_first_line_and_the_whole_stays_within_63_characters()
        {
            string text = TrayText.Tooltip(TrayState.Ok, "", Now.AddMinutes(-3), Now.AddHours(-2), "Update available: 0.4.1", Now);
            Assert.StartsWith("Update available: 0.4.1\nCrucible - up to date", text);
            Assert.True(text.Length <= 63, text.Length + ": " + text);
            Assert.EndsWith("…", text);

            string longReason = new string('x', 200);
            Assert.True(TrayText.Tooltip(TrayState.Retrying, longReason, null, null, null, Now).Length <= 63);
        }
    }
}
