using System;
using System.IO;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>Stands in for Windows' DPAPI: reversible, recognisable, and it refuses what it did not make.</summary>
    internal sealed class FakeProtector : ISecretProtector
    {
        /// <summary>Like DPAPI's entropy: a protector with another tag cannot open what this one sealed.</summary>
        private readonly string open;

        public FakeProtector(string tag = "") { open = "fake" + tag + "["; }

        public string Protect(string plain)
        {
            char[] c = plain.ToCharArray();
            Array.Reverse(c);
            return open + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(new string(c))) + "]";
        }

        public string Unprotect(string sealedText)
        {
            if (!sealedText.StartsWith(open, StringComparison.Ordinal) || !sealedText.EndsWith("]", StringComparison.Ordinal))
                throw new InvalidOperationException("not mine");
            char[] c = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(sealedText.Substring(open.Length, sealedText.Length - open.Length - 1))).ToCharArray();
            Array.Reverse(c);
            return new string(c);
        }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "crucible-test-" + Guid.NewGuid().ToString("N"));
        public TempDir() { Directory.CreateDirectory(Path); }
        public string File(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
    }

    public class ConfigStoreTests
    {
        private const string Download = "{\n  \"api\": \"https://crucible-api.example.com\",\n  \"ui\": \"https://crucible.example.com\",\n"
            + "  \"clientId\": \"abc123.access\",\n  \"clientSecret\": \"s3cr3t-client-secret\",\n  \"uploadKey\": \"upl0ad-key-43-chars\"\n}\n";

        [Fact]
        public void The_servers_download_imports()
        {
            TrayConfig? c = ConfigStore.ImportDownload(Download);
            Assert.NotNull(c);
            Assert.Equal("https://crucible-api.example.com", c!.Api);
            Assert.Equal("https://crucible.example.com", c.Ui);
            Assert.Equal("abc123.access", c.ClientId);
            Assert.Equal("s3cr3t-client-secret", c.ClientSecret);
            Assert.Equal("upl0ad-key-43-chars", c.UploadKey);
            Assert.True(ConfigStore.IsComplete(c));
        }

        [Theory]
        [InlineData("")]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("{\"api\":\"https://a.example.com\"}")]
        [InlineData("{\"api\":\"http://crucible-api.example.com\",\"ui\":\"https://crucible.example.com\",\"clientId\":\"a\",\"clientSecret\":\"b\",\"uploadKey\":\"c\"}")]
        [InlineData("{\"api\":\"https://crucible-api.example.com/path\",\"ui\":\"https://crucible.example.com\",\"clientId\":\"a\",\"clientSecret\":\"b\",\"uploadKey\":\"c\"}")]
        public void What_is_not_a_complete_https_download_does_not_import(string json)
        {
            Assert.Null(ConfigStore.ImportDownload(json));
        }

        private static string DownloadWithApi(string api) => Download.Replace("https://crucible-api.example.com", api);

        [Theory]
        [InlineData("http://127.0.0.1:3102")]
        [InlineData("http://localhost:3102")]
        public void A_loopback_address_may_be_plain_http_for_the_rehearsal_on_one_pc(string api)
        {
            TrayConfig? c = ConfigStore.ImportDownload(DownloadWithApi(api));
            Assert.NotNull(c);
            Assert.Equal(api, c!.Api);
        }

        [Theory]
        [InlineData("http://10.0.0.5:3000")]  // any LAN address: the credentials must never cross a network in the clear
        [InlineData("http://crucible.example.com")]
        [InlineData("http://127.0.0.1.evil.example")]
        [InlineData("http://localhost.evil.example:3102")]
        [InlineData("ftp://127.0.0.1")]
        public void Nothing_else_may_travel_in_the_clear(string api)
        {
            Assert.Null(ConfigStore.ImportDownload(DownloadWithApi(api)));
        }

        [Fact]
        public void Defaults_are_both_switches_on_and_the_notice_not_accepted()
        {
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            Assert.True(c.BringDataBack);
            Assert.True(c.StartWithWindows);
            Assert.Equal(0, c.AcceptedNoticeVersion);
            Assert.False(c.Paused);
            Assert.Equal("", c.WowFolder);
        }

        [Fact]
        public void Save_then_load_round_trips_and_no_secret_is_on_disk_in_the_clear()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            c.WowFolder = @"D:\Games\World of Warcraft";
            c.AcceptedNoticeVersion = 2;
            c.BringDataBack = false;
            ConfigStore.Save(path, c, new FakeProtector());

            string onDisk = File.ReadAllText(path);
            Assert.DoesNotContain("s3cr3t-client-secret", onDisk);
            Assert.DoesNotContain("upl0ad-key-43-chars", onDisk);
            Assert.Contains("p1:fake[", onDisk);
            Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp*"));

            TrayConfig? back = ConfigStore.Load(path, new FakeProtector());
            Assert.NotNull(back);
            Assert.Equal("s3cr3t-client-secret", back!.ClientSecret);
            Assert.Equal("upl0ad-key-43-chars", back.UploadKey);
            Assert.Equal(@"D:\Games\World of Warcraft", back.WowFolder);
            Assert.Equal(2, back.AcceptedNoticeVersion);
            Assert.False(back.BringDataBack);
            Assert.True(back.StartWithWindows);
        }

        [Fact]
        public void No_file_is_no_config()
        {
            using var dir = new TempDir();
            Assert.Null(ConfigStore.Load(dir.File("config.json"), new FakeProtector()));
        }

        [Fact]
        public void A_tampered_or_foreign_secret_is_no_config_not_a_crash()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Save(path, ConfigStore.ImportDownload(Download)!, new FakeProtector());
            File.WriteAllText(path, File.ReadAllText(path).Replace("p1:fake[", "p1:other["));
            Assert.Null(ConfigStore.Load(path, new FakeProtector()));

            File.WriteAllText(path, "{ this is not json");
            Assert.Null(ConfigStore.Load(path, new FakeProtector()));
        }

        [Fact]
        public void A_new_settings_file_replaces_the_credentials_and_keeps_everything_the_person_chose()
        {
            TrayConfig mine = ConfigStore.ImportDownload(Download)!;
            mine.WowFolder = @"D:\Games\World of Warcraft";
            mine.AcceptedNoticeVersion = 2;
            mine.BringDataBack = false;
            mine.StartWithWindows = false;
            mine.Paused = true;

            TrayConfig fresh = ConfigStore.ImportDownload(Download.Replace("upl0ad-key-43-chars", "a-brand-new-upload-key").Replace("s3cr3t-client-secret", "rotated-secret"))!;
            ConfigStore.ApplyCredentials(mine, fresh);

            Assert.Equal("a-brand-new-upload-key", mine.UploadKey);
            Assert.Equal("rotated-secret", mine.ClientSecret);
            Assert.Equal(fresh.Api, mine.Api);
            Assert.Equal(fresh.Ui, mine.Ui);
            Assert.Equal(fresh.ClientId, mine.ClientId);
            Assert.Equal(@"D:\Games\World of Warcraft", mine.WowFolder);
            Assert.Equal(2, mine.AcceptedNoticeVersion);
            Assert.False(mine.BringDataBack);
            Assert.False(mine.StartWithWindows);
            Assert.True(mine.Paused);
        }

        // The rename (2026-10-04, remove at 1.0.0 with the code): Tallybook kept its config in %APPDATA%\Tallybook,
        // sealed with its own DPAPI entropy. Two protectors with different tags stand for the two.

        private static TrayConfig Chosen()
        {
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            c.WowFolder = @"D:\Games\World of Warcraft";
            c.AcceptedNoticeVersion = 4;
            c.BringDataBack = false;
            c.KeepAddonUpToDate = false;
            c.StartWithWindows = false;
            c.Paused = true;
            c.Products = new[] { "_classic_forever_", "_classic_beta_" };
            c.NudgedVersion = "0.99.1";
            return c;
        }

        [Fact]
        public void The_failed_update_version_round_trips_and_junk_reads_as_none()
        {
            using var dir = new TempDir();
            string file = dir.File("config.json");
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            c.FailedUpdateVersion = "0.99.5";
            ConfigStore.Save(file, c, new FakeProtector());
            Assert.Equal("0.99.5", ConfigStore.Load(file, new FakeProtector())!.FailedUpdateVersion);
            Assert.Contains("\"failedUpdate\": \"0.99.5\"", System.IO.File.ReadAllText(file)); // the store pretty-prints
            string text = System.IO.File.ReadAllText(file).Replace("\"failedUpdate\": \"0.99.5\"", "\"failedUpdate\": \"C:\\\\evil\"");
            System.IO.File.WriteAllText(file, text);
            Assert.Equal("", ConfigStore.Load(file, new FakeProtector())!.FailedUpdateVersion);
        }

        [Fact]
        public void The_first_run_after_the_rename_takes_Tallybooks_config_once_and_leaves_the_old_file_where_it_was()
        {
            using var dir = new TempDir();
            string oldPath = System.IO.Path.Combine(dir.Path, "Tallybook", "config.json");
            string newPath = System.IO.Path.Combine(dir.Path, "Crucible", "config.json");
            var oldSeal = new FakeProtector("-tallybook");
            var newSeal = new FakeProtector("-crucible");
            ConfigStore.Save(oldPath, Chosen(), oldSeal);
            byte[] oldBytes = File.ReadAllBytes(oldPath);

            TrayConfig? moved = ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal);

            Assert.NotNull(moved);
            Assert.Equal(oldBytes, File.ReadAllBytes(oldPath)); // left in place, untouched (deleted at 1.0.0)
            Assert.Null(ConfigStore.Load(newPath, oldSeal));    // sealed again, with the new name's protector
            TrayConfig back = ConfigStore.Load(newPath, newSeal)!;
            Assert.NotNull(back);
            foreach (TrayConfig c in new[] { moved!, back })
            {
                Assert.Equal("s3cr3t-client-secret", c.ClientSecret);
                Assert.Equal("upl0ad-key-43-chars", c.UploadKey);
                Assert.Equal("abc123.access", c.ClientId);
                Assert.Equal("https://crucible-api.example.com", c.Api);
                Assert.Equal("https://crucible.example.com", c.Ui);
                Assert.Equal(@"D:\Games\World of Warcraft", c.WowFolder);
                Assert.Equal(4, c.AcceptedNoticeVersion);
                Assert.False(c.BringDataBack);
                Assert.False(c.KeepAddonUpToDate);
                Assert.False(c.StartWithWindows);
                Assert.True(c.Paused);
                Assert.Equal(new[] { "_classic_forever_", "_classic_beta_" }, c.Products);
                Assert.Equal("0.99.1", c.NudgedVersion);
            }
            Assert.DoesNotContain("s3cr3t-client-secret", File.ReadAllText(newPath));
        }

        [Fact]
        public void Nothing_is_taken_once_there_is_a_Crucible_config_not_even_over_a_damaged_one()
        {
            using var dir = new TempDir();
            string oldPath = System.IO.Path.Combine(dir.Path, "Tallybook", "config.json");
            string newPath = System.IO.Path.Combine(dir.Path, "Crucible", "config.json");
            var oldSeal = new FakeProtector("-tallybook");
            var newSeal = new FakeProtector("-crucible");
            ConfigStore.Save(oldPath, Chosen(), oldSeal);

            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(newPath)!);
            File.WriteAllText(newPath, "{ damaged");
            Assert.Null(ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal));
            Assert.Equal("{ damaged", File.ReadAllText(newPath)); // older settings never overwrite the new name's file

            TrayConfig mine = ConfigStore.ImportDownload(Download)!;
            ConfigStore.Save(newPath, mine, newSeal);
            string before = File.ReadAllText(newPath);
            Assert.Null(ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal));
            Assert.Equal(before, File.ReadAllText(newPath));
        }

        [Fact]
        public void No_old_config_or_one_that_cannot_be_opened_moves_nothing_and_writes_nothing()
        {
            using var dir = new TempDir();
            string oldPath = System.IO.Path.Combine(dir.Path, "Tallybook", "config.json");
            string newPath = System.IO.Path.Combine(dir.Path, "Crucible", "config.json");
            var oldSeal = new FakeProtector("-tallybook");
            var newSeal = new FakeProtector("-crucible");

            Assert.Null(ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal));
            Assert.False(File.Exists(newPath));

            // Sealed by somebody else (another Windows user, say): not a config, and nothing is written.
            ConfigStore.Save(oldPath, Chosen(), new FakeProtector("-another-user"));
            Assert.Null(ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal));
            Assert.False(File.Exists(newPath));
        }

        private const string OldUi = "https://tally.tigerforge.io";
        private const string OldApi = "https://tally-api.tigerforge.io";

        private static TrayConfig WithHosts(string api, string ui)
        {
            TrayConfig c = Chosen();
            c.Api = api;
            c.Ui = ui;
            return c;
        }

        [Fact]
        public void The_move_from_Tallybook_replaces_the_two_old_hosts_and_touches_nothing_else()
        {
            using var dir = new TempDir();
            string oldPath = System.IO.Path.Combine(dir.Path, "Tallybook", "config.json");
            string newPath = System.IO.Path.Combine(dir.Path, "Crucible", "config.json");
            var oldSeal = new FakeProtector("-tallybook");
            var newSeal = new FakeProtector("-crucible");
            ConfigStore.Save(oldPath, WithHosts(OldApi, OldUi), oldSeal);

            TrayConfig moved = ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal)!;
            TrayConfig back = ConfigStore.Load(newPath, newSeal)!;

            foreach (TrayConfig c in new[] { moved, back })
            {
                Assert.Equal("https://crucible-api.tigerforge.io", c.Api);
                Assert.Equal("https://crucible.tigerforge.io", c.Ui);
                Assert.Equal("upl0ad-key-43-chars", c.UploadKey);
                Assert.Equal("s3cr3t-client-secret", c.ClientSecret);
                Assert.Equal(@"D:\Games\World of Warcraft", c.WowFolder);
            }
            Assert.Equal(OldUi, ConfigStore.Load(oldPath, oldSeal)!.Ui); // the old file is left as it was
        }

        [Fact]
        public void The_move_from_Tallybook_leaves_another_host_port_and_scheme_alone()
        {
            using var dir = new TempDir();
            string oldPath = System.IO.Path.Combine(dir.Path, "Tallybook", "config.json");
            string newPath = System.IO.Path.Combine(dir.Path, "Crucible", "config.json");
            var oldSeal = new FakeProtector("-tallybook");
            var newSeal = new FakeProtector("-crucible");
            ConfigStore.Save(oldPath, WithHosts("https://api.example.com", "https://tally.tigerforge.io:8443"), oldSeal);

            TrayConfig moved = ConfigStore.MoveFromOldName(oldPath, oldSeal, newPath, newSeal)!;

            Assert.Equal("https://api.example.com", moved.Api);
            Assert.Equal("https://crucible.tigerforge.io:8443", moved.Ui);
        }

        [Fact]
        public void A_host_that_only_contains_an_old_one_is_not_replaced()
        {
            TrayConfig c = WithHosts("https://not-tally-api.tigerforge.io", "https://tally.tigerforge.io.example.com");
            Assert.False(ConfigStore.ReplaceOldHosts(c, false));
            Assert.Equal("https://not-tally-api.tigerforge.io", c.Api);
            Assert.Equal("https://tally.tigerforge.io.example.com", c.Ui);
        }

        [Fact]
        public void A_config_that_already_went_across_with_exactly_the_two_old_hosts_is_rewritten_once()
        {
            TrayConfig c = WithHosts(OldApi, OldUi);
            Assert.True(ConfigStore.ReplaceOldHosts(c, true));
            Assert.Equal("https://crucible-api.tigerforge.io", c.Api);
            Assert.Equal("https://crucible.tigerforge.io", c.Ui);
            Assert.Equal("upl0ad-key-43-chars", c.UploadKey);
            Assert.False(ConfigStore.ReplaceOldHosts(c, true)); // nothing fires again
        }

        [Theory]
        [InlineData("https://crucible-api.tigerforge.io", "https://crucible.tigerforge.io")]
        [InlineData("https://tally-api.tigerforge.io", "https://crucible.tigerforge.io")]
        [InlineData("https://api.example.com", "https://tally.tigerforge.io")]
        public void At_startup_nothing_changes_unless_both_hosts_are_exactly_the_old_two(string api, string ui)
        {
            TrayConfig c = WithHosts(api, ui);
            Assert.False(ConfigStore.ReplaceOldHosts(c, true));
            Assert.Equal(api, c.Api);
            Assert.Equal(ui, c.Ui);
        }

        [Fact]
        public void A_settings_file_the_member_loads_is_not_rewritten()
        {
            string json = Download.Replace("https://crucible-api.example.com", OldApi).Replace("https://crucible.example.com", OldUi);
            TrayConfig c = ConfigStore.ImportDownload(json)!;
            Assert.Equal(OldApi, c.Api);
            Assert.Equal(OldUi, c.Ui);
            TrayConfig saved = Chosen();
            ConfigStore.ApplyCredentials(saved, c);
            Assert.Equal(OldApi, saved.Api);
            Assert.Equal(OldUi, saved.Ui);
        }

        [Fact]
        public void Unknown_fields_are_ignored()
        {
            string json = Download.Replace("{\n", "{\n  \"somethingNew\": 5,\n");
            Assert.NotNull(ConfigStore.ImportDownload(json));
        }

        [Fact]
        public void A_config_written_before_the_notice_had_versions_counts_as_having_accepted_version_1()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            // Exactly what an older build wrote: a bare "acceptedNotice": true, and no version at all.
            var p = new FakeProtector();
            File.WriteAllText(path,
                "{\"api\":\"https://crucible-api.example.com\",\"ui\":\"https://crucible.example.com\",\"clientId\":\"id\","
                + "\"clientSecret\":\"p1:" + p.Protect("s") + "\",\"uploadKey\":\"p1:" + p.Protect("k") + "\","
                + "\"wowFolder\":\"D:\\\\Games\\\\WoW\",\"acceptedNotice\":true}");
            TrayConfig? back = ConfigStore.Load(path, p);
            Assert.NotNull(back);
            Assert.Equal(1, back!.AcceptedNoticeVersion); // so a version 2 notice is shown to them once
            Assert.Equal("D:\\Games\\WoW", back.WowFolder); // and nothing else about them is lost
        }

        [Theory]
        [InlineData("Crucible notice, version 4\n\nBefore you install", 4)]
        [InlineData("Crucible notice, version 11\n", 11)]
        // The notice was renamed with the program (2026-10-04): until 1.0.0 the old first line still counts.
        [InlineData("Tallybook notice, version 2\n\nBefore you install", 2)]
        [InlineData("Tallybook notice, version 11\n", 11)]
        [InlineData("Somebody notice, version 4\n", 0)]
        [InlineData("Before you install - please read this\n", 0)]
        [InlineData("", 0)]
        public void The_notice_carries_its_own_version(string text, int expected)
        {
            Assert.Equal(expected, Notice.VersionOf(text));
        }

        /// <summary>
        /// M124: somebody who accepted version 3 meets version 4 with the box UNticked - one OK must not carry an old
        /// "I understand" over to words they have not read. Settings, after accepting the current one, shows it ticked.
        /// </summary>
        [Theory]
        [InlineData(0, 4, false)]
        [InlineData(3, 4, false)]
        [InlineData(4, 4, true)]
        [InlineData(5, 4, true)]
        [InlineData(1, 0, false)] // a build with no readable notice never counts as accepted
        public void The_box_is_ticked_only_for_the_current_notice_or_a_later_one(int accepted, int current, bool ticked)
        {
            Assert.Equal(ticked, Notice.IsAccepted(accepted, current));
        }

        [Fact]
        public void CanvasFieldsRoundTrip_AndAreAbsentUntilUsed()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            ConfigStore.Save(path, c, new FakeProtector());
            string text = File.ReadAllText(path);
            Assert.DoesNotContain("canvas", text);
            c.CanvasEnabled = true; c.CanvasMode = "SPLIT"; c.CanvasDisplays = new[] { "MONITOR\\A\\1" }; c.CanvasMainhand = "MONITOR\\A\\1";
            c.CanvasSplitSide = "right"; c.CanvasAutoSpan = true; c.CanvasSpanDelaySeconds = 70; c.NoticeShownVersion = 7;
            ConfigStore.Save(path, c, new FakeProtector());
            // Ruling P2-R11: the keys and the mode's spelling are the spec's (section 6.7).
            text = File.ReadAllText(path);
            Assert.Contains("\"canvasMode\": \"split\"", text);
            Assert.Contains("\"canvasSpanDelaySeconds\": 70", text);
            Assert.Contains("\"noticeShownVersion\": 7", text);
            Assert.DoesNotContain("\"canvasSpanDelay\"", text);
            Assert.DoesNotContain("\"noticeShown\"", text);
            TrayConfig back = ConfigStore.Load(path, new FakeProtector())!;
            Assert.True(back.CanvasEnabled); Assert.Equal("SPLIT", back.CanvasMode); Assert.Equal(new[] { "MONITOR\\A\\1" }, back.CanvasDisplays);
            Assert.Equal("MONITOR\\A\\1", back.CanvasMainhand); Assert.Equal("right", back.CanvasSplitSide); Assert.True(back.CanvasAutoSpan);
            Assert.Equal(60, back.CanvasSpanDelaySeconds);   // clamped 0..60
            Assert.Equal(7, back.NoticeShownVersion);
            back.CanvasMode = "DUAL";
            ConfigStore.Save(path, back, new FakeProtector());
            Assert.Contains("\"canvasMode\": \"dual\"", File.ReadAllText(path));
        }

        [Theory]
        [InlineData("split", "SPLIT")]
        [InlineData("SPLIT", "SPLIT")]
        [InlineData("Split", "SPLIT")]
        [InlineData("dual", "DUAL")]
        [InlineData("DUAL", "DUAL")]
        public void TheCanvasModeIsReadWhateverItsCase(string stored, string loaded)
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            ConfigStore.Save(path, ConfigStore.ImportDownload(Download)!, new FakeProtector());
            File.WriteAllText(path, File.ReadAllText(path).TrimEnd('}', '\n') + ",\"canvasMode\":\"" + stored + "\"}");
            Assert.Equal(loaded, ConfigStore.Load(path, new FakeProtector())!.CanvasMode);
        }

        [Fact]
        public void HandEditedCanvasValuesAreHeldToTheirShapes()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            // Load needs sealed credentials, so the hand edit is made to a file the tray itself saved.
            ConfigStore.Save(path, ConfigStore.ImportDownload(Download)!, new FakeProtector());
            string saved = File.ReadAllText(path);
            string tooLong = new string('x', 513);
            File.WriteAllText(path, saved.TrimEnd('}', '\n') + ",\"canvasMode\":\"WHAT\",\"canvasSplitSide\":\"up\",\"canvasSpanDelaySeconds\":-4,\"canvasDisplays\":[\"ok\",null,\"\",\"" + tooLong + "\",5],\"canvasMainhand\":\"" + tooLong + "\",\"noticeShownVersion\":-3}");
            TrayConfig back = ConfigStore.Load(path, new FakeProtector())!;
            Assert.Equal("DUAL", back.CanvasMode); Assert.Equal("left", back.CanvasSplitSide); Assert.Equal(0, back.CanvasSpanDelaySeconds);
            // A string-typed contract reads a bare number as its text, which is a non-empty id of the right length - so it stays.
            Assert.Equal(new[] { "ok", "5" }, back.CanvasDisplays);
            Assert.Equal("", back.CanvasMainhand);
            Assert.Equal(0, back.NoticeShownVersion);
        }

        [Fact]
        public void Keeping_the_addon_up_to_date_defaults_to_on_and_survives_a_round_trip()
        {
            using var dir = new TempDir();
            string path = dir.File("config.json");
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            Assert.True(c.KeepAddonUpToDate);
            c.KeepAddonUpToDate = false;
            ConfigStore.Save(path, c, new FakeProtector());
            Assert.False(ConfigStore.Load(path, new FakeProtector())!.KeepAddonUpToDate);
        }

        [Fact]
        public void A_config_never_prints_its_secrets()
        {
            TrayConfig c = ConfigStore.ImportDownload(Download)!;
            Assert.DoesNotContain("s3cr3t", c.ToString());
            Assert.DoesNotContain("upl0ad", c.ToString());
        }
    }
}
