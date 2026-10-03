using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Tallybook.Tray.Tests
{
    /// <summary>A pretend World of Warcraft folder on disk, a pretend server, and a clock the test moves by hand.</summary>
    internal sealed class World : IDisposable
    {
        public readonly TempDir Dir = new TempDir();
        public readonly FakeServer Server = new FakeServer();
        public readonly TrayConfig Config = ServerClientTests.Config();
        public DateTime Now = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        public readonly string Product;
        public readonly string Wow;
        public readonly string Saved;
        public readonly string DataLua;
        public readonly string LogFile;
        /// <summary>What the server answers "is the item cache wanted": true only for the owner's install (spec 2026-09-26).</summary>
        public bool ItemCacheWanted;
        /// <summary>What the server answers a POST of the item cache - 200 unless a test sets otherwise (Task 13 review).</summary>
        public int ItemCacheStatus = 200;
        public string ItemCache => Path.Combine(Wow, Product, "Cache", "ADB", "enUS", "DBCache.bin");
        public void SaveItemCache(byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ItemCache)!);
            File.WriteAllBytes(ItemCache, bytes);
        }
        public List<FakeServer.Seen> ItemCachePosts() =>
            Server.Requests.Where(r => r.Method == HttpMethod.Post && r.Url.Contains("/api/v1/item-cache", StringComparison.Ordinal)).ToList();
        public string Lua = ServerClientTests.GoodLua;
        public string AddonVersion = "0.9.0";
        public string AddonETag = "\"addon1\"";
        public int AddonStatus = 200;
        public int AddonRequests;
        /// <summary>The server's FOREVER_PRODUCTS, as its manifest carries them. Null: a server from before the list.</summary>
        public string[]? Products = { "_classic_beta_" };
        public string ETag = "\"v1\"";
        public int IngestStatus = 200;
        public Cycle Cycle;

        /// <summary>A manifest in the shape the server sends: a .toc with that version, and one Lua file.</summary>
        public static string AddonJson(string version, string[]? products = null)
        {
            string toc = "## Interface: 16001\n## Title: Tallybook\n## Version: " + version + "\n\nLogic.lua\nData.lua\n";
            string logic = "local _, ns = ...\n-- " + version + "\n";
            string empty = "-- GENERATED\nns.baked = {\n}\n";
            string One(string n, string c) => "{\"name\":\"" + n + "\",\"sha256\":\"" + AddonManifest.Hash(c) + "\",\"content\":" + Quote(c) + "}";
            string list = products == null ? "" : ",\"products\":[" + string.Join(",", products.Select(Quote)) + "]";
            return "{\"version\":\"" + version + "\",\"files\":[" + One("Tallybook.toc", toc) + "," + One("Logic.lua", logic) + "," + One("Data.lua", empty) + "]" + list + "}";
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        /// <param name="product">The game's product folder on this pretend PC - renamed at launch, retail on some PCs.</param>
        public World(bool addonInstalled = true, string product = "_classic_beta_")
        {
            Product = product;
            Wow = Path.Combine(Dir.Path, "World of Warcraft");
            Saved = Path.Combine(Wow, product, "WTF", "Account", "ACCT#1", "SavedVariables", "Tallybook.lua");
            DataLua = Path.Combine(Wow, product, "Interface", "AddOns", "Tallybook", "Data.lua");
            LogFile = Dir.File("log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Saved)!);
            if (addonInstalled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DataLua)!);
                File.WriteAllText(DataLua, "-- the empty one\nns.baked = {\n}\n");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(DataLua)!, "Tallybook.toc"), "## Version: 0.9.0\n\nLogic.lua\nData.lua\n");
            }
            else
            {
                Directory.CreateDirectory(Path.Combine(Wow, product, "Interface", "AddOns"));
            }
            Config.WowFolder = Wow;
            Config.AcceptedNoticeVersion = 2;
            Server.Answer = r =>
            {
                if (r.Url.Contains("/api/v1/item-cache", StringComparison.Ordinal))
                    return r.Method == HttpMethod.Get
                        ? FakeServer.Status(200, ItemCacheWanted ? "{\"wanted\":true}" : "{\"wanted\":false}")
                        : FakeServer.Status(ItemCacheStatus, "{}");
                if (r.Url.EndsWith("/api/v1/ingest", StringComparison.Ordinal)) return FakeServer.Status(IngestStatus, "{}", IngestStatus == 429 ? "120" : null);
                if (r.Url.EndsWith("/api/v1/addon", StringComparison.Ordinal))
                {
                    AddonRequests++;
                    if (AddonStatus != 200) return FakeServer.Status(AddonStatus, "{}");
                    if (r.Headers.TryGetValue("If-None-Match", out string? a) && a == AddonETag) return FakeServer.Status(304, "");
                    var m = FakeServer.Status(200, AddonJson(AddonVersion, Products));
                    m.Headers.TryAddWithoutValidation("ETag", AddonETag);
                    return m;
                }
                if (r.Headers.TryGetValue("If-None-Match", out string? tag) && tag == ETag) return FakeServer.Status(304, "");
                var ok = FakeServer.Status(200, Lua);
                ok.Headers.TryAddWithoutValidation("ETag", ETag);
                return ok;
            };
            Cycle = NewCycle();
        }

        /// <summary>As after a restart: nothing in memory, the sent log and the files still there.</summary>
        public Cycle NewCycle() =>
            new Cycle(Config, new ServerClient(Config, Server), new SentLog(Dir.File("sent.txt")), new Stability(), new TrayLog(LogFile), () => Now);

        public void Save(string content) { File.WriteAllText(Saved, content); }

        /// <summary>Two looks, more than three seconds apart: the second one finds the file stable.</summary>
        public async Task<CycleReport> RunUntilStable(bool force = false)
        {
            await Cycle.RunAsync(force);
            Now = Now.AddSeconds(4);
            return await Cycle.RunAsync(force);
        }

        public int Count(string urlEnd) => Server.Requests.Count(r => r.Url.EndsWith(urlEnd, StringComparison.Ordinal));
        public void Dispose() { Dir.Dispose(); }
    }

    public class CycleTests
    {
        [Fact]
        public async Task A_new_saved_file_is_sent_once_it_is_quiet_and_the_data_file_comes_back()
        {
            using var w = new World();
            w.Save("TallybookDB = { one = 1 }");

            CycleReport first = await w.Cycle.RunAsync(false);
            Assert.Equal(0, first.Uploaded);
            Assert.Equal(0, w.Count("/ingest")); // seen for the first time: not yet known to be quiet

            w.Now = w.Now.AddSeconds(4);
            CycleReport second = await w.Cycle.RunAsync(false);
            Assert.Equal(1, second.Uploaded);
            Assert.Equal(TrayState.Ok, second.State);
            Assert.Equal(1, w.Count("/ingest"));
            Assert.Equal(ServerClientTests.GoodLua, File.ReadAllText(w.DataLua));
            Assert.Equal(w.Now, w.Cycle.LastUploadUtc);

            byte[] sent = w.Server.Requests.First(r => r.Url.EndsWith("/ingest", StringComparison.Ordinal)).Body;
            Assert.Equal(Payload.Gzip(File.ReadAllBytes(w.Saved)), sent);
        }

        [Fact]
        public async Task The_same_bytes_are_never_sent_twice_not_even_after_a_restart()
        {
            using var w = new World();
            w.Save("TallybookDB = { one = 1 }");
            await w.RunUntilStable();
            w.Now = w.Now.AddSeconds(10);
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/ingest"));

            w.Cycle = w.NewCycle();
            await w.RunUntilStable();
            Assert.Equal(1, w.Count("/ingest"));

            w.Save("TallybookDB = { two = 2 }");
            await w.RunUntilStable();
            Assert.Equal(2, w.Count("/ingest"));
        }

        [Fact]
        public async Task The_bak_file_is_sent_too_when_its_bytes_are_new()
        {
            using var w = new World();
            w.Save("TallybookDB = { now = 1 }");
            File.WriteAllText(w.Saved + ".bak", "TallybookDB = { before = 1 }");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(2, r.Uploaded);
        }

        /// <summary>
        /// A file the server will never take (400, 413, 422) is not sent again - and the icon says so in yellow, with its
        /// own words, instead of a green "up to date" over a scan that never arrived. A later file going through clears it.
        /// </summary>
        [Fact]
        public async Task A_rejected_file_is_not_sent_again_and_stays_yellow_with_its_own_words_until_a_file_goes_through()
        {
            const string words = "The server turned down a saved file - see the log";
            using var w = new World { IngestStatus = 422 };
            w.Save("not a saved file");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(0, r.Uploaded);
            Assert.Equal(1, r.Rejected);
            Assert.Equal(TrayState.Retrying, r.State);
            Assert.Equal(words, r.Reason);
            Assert.Equal(1, w.Count("/datafile")); // the rest of the pass went on as usual

            w.Now = w.Now.AddMinutes(5);
            CycleReport later = await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/ingest"));
            Assert.Equal(TrayState.Retrying, later.State);
            Assert.Equal(words, later.Reason);
            Assert.Equal(words, (await w.Cycle.RunAsync(true)).Reason); // "Upload now" sends nothing new, so it stays

            w.IngestStatus = 200;
            w.Save("TallybookDB = { one = 1 }");
            CycleReport ok = await w.RunUntilStable();
            Assert.Equal(1, ok.Uploaded);
            Assert.Equal(TrayState.Ok, ok.State);
        }

        /// <summary>A Data.lua that cannot be written is not "Cannot reach the server" - the server was reached fine.</summary>
        [Fact]
        public async Task A_data_lua_that_cannot_be_written_says_so_and_keeps_saying_so_while_it_waits()
        {
            const string words = "Cannot write Data.lua - trying again soon";
            using var w = new World();
            // The temp file beside it cannot be made, so the write fails - on Windows and Linux alike.
            Directory.CreateDirectory(w.DataLua + AtomicFile.TempSuffix);
            CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(TrayState.Retrying, r.State);
            Assert.Equal(words, r.Reason);

            w.Now = w.Now.AddSeconds(2); // inside the back-off: nothing is tried, and the words do not change
            CycleReport waiting = await w.Cycle.RunAsync(false);
            Assert.Equal(TrayState.Retrying, waiting.State);
            Assert.Equal(words, waiting.Reason);
        }

        [Fact]
        public async Task Refused_credentials_stop_everything_until_the_person_asks_again()
        {
            using var w = new World { IngestStatus = 403 };
            w.Save("TallybookDB = { one = 1 }");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(TrayState.NeedsAttention, r.State);
            Assert.Equal(1, w.Count("/ingest"));
            int requestsSoFar = w.Server.Requests.Count; // includes the fetch at start, before anything was refused

            w.Now = w.Now.AddHours(2);
            Assert.Equal(TrayState.NeedsAttention, (await w.Cycle.RunAsync(false)).State);
            Assert.Equal(requestsSoFar, w.Server.Requests.Count); // no retrying, and no fetching either

            w.IngestStatus = 200;
            CycleReport asked = await w.Cycle.RunAsync(true); // "Upload now"
            Assert.Equal(TrayState.Ok, asked.State);
            Assert.Equal(1, asked.Uploaded);
        }

        [Fact]
        public async Task A_failure_backs_off_and_a_429_waits_as_long_as_it_is_told()
        {
            using var w = new World { IngestStatus = 500 };
            w.Save("TallybookDB = { one = 1 }");
            CycleReport failed = await w.RunUntilStable();
            Assert.Equal(TrayState.Retrying, failed.State);
            Assert.Equal("Cannot reach the server - trying again soon", failed.Reason);
            Assert.Equal(1, w.Count("/ingest"));

            w.Now = w.Now.AddSeconds(2);
            Assert.Equal("Cannot reach the server - trying again soon", (await w.Cycle.RunAsync(false)).Reason);
            Assert.Equal(1, w.Count("/ingest")); // still inside the 5 s back-off

            w.Now = w.Now.AddSeconds(4);
            w.IngestStatus = 429;
            Assert.Equal(TrayState.Retrying, (await w.Cycle.RunAsync(false)).State);
            Assert.Equal(2, w.Count("/ingest"));

            w.IngestStatus = 200;
            w.Now = w.Now.AddSeconds(60);
            await w.Cycle.RunAsync(false);
            Assert.Equal(2, w.Count("/ingest")); // Retry-After: 120

            w.Now = w.Now.AddSeconds(61);
            CycleReport ok = await w.Cycle.RunAsync(false);
            Assert.Equal(3, w.Count("/ingest"));
            Assert.Equal(TrayState.Ok, ok.State);
        }

        [Fact]
        public async Task With_bring_data_back_off_nothing_is_fetched_or_written()
        {
            using var w = new World();
            w.Config.BringDataBack = false;
            string before = File.ReadAllText(w.DataLua);
            w.Save("TallybookDB = { one = 1 }");
            CycleReport r = await w.RunUntilStable(true);
            Assert.Equal(1, r.Uploaded);
            Assert.Equal(0, w.Count("/datafile"));
            Assert.Equal(before, File.ReadAllText(w.DataLua));
        }

        [Fact]
        public async Task Paused_does_nothing_at_all()
        {
            using var w = new World();
            w.Config.Paused = true;
            w.Save("TallybookDB = { one = 1 }");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(TrayState.Paused, r.State);
            Assert.Empty(w.Server.Requests);
        }

        [Fact]
        public async Task A_missing_game_folder_needs_attention_and_nothing_is_created()
        {
            using var w = new World();
            w.Config.WowFolder = Path.Combine(w.Dir.Path, "gone");
            CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(TrayState.NeedsAttention, r.State);
            Assert.False(Directory.Exists(w.Config.WowFolder));
            Assert.Empty(w.Server.Requests);
        }

        [Fact]
        public async Task The_data_file_is_fetched_at_start_then_at_most_every_thirty_minutes_with_the_etag()
        {
            using var w = new World();
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/datafile"));
            w.Now = w.Now.AddMinutes(29);
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/datafile"));
            w.Now = w.Now.AddMinutes(2);
            CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(2, w.Count("/datafile"));
            Assert.Equal("\"v1\"", w.Server.Requests.Last().Headers["If-None-Match"]);
            Assert.Equal(0, r.Wrote);
        }

        [Fact]
        public async Task An_addon_reinstall_that_put_the_empty_file_back_is_repaired_at_the_next_fetch()
        {
            using var w = new World();
            await w.Cycle.RunAsync(false);
            File.WriteAllText(w.DataLua, "-- the empty one again\nns.baked = {\n}\n");
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal(1, r.Wrote);
            Assert.Equal(ServerClientTests.GoodLua, File.ReadAllText(w.DataLua));
        }

        [Fact]
        public async Task What_is_not_our_data_file_never_reaches_the_disk()
        {
            using var w = new World { Lua = "<!DOCTYPE html><html>Just a moment...</html>" };
            string before = File.ReadAllText(w.DataLua);
            CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(0, r.Wrote);
            Assert.Equal(TrayState.Retrying, r.State);
            // The server answered - just not with our file - so not "Cannot reach the server" (tray G3).
            Assert.Equal("The data file looked wrong - trying again soon", r.Reason);
            Assert.Equal(before, File.ReadAllText(w.DataLua));

            w.Now = w.Now.AddSeconds(2);
            Assert.Equal("The data file looked wrong - trying again soon", (await w.Cycle.RunAsync(false)).Reason);
        }

        [Fact]
        public async Task It_writes_only_an_existing_data_lua_creates_nothing_and_leaves_no_temp_file()
        {
            using var w = new World();
            string otherProduct = Path.Combine(w.Wow, "_retail_", "Interface", "AddOns");
            Directory.CreateDirectory(otherProduct); // the addon is not installed there
            await w.Cycle.RunAsync(false);

            // A Forever addon has no business in somebody's retail game: a first install never guesses a product.
            Assert.False(Directory.Exists(Path.Combine(otherProduct, "Tallybook")));
            string addon = Path.GetDirectoryName(w.DataLua)!;
            string[] inAddon = Directory.GetFiles(addon).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            Assert.Equal(new[] { "Data.lua", "Tallybook.toc" }, inAddon); // what was installed, and nothing more
            Assert.Empty(Directory.GetDirectories(addon));
            Assert.DoesNotContain(Directory.GetDirectories(Path.GetDirectoryName(addon)!), d => Path.GetFileName(d)!.Contains(".new-") || Path.GetFileName(d)!.Contains(".old-"));
        }

        [Fact]
        public async Task The_age_of_the_prices_is_read_from_the_file_for_the_tooltip()
        {
            using var w = new World { Lua = "-- GENERATED\nns.baked = {\n    pricesAt = 1790000000,\n    prices = {},\n}\n" };
            await w.Cycle.RunAsync(false);
            Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1790000000), w.Cycle.PricesAtUtc);
        }

        [Fact]
        public async Task The_log_says_what_happened_and_never_a_credential_or_the_account_folder()
        {
            using var w = new World();
            w.Save("TallybookDB = { one = 1 }");
            await w.RunUntilStable();
            string log = File.ReadAllText(w.LogFile);
            Assert.Contains("sent", log);
            Assert.DoesNotContain(ServerClientTests.Secret, log);
            Assert.DoesNotContain(ServerClientTests.Key, log);
            Assert.DoesNotContain("ACCT#1", log);
        }
    }

    public class AddonCycleTests
    {
        [Fact]
        public async Task With_no_addon_installed_a_pass_installs_it_AND_THEN_fills_its_Data_lua()
        {
            using var w = new World(addonInstalled: false);
            string addon = Path.GetDirectoryName(w.DataLua)!;
            Assert.False(Directory.Exists(addon));

            CycleReport r = await w.Cycle.RunAsync(false);

            Assert.Equal("0.9.0", r.AddonInstalled);
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(addon));
            // The order is what matters: an addon installed with the manifest's empty Data.lua would have no prices
            // until the next fetch. It is filled in the same pass.
            Assert.Equal(ServerClientTests.GoodLua, File.ReadAllText(w.DataLua));
            Assert.Equal(1, r.Wrote);
        }

        [Fact]
        public async Task A_half_there_addon_folder_is_repaired_and_the_prices_in_it_are_kept()
        {
            // Data.lua but no .toc: what a half-finished manual install, or a deleted file, leaves behind. The
            // rehearsal was set up this way before the tray app installed addons, and it must still come good.
            using var w = new World(addonInstalled: false);
            string addon = Path.GetDirectoryName(w.DataLua)!;
            Directory.CreateDirectory(addon);
            File.WriteAllText(w.DataLua, "-- mine\nns.baked = { prices = { [2589] = 160 } }\n");
            string mine = File.ReadAllText(w.DataLua);
            w.Save("TallybookDB = { one = 1 }");

            // The repair happens on the first pass - the saved file is not quiet enough to send yet.
            CycleReport first = await w.Cycle.RunAsync(false);
            Assert.Equal("0.9.0", first.AddonInstalled);
            w.Now = w.Now.AddSeconds(4);
            CycleReport then = await w.Cycle.RunAsync(false);

            Assert.Equal(TrayState.Ok, then.State);
            Assert.Equal(1, then.Uploaded);
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(addon));
            Assert.Equal(ServerClientTests.GoodLua, File.ReadAllText(w.DataLua)); // the server's file, written after
            Assert.NotEqual(mine, File.ReadAllText(w.DataLua));
        }

        [Fact]
        public async Task The_version_already_installed_is_not_installed_again()
        {
            using var w = new World();
            CycleReport first = await w.Cycle.RunAsync(false);
            Assert.Null(first.AddonInstalled);
            int asked = w.AddonRequests;

            w.Now = w.Now.AddMinutes(5);
            await w.Cycle.RunAsync(false);
            Assert.Equal(asked, w.AddonRequests); // not asked again so soon
        }

        [Fact]
        public async Task A_newer_addon_is_installed_and_the_players_prices_come_across_it()
        {
            using var w = new World();
            await w.Cycle.RunAsync(false);
            File.WriteAllText(w.DataLua, "-- mine\nns.baked = { prices = { [2589] = 160 } }\n");
            string mine = File.ReadAllText(w.DataLua);

            w.AddonVersion = "1.0.0";
            w.AddonETag = "\"addon2\"";
            w.Lua = "-- GENERATED\nns.baked = {\n    pricesAt = 1790000000,\n}\n"; // nothing new to write back yet
            CycleReport r = await w.Cycle.RunAsync(true);

            Assert.Equal("1.0.0", r.AddonInstalled);
            Assert.Equal("1.0.0", AddonInstaller.InstalledVersion(Path.GetDirectoryName(w.DataLua)!));
            Assert.Contains("[2589] = 160", File.ReadAllText(w.DataLua) + mine); // the prices were not thrown away
        }

        [Fact]
        public async Task Switched_off_it_installs_nothing_but_still_learns_the_product_folders_on_the_same_cadence()
        {
            // Changed knowingly (FOREVER_PRODUCTS): the manifest is also where the product folders come from, and a
            // player who switched updates off must not stop uploading on launch day. It is read; nothing is written.
            using var w = new World(addonInstalled: false);
            w.Config.KeepAddonUpToDate = false;
            w.Products = new[] { "_classic_forever_", "_classic_beta_" };
            CycleReport r = await w.Cycle.RunAsync(true);

            Assert.Equal(1, w.AddonRequests);
            Assert.Null(r.AddonInstalled);
            Assert.False(Directory.Exists(Path.GetDirectoryName(w.DataLua)!));
            Assert.Equal(new[] { "_classic_forever_", "_classic_beta_" }, w.Config.Products);
            Assert.True(r.ProductsChanged);

            w.Now = w.Now.AddMinutes(5);
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.AddonRequests); // not asked again so soon
        }

        [Fact]
        public async Task Launch_day_the_folder_is_renamed_and_one_pass_learns_it_and_the_saved_file_then_goes_out()
        {
            // The PC still holds the beta's list; the server now says the game is in _classic_forever_.
            using var w = new World(product: "_classic_forever_");
            w.Products = new[] { "_classic_forever_", "_classic_beta_" };
            w.Save("TallybookDB = { one = 1 }");
            Assert.Equal(new[] { "_classic_beta_" }, w.Config.Products);

            CycleReport first = await w.Cycle.RunAsync(true);
            Assert.Equal(TrayState.Ok, first.State); // not "the folder is not there"
            Assert.True(first.ProductsChanged);
            Assert.Equal(new[] { "_classic_forever_", "_classic_beta_" }, w.Config.Products);
            Assert.Equal(1, first.Wrote); // Data.lua in the renamed folder, in that same pass

            // The saved file was out of sight in the first pass (the old list), so it is first seen now and sent once
            // it is quiet - the ordinary two looks.
            CycleReport later = await w.RunUntilStable();
            Assert.Equal(1, later.Uploaded);
            Assert.Equal(1, w.Count("/ingest"));
            Assert.False(later.ProductsChanged);
        }

        [Fact]
        public async Task A_first_install_after_the_rename_goes_into_the_renamed_folder()
        {
            using var w = new World(addonInstalled: false, product: "_classic_forever_");
            w.Products = new[] { "_classic_forever_" };
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("0.9.0", r.AddonInstalled);
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(Path.GetDirectoryName(w.DataLua)!));
        }

        [Fact]
        public async Task A_server_from_before_the_list_or_a_list_of_nothing_usable_keeps_the_list_held()
        {
            using var w = new World();
            w.Config.Products = new[] { "_classic_beta_", "_classic_era_" };
            w.Products = null;
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.False(r.ProductsChanged);
            Assert.Equal(new[] { "_classic_beta_", "_classic_era_" }, w.Config.Products);

            w.Products = new[] { "..\\evil", "_Retail_" };
            w.AddonETag = "\"addon2\"";
            r = await w.Cycle.RunAsync(true);
            Assert.False(r.ProductsChanged);
            Assert.Equal(new[] { "_classic_beta_", "_classic_era_" }, w.Config.Products);
        }

        [Fact]
        public async Task A_pc_with_only_retail_gets_no_addon_and_sends_nothing_and_does_not_keep_asking()
        {
            using var w = new World(addonInstalled: false, product: "_retail_");
            w.Save("TallybookDB = { one = 1 }");
            CycleReport r = await w.RunUntilStable();

            Assert.Null(r.AddonInstalled);
            Assert.False(Directory.Exists(Path.GetDirectoryName(w.DataLua)!));
            Assert.Equal(0, w.Count("/ingest"));
            Assert.Equal(1, w.AddonRequests); // once, on the cadence - not every two seconds
        }

        [Fact]
        public async Task An_addon_the_server_cannot_offer_changes_nothing_and_the_upload_still_happens()
        {
            using var w = new World { AddonStatus = 503 };
            w.Save("TallybookDB = { one = 1 }");
            CycleReport r = await w.RunUntilStable();

            Assert.Equal(1, r.Uploaded);
            Assert.Null(r.AddonInstalled);
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(Path.GetDirectoryName(w.DataLua)!)); // untouched
            Assert.Equal(TrayState.Ok, r.State);
        }
    }

    public class TrayLogTests
    {
        [Fact]
        public void It_rotates_and_hides_what_it_was_told_to_hide()
        {
            using var dir = new TempDir();
            string file = dir.File("log.txt");
            var log = new TrayLog(file, 2000);
            log.Hide("hunter2-secret", "");
            log.Write("the key is hunter2-secret, do not tell");
            Assert.DoesNotContain("hunter2", File.ReadAllText(file));
            Assert.Contains("[hidden]", File.ReadAllText(file));

            for (int i = 0; i < 100; i++) log.Write("line " + i + " " + new string('x', 50));
            Assert.True(new FileInfo(file).Length <= 2200);
            Assert.True(File.Exists(dir.File("log.1.txt")));
            Assert.Equal(2, Directory.GetFiles(dir.Path).Length);
        }

        [Fact]
        public void A_log_that_cannot_be_written_never_stops_the_program()
        {
            using var dir = new TempDir();
            var log = new TrayLog(Path.Combine(dir.Path, "no", "such", "\0bad", "log.txt"));
            log.Write("still fine");
        }
    }

    public class AtomicFileTests
    {
        [Fact]
        public void A_failed_write_leaves_the_old_file_and_no_temp_file()
        {
            using var dir = new TempDir();
            string target = Path.Combine(dir.Path, "folder-in-the-way");
            Directory.CreateDirectory(target); // a directory where the file should go: the swap must fail
            Assert.ThrowsAny<Exception>(() => AtomicFile.Write(target, Encoding.UTF8.GetBytes("new")));
            Assert.True(Directory.Exists(target));
            Assert.Empty(Directory.GetFiles(dir.Path));
        }

        [Fact]
        public void It_replaces_and_it_creates()
        {
            using var dir = new TempDir();
            string f = dir.File("a.txt");
            AtomicFile.Write(f, Encoding.UTF8.GetBytes("one"));
            AtomicFile.Write(f, Encoding.UTF8.GetBytes("two"));
            Assert.Equal("two", File.ReadAllText(f));
            Assert.Single(Directory.GetFiles(dir.Path));
        }
    }

    public class ItemCacheCycleTests
    {
        private const uint ItemSparse = 0x919BE54E, TactKey = 0xDF2F53CF;

        [Fact]
        public async Task The_owners_install_sends_only_the_item_rows_gzipped_and_never_the_same_rows_twice()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            byte[] cache = Xfth.Cache(70009, (TactKey, 1, 24), (ItemSparse, 1, 10));
            w.SaveItemCache(cache);

            await w.Cycle.RunAsync(false);
            List<FakeServer.Seen> posts = w.ItemCachePosts();
            Assert.Single(posts);
            Assert.EndsWith("/api/v1/item-cache?product=_classic_beta_", posts[0].Url, StringComparison.Ordinal);
            Assert.Equal(Payload.Gzip(ItemCache.Filter(cache)!), posts[0].Body);

            await w.Cycle.RunAsync(true); // "Upload now" asks again - the same rows are not sent again
            Assert.Single(w.ItemCachePosts());
        }

        [Fact]
        public async Task A_friends_install_is_not_asked_for_it_and_sends_nothing()
        {
            using var w = new World();
            w.SaveItemCache(Xfth.Cache(70009, (ItemSparse, 1, 10)));
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/api/v1/item-cache"));
            Assert.Empty(w.ItemCachePosts());
        }

        [Fact]
        public async Task With_no_cache_on_this_PC_the_server_is_not_even_asked()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            await w.Cycle.RunAsync(false);
            Assert.Equal(0, w.Server.Requests.Count(r => r.Url.Contains("/api/v1/item-cache", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task It_asks_again_every_six_hours_not_every_cycle()
        {
            using var w = new World();
            w.SaveItemCache(Xfth.Cache(70009, (ItemSparse, 1, 10)));
            await w.Cycle.RunAsync(false);
            w.Now = w.Now.AddHours(1);
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/api/v1/item-cache"));
            w.Now = w.Now.AddHours(6);
            await w.Cycle.RunAsync(false);
            Assert.Equal(2, w.Count("/api/v1/item-cache"));
        }

        [Fact]
        public async Task The_sent_record_survives_a_restart()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            w.SaveItemCache(Xfth.Cache(70009, (ItemSparse, 1, 10)));
            await w.Cycle.RunAsync(false);
            Assert.Single(w.ItemCachePosts());

            // As after a restart: a fresh Cycle and a fresh SentLog reading the same sent.txt from disk.
            w.Cycle = w.NewCycle();
            await w.Cycle.RunAsync(true);
            Assert.Single(w.ItemCachePosts());
        }

        [Fact]
        public async Task A_cache_the_server_rejects_is_not_sent_again()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            w.ItemCacheStatus = 422;
            w.SaveItemCache(Xfth.Cache(70009, (ItemSparse, 1, 10)));
            await w.Cycle.RunAsync(false);
            Assert.Single(w.ItemCachePosts());

            await w.Cycle.RunAsync(true); // "Upload now" asks again - a rejected cache is not retried
            Assert.Single(w.ItemCachePosts());
        }

        [Fact]
        public async Task A_cache_larger_than_the_saved_file_cap_is_skipped_and_logged()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            Directory.CreateDirectory(Path.GetDirectoryName(w.ItemCache)!);
            using (FileStream big = File.Create(w.ItemCache)) { big.SetLength(64L * 1024 * 1024 + 1); }

            await w.Cycle.RunAsync(false);

            Assert.Empty(w.ItemCachePosts());
            Assert.Contains("too large to send", File.ReadAllText(w.LogFile));
        }

        [Fact]
        public async Task A_cache_that_cannot_be_read_is_logged()
        {
            using var w = new World();
            w.ItemCacheWanted = true;
            w.SaveItemCache(Xfth.Cache(70009, (ItemSparse, 1, 10)));

            using (new FileStream(w.ItemCache, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await w.Cycle.RunAsync(false);
            }

            Assert.Empty(w.ItemCachePosts());
            Assert.Contains("could not read the game's item cache", File.ReadAllText(w.LogFile));
        }
    }
}
