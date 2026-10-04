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
