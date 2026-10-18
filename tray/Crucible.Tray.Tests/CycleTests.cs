using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Crucible.Tray.Tests
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
        /// <summary>The Canvas addon on offer at /api/v1/addons/CrucibleCanvas (spec 2026-10-15 §7.2).</summary>
        public string CanvasVersion = "0.1.0";
        public int CanvasStatus = 200;
        public int CanvasRequests;
        /// <summary>The canvas manifest request gets no HTTP answer at all (a dropped connection).</summary>
        public bool CanvasUnreachable;
        public string CanvasSaved => CanvasSavedOf("ACC");
        public string CanvasSavedOf(string account) => Path.Combine(Wow, Product, "WTF", "Account", account, "SavedVariables", "CrucibleCanvas.lua");
        /// <param name="writtenUtc">The file's write time, when a test needs one account's file newer than another's.</param>
        public void SaveCanvas(string text, string account = "ACC", DateTime? writtenUtc = null)
        {
            string file = CanvasSavedOf(account);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
            if (writtenUtc != null) File.SetLastWriteTimeUtc(file, writtenUtc.Value);
        }
        /// <summary>A complete saved Canvas file: one chunk and its sentinel.</summary>
        public static string CanvasDb(int revision, string base64) =>
            "CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:" + revision + ":1/1:" + base64 + "\", -- [1]\n\"cc1end:" + revision + ":1\", -- [2]\n},\n}\n";
        public Cycle Cycle;

        /// <summary>A manifest in the shape the server sends: a .toc with that version, and one Lua file.</summary>
        public static string AddonJson(string version, string[]? products = null)
        {
            string toc = "## Interface: 16001\n## Title: Crucible\n## Version: " + version + "\n\nLogic.lua\nData.lua\n";
            string logic = "local _, ns = ...\n-- " + version + "\n";
            string empty = "-- GENERATED\nns.baked = {\n}\n";
            string One(string n, string c) => "{\"name\":\"" + n + "\",\"sha256\":\"" + AddonManifest.Hash(c) + "\",\"content\":" + Quote(c) + "}";
            string list = products == null ? "" : ",\"products\":[" + string.Join(",", products.Select(Quote)) + "]";
            return "{\"version\":\"" + version + "\",\"files\":[" + One("Crucible.toc", toc) + "," + One("Logic.lua", logic) + "," + One("Data.lua", empty) + "]" + list + "}";
        }

        /// <summary>The Canvas manifest: its .toc with that version, its code, and the two placeholders the tray later rewrites.</summary>
        public static string CanvasJson(string version)
        {
            string toc = "## Interface: 16001\n## Title: Crucible Canvas\n## Version: " + version + "\n\nTopology.lua\nRecovery.lua\nInit.lua\n";
            string init = "local _, ns = ...\n-- " + version + "\n";
            string One(string n, string c) => "{\"name\":\"" + n + "\",\"sha256\":\"" + AddonManifest.Hash(c) + "\",\"content\":" + Quote(c) + "}";
            return "{\"version\":\"" + version + "\",\"files\":[" + One("CrucibleCanvas.toc", toc) + "," + One("Init.lua", init) + ","
                + One("Topology.lua", "CrucibleCanvasTopology = {}\n") + "," + One("Recovery.lua", "CrucibleCanvasRecovery = {}\n") + "]}";
        }

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";

        /// <param name="product">The game's product folder on this pretend PC - renamed at launch, retail on some PCs.</param>
        public World(bool addonInstalled = true, string product = "_classic_beta_")
        {
            Product = product;
            Wow = Path.Combine(Dir.Path, "World of Warcraft");
            Saved = Path.Combine(Wow, product, "WTF", "Account", "ACCT#1", "SavedVariables", "Crucible.lua");
            DataLua = Path.Combine(Wow, product, "Interface", "AddOns", "Crucible", "Data.lua");
            LogFile = Dir.File("log.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(Saved)!);
            if (addonInstalled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(DataLua)!);
                File.WriteAllText(DataLua, "-- the empty one\nns.baked = {\n}\n");
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(DataLua)!, "Crucible.toc"), "## Version: 0.9.0\n\nLogic.lua\nData.lua\n");
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
                if (r.Url.EndsWith("/api/v1/addons/CrucibleCanvas", StringComparison.Ordinal))
                {
                    CanvasRequests++;
                    if (CanvasUnreachable) throw new HttpRequestException("no answer");
                    if (CanvasStatus != 200) return FakeServer.Status(CanvasStatus, "{}");
                    string canvasTag = "\"canvas-" + CanvasVersion + "\"";
                    if (r.Headers.TryGetValue("If-None-Match", out string? c) && c == canvasTag) return FakeServer.Status(304, "");
                    var m = FakeServer.Status(200, CanvasJson(CanvasVersion));
                    m.Headers.TryAddWithoutValidation("ETag", canvasTag);
                    return m;
                }
                if (r.Headers.TryGetValue("If-None-Match", out string? tag) && tag == ETag) return FakeServer.Status(304, "");
                var ok = FakeServer.Status(200, Lua);
                ok.Headers.TryAddWithoutValidation("ETag", ETag);
                return ok;
            };
            Cycle = NewCycle();
        }

        /// <summary>Spec 2026-10-12: the running exe and the shell's signature check, when a test sets them.</summary>
        public string? ExePath;
        public FakeSigner Signer = new FakeSigner();

        /// <summary>As after a restart: nothing in memory, the sent log and the files still there.</summary>
        public Cycle NewCycle() =>
            new Cycle(Config, new ServerClient(Config, Server), new SentLog(Dir.File("sent.txt")), new Stability(), new TrayLog(LogFile), () => Now, ExePath, ExePath == null ? null : Signer);

        public string[] Log() => File.Exists(LogFile) ? File.ReadAllLines(LogFile) : new string[0];

        /// <summary>A pretend program: the PE header and some bytes.</summary>
        public static byte[] Exe(int size) { var b = new byte[size]; b[0] = (byte)'M'; b[1] = (byte)'Z'; return b; }

        public static SignerInfo Ours(string version) => new SignerInfo(AppInfo.Publisher, "CN=Jason Howard, O=Jason Howard, C=US", version);

        /// <summary>Puts a running exe on disk and makes the server offer `version` with `fresh` as the download.</summary>
        public void OfferTray(byte[] fresh, string version, string? sha = null)
        {
            ExePath = Dir.File("Crucible.exe");
            File.WriteAllBytes(ExePath, Exe(100));
            Signer.Signers[ExePath] = Ours(AppInfo.Version);
            Func<FakeServer.Seen, HttpResponseMessage> inner = Server.Answer;
            Server.Answer = r =>
            {
                if (r.Url.EndsWith("/api/v1/tray/version", StringComparison.Ordinal))
                    return FakeServer.Status(200, "{\"version\":\"" + version + "\",\"sha256\":\"" + (sha ?? SelfUpdate.Sha256Hex(fresh)) + "\",\"publisher\":\"Jason Howard\"}");
                if (r.Url.EndsWith("/api/v1/tray/Crucible.exe", StringComparison.Ordinal))
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(fresh) };
                return inner(r);
            };
            Cycle = NewCycle();
        }

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

    internal sealed class FakeSigner : ISigner
    {
        public Dictionary<string, SignerInfo?> Signers = new Dictionary<string, SignerInfo?>(StringComparer.OrdinalIgnoreCase);
        public int Calls;
        public SignerInfo? SignerOf(string path) { Calls++; return Signers.TryGetValue(path, out SignerInfo? s) ? s : null; }
    }

    public class CycleTests
    {
        // ---- spec 2026-10-12: the tray keeps itself current ------------------------------------------------------

        [Fact]
        public async Task A_newer_signed_tray_is_fetched_verified_and_staged_beside_the_exe()
        {
            using var w = new World();
            byte[] fresh = World.Exe(5000);
            w.OfferTray(fresh, "9.9.9");
            w.Signer.Signers[w.ExePath + ".new"] = World.Ours("9.9.9");

            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("9.9.9", r.TrayStaged);
            Assert.Null(r.TrayUpdateFailed);
            Assert.Equal("9.9.9", w.Cycle.StagedVersion);
            Assert.Equal(fresh, File.ReadAllBytes(w.ExePath + ".new"));
            Assert.Contains(w.Log(), l => l.Contains("tray 9.9.9 staged"));
            Assert.DoesNotContain(w.Log(), l => l.Contains(w.Dir.Path)); // a path is never logged
            // The next pass does not fetch again: the file is there.
            int before = w.Server.Requests.Count;
            await w.Cycle.RunAsync(true);
            Assert.DoesNotContain(w.Server.Requests.Skip(before), q => q.Url.EndsWith("/Crucible.exe", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData("sha", Refusal.ShaMismatch)]
        [InlineData("signer", Refusal.WrongPublisher)]
        [InlineData("nosig", Refusal.BadSignature)]
        [InlineData("subject", Refusal.DifferentSigner)]
        [InlineData("version", Refusal.VersionMismatch)]
        public async Task A_download_that_fails_a_check_is_deleted_and_the_version_is_remembered_as_failed(string how, Refusal expected)
        {
            using var w = new World();
            byte[] fresh = World.Exe(5000);
            w.OfferTray(fresh, "9.9.9", how == "sha" ? new string('b', 64) : null);
            string staged = w.ExePath + ".new";
            if (how == "sha") w.Signer.Signers[staged] = World.Ours("9.9.9");
            if (how == "signer") w.Signer.Signers[staged] = new SignerInfo("Somebody Else", "CN=Somebody Else", "9.9.9");
            if (how == "nosig") w.Signer.Signers[staged] = null;
            if (how == "subject") w.Signer.Signers[staged] = new SignerInfo(AppInfo.Publisher, "CN=Jason Howard, O=Elsewhere Ltd, C=GB", "9.9.9");
            if (how == "version") w.Signer.Signers[staged] = World.Ours("0.1.0");

            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Null(r.TrayStaged);
            Assert.Equal(expected, r.TrayUpdateFailed);
            Assert.False(File.Exists(staged));
            Assert.Equal("9.9.9", w.Config.FailedUpdateVersion);
            Assert.Contains(w.Log(), l => l.Contains("tray 9.9.9 refused: " + expected));
            int before = w.Server.Requests.Count;
            await w.Cycle.RunAsync(true); // forced again: the failed version is not fetched a second time
            Assert.DoesNotContain(w.Server.Requests.Skip(before), q => q.Url.EndsWith("/Crucible.exe", StringComparison.Ordinal));
        }

        [Fact]
        public async Task An_unsigned_running_build_never_asks_for_the_exe()
        {
            using var w = new World();
            w.OfferTray(World.Exe(5000), "9.9.9");
            w.Signer.Signers[w.ExePath!] = null; // SignerOf(exe) -> null: a local build
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Null(r.TrayStaged);
            Assert.DoesNotContain(w.Server.Requests, q => q.Url.EndsWith("/Crucible.exe", StringComparison.Ordinal));
            Assert.False(File.Exists(w.ExePath + ".new"));
        }

        [Fact]
        public async Task A_folder_that_cannot_be_written_fails_the_staging_and_keeps_the_old_exe()
        {
            using var w = new World();
            w.OfferTray(World.Exe(5000), "9.9.9");
            Directory.CreateDirectory(w.ExePath + ".new"); // a directory where the staged file must go: the write throws
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal(Refusal.StagingFailed, r.TrayUpdateFailed);
            Assert.Equal("9.9.9", w.Config.FailedUpdateVersion);
            Assert.Equal(World.Exe(100), File.ReadAllBytes(w.ExePath!));
            Assert.Contains(w.Log(), l => l.Contains("tray 9.9.9 could not be staged: "));
        }

        [Fact]
        public async Task Three_checks_that_cannot_get_the_exe_mark_the_update_stalled_so_the_nudge_can_take_over()
        {
            // Review I4(a): a 404 on the exe route (a Cloudflare rule, say) is not a verification failure and is retried;
            // after three misses the shell is told, so the person is sent to the download page instead of waiting forever.
            using var w = new World();
            w.OfferTray(World.Exe(5000), "9.9.9");
            Func<FakeServer.Seen, HttpResponseMessage> inner = w.Server.Answer;
            w.Server.Answer = r => r.Url.EndsWith("/api/v1/tray/Crucible.exe", StringComparison.Ordinal) ? FakeServer.Status(404) : inner(r);
            for (int i = 1; i <= 2; i++)
            {
                await w.Cycle.RunAsync(true);
                Assert.False(w.Cycle.TrayStalled);
            }
            await w.Cycle.RunAsync(true);
            Assert.True(w.Cycle.TrayStalled);
            Assert.Equal("", w.Config.FailedUpdateVersion); // not a failed version: it is still tried on the cadence
            // A fetch that gets through clears it.
            w.Server.Answer = inner;
            w.Signer.Signers[w.ExePath + ".new"] = World.Ours("9.9.9");
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("9.9.9", r.TrayStaged);
            Assert.False(w.Cycle.TrayStalled);
        }

        [Fact]
        public async Task An_older_or_equal_tray_on_offer_is_left_alone()
        {
            using var w = new World();
            w.OfferTray(World.Exe(5000), "0.0.1");
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Null(r.TrayStaged);
            Assert.Null(r.TrayUpdateFailed);
            Assert.DoesNotContain(w.Server.Requests, q => q.Url.EndsWith("/Crucible.exe", StringComparison.Ordinal));
        }

        [Fact]
        public async Task A_new_saved_file_is_sent_once_it_is_quiet_and_the_data_file_comes_back()
        {
            using var w = new World();
            w.Save("CrucibleDB = { one = 1 }");

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
            w.Save("CrucibleDB = { one = 1 }");
            await w.RunUntilStable();
            w.Now = w.Now.AddSeconds(10);
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.Count("/ingest"));

            w.Cycle = w.NewCycle();
            await w.RunUntilStable();
            Assert.Equal(1, w.Count("/ingest"));

            w.Save("CrucibleDB = { two = 2 }");
            await w.RunUntilStable();
            Assert.Equal(2, w.Count("/ingest"));
        }

        [Fact]
        public async Task The_bak_file_is_sent_too_when_its_bytes_are_new()
        {
            using var w = new World();
            w.Save("CrucibleDB = { now = 1 }");
            File.WriteAllText(w.Saved + ".bak", "CrucibleDB = { before = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            Assert.False(Directory.Exists(Path.Combine(otherProduct, "Crucible")));
            string addon = Path.GetDirectoryName(w.DataLua)!;
            string[] inAddon = Directory.GetFiles(addon).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray()!;
            Assert.Equal(new[] { "Crucible.toc", "Data.lua" }, inAddon); // what was installed, and nothing more
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
            w.Save("CrucibleDB = { one = 1 }");
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
        public async Task A_member_who_had_Tallybook_gets_Crucible_the_old_folder_goes_and_the_old_saved_file_is_still_sent()
        {
            // The rename (2026-10-04, remove at 1.0.0): the game still has the old addon and has not written Crucible.lua yet.
            using var w = new World(addonInstalled: false);
            string old = Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "Tallybook");
            Directory.CreateDirectory(old);
            File.WriteAllText(Path.Combine(old, "Tallybook.toc"), AddonInstallerTests.OldToc);
            File.WriteAllText(Path.Combine(old, "Data.lua"), "-- mine\nns.baked = { prices = { [2589] = 160 } }\n");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(w.Saved)!, "Tallybook.lua"), "TallybookDB = { one = 1 }");

            CycleReport first = await w.Cycle.RunAsync(false);
            w.Now = w.Now.AddSeconds(4);
            CycleReport then = await w.Cycle.RunAsync(false);

            Assert.Equal("0.9.0", first.AddonInstalled);
            Assert.Equal("0.9.0", AddonInstaller.InstalledVersion(Path.GetDirectoryName(w.DataLua)!));
            Assert.False(Directory.Exists(old));
            Assert.Equal(ServerClientTests.GoodLua, File.ReadAllText(w.DataLua));
            Assert.Equal(1, then.Uploaded);
            Assert.Equal(TrayState.Ok, then.State);
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
            w.Save("CrucibleDB = { one = 1 }");

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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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
            w.Save("CrucibleDB = { one = 1 }");
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

    /// <summary>Spec 2026-10-15 §6.6, §7.2: the Canvas addon, installed only while switched on, and its Recovery.lua.</summary>
    public class CanvasCycleTests
    {
        [Fact]
        public async Task TheCanvasAddonIsInstalledOnlyWhenTheSwitchIsOn_AndThenKeptCurrent()
        {
            using var w = new World();
            await w.Cycle.RunAsync(true);
            Assert.Equal(0, w.CanvasRequests);
            Assert.False(Directory.Exists(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas")));
            w.Config.CanvasEnabled = true;
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("0.1.0", r.CanvasAddonInstalled);
            Assert.True(File.Exists(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "CrucibleCanvas.toc")));
            Assert.Equal("CrucibleCanvasTopology = {}\n", File.ReadAllText(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Topology.lua")));
            w.CanvasVersion = "0.2.0"; w.Now = w.Now.AddHours(7);
            r = await w.Cycle.RunAsync(false);
            Assert.Equal("0.2.0", r.CanvasAddonInstalled);
        }

        [Fact]
        public async Task SwitchingCanvasOff_LeavesTheFolder_AndStopsUpdating()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            w.Config.CanvasEnabled = false; w.CanvasVersion = "0.2.0"; w.Now = w.Now.AddHours(7);
            await w.Cycle.RunAsync(false);
            Assert.Equal("0.1.0", AddonInstaller.InstalledVersion(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas"), AddonSpec.Canvas));
        }

        [Fact]
        public async Task RecoveryLua_IsWrittenFromAStableSavedFile_OnlyWhenTheRevisionRose()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            string recovery = Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua");
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:3:1/1:QUJD\", -- [1]\n\"cc1end:3:1\", -- [2]\n},\n}\n");
            CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(0, r.RecoveryWritten);                 // not stable yet
            w.Now = w.Now.AddSeconds(4); r = await w.Cycle.RunAsync(false);
            Assert.Equal(1, r.RecoveryWritten);
            Assert.Equal(RecoveryFile.Render(3, "QUJD"), File.ReadAllText(recovery));
            w.Now = w.Now.AddSeconds(4); r = await w.Cycle.RunAsync(false);
            Assert.Equal(0, r.RecoveryWritten);                 // same revision: nothing rewritten
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:2:1/1:QUJE\", -- [1]\n\"cc1end:2:1\", -- [2]\n},\n}\n");
            w.Now = w.Now.AddSeconds(4); r = await w.Cycle.RunAsync(false);
            Assert.Equal(0, r.RecoveryWritten);                 // an OLDER revision never replaces a newer file
            Assert.Contains("revision = 3", File.ReadAllText(recovery));
            w.Now = w.Now.AddSeconds(4); r = await w.Cycle.RunAsync(false);
            Assert.Equal(0, r.RecoveryWritten);                 // ...also once that older file is stable (the guard, not the stability check)
            Assert.Contains("revision = 3", File.ReadAllText(recovery));
        }

        [Fact]
        public async Task APartialSavedFile_IsRefusedWhole_AndTriedAgain()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:5:1/2:QUJD\", -- [1]\n},\n}\n");
            CycleReport r = await w.RunUntilStable();       // brief slip: a file is stable only on its second look
            Assert.Equal(0, r.RecoveryWritten);
            Assert.Contains("no sentinel", File.ReadAllText(w.LogFile));
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:5:1/1:QUJD\", -- [1]\n\"cc1end:5:1\", -- [2]\n},\n}\n");
            r = await w.RunUntilStable();
            Assert.Equal(1, r.RecoveryWritten);
        }

        [Fact]
        public async Task WithNoSecondDisplay_TheAddonStillInstalls_AndNothingElseChanges()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;   // the cycle does not span; the controller does, in the shell
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("0.1.0", r.CanvasAddonInstalled);
            Assert.Equal(TrayState.Ok, r.State);
        }

        [Fact]
        public async Task A_server_with_no_canvas_addon_does_not_stop_the_uploads()
        {
            using var w = new World(); w.Config.CanvasEnabled = true; w.CanvasStatus = 404;
            w.Save("CrucibleDB = {}\n");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(1, r.Uploaded);
            Assert.Equal(TrayState.Ok, r.State);
            Assert.Null(r.CanvasAddonInstalled);
            Assert.Contains("no canvas addon to install this time: the canvas addon answered 404", File.ReadAllText(w.LogFile));
        }

        [Fact]
        public async Task A_saved_file_over_the_cap_is_skipped()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            Directory.CreateDirectory(Path.GetDirectoryName(w.CanvasSaved)!);
            using (FileStream big = File.Create(w.CanvasSaved)) { big.SetLength(RecoveryFile.MaxSavedBytes + 1); }
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(0, r.RecoveryWritten);
            Assert.Equal("CrucibleCanvasRecovery = {}\n", File.ReadAllText(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua")));
        }

        [Fact]
        public async Task A_Recovery_lua_already_on_disk_seeds_the_revision_after_a_restart()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            string recovery = Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua");
            File.WriteAllText(recovery, RecoveryFile.Render(9, "QUJD"));
            w.Cycle = w.NewCycle();
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:4:1/1:QUJE\", -- [1]\n\"cc1end:4:1\", -- [2]\n},\n}\n");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(0, r.RecoveryWritten);
            Assert.Contains("revision = 9", File.ReadAllText(recovery));
            Assert.DoesNotContain(w.Log(), l => l.Contains(w.Dir.Path)); // a path is never logged
        }

        [Fact]
        public async Task A_damaged_Recovery_lua_does_not_stop_the_pass()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            string recovery = Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua");
            File.WriteAllText(recovery, "CrucibleCanvasRecovery = { schema = 1, revision = \u0663, state = \"\" }\n"); // an Arabic-Indic digit
            w.Cycle = w.NewCycle();
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:1:1/1:QUJD\", -- [1]\n\"cc1end:1:1\", -- [2]\n},\n}\n");
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(1, r.RecoveryWritten);
            Assert.Equal(TrayState.Ok, r.State);
        }

        [Fact]
        public async Task A_partial_file_that_stays_partial_is_logged_once_not_every_pass()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:5:1/2:QUJD\", -- [1]\n},\n}\n");
            await w.RunUntilStable();
            for (int i = 0; i < 3; i++) { w.Now = w.Now.AddSeconds(2); await w.Cycle.RunAsync(false); }
            Assert.Single(w.Log(), l => l.Contains("canvas saved file not usable yet: no sentinel"));
        }

        [Fact]
        public async Task A_canvas_addon_the_server_does_not_have_is_asked_for_once_then_on_the_cadence_or_when_forced()
        {
            // Ruling P2-R4: "at once when missing" is the first check only; a 404 waits the cadence like any other answer.
            using var w = new World(); w.Config.CanvasEnabled = true; w.CanvasStatus = 404;
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.CanvasRequests);
            for (int i = 0; i < 10; i++) { w.Now = w.Now.AddSeconds(2); await w.Cycle.RunAsync(false); }
            Assert.Equal(1, w.CanvasRequests);
            await w.Cycle.RunAsync(true);                        // "Upload now"
            Assert.Equal(2, w.CanvasRequests);
            w.CanvasStatus = 200; w.Now = w.Now.Add(Cycle.AddonEvery);
            CycleReport r = await w.Cycle.RunAsync(false);       // and the cadence
            Assert.Equal(3, w.CanvasRequests);
            Assert.Equal("0.1.0", r.CanvasAddonInstalled);
        }

        [Fact]
        public async Task The_account_written_last_decides_Recovery_lua_not_the_higher_revision_of_an_older_one()
        {
            // Ruling P2-R5 (spec §6.6, R27): the newest account's document wins.
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            string recovery = Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua");
            DateTime t = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            w.SaveCanvas(World.CanvasDb(9, "QUJD"), "ACC1", t);
            w.SaveCanvas(World.CanvasDb(4, "QUJE"), "ACC2", t.AddMinutes(1));
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(1, r.RecoveryWritten);
            Assert.Equal(RecoveryFile.Render(4, "QUJE"), File.ReadAllText(recovery));
            w.SaveCanvas(World.CanvasDb(10, "QUJG"), "ACC1", t.AddMinutes(2));
            r = await w.RunUntilStable();
            Assert.Equal(1, r.RecoveryWritten);
            Assert.Equal(RecoveryFile.Render(10, "QUJG"), File.ReadAllText(recovery));
        }

        [Fact]
        public async Task Two_accounts_written_in_the_same_second_give_the_higher_revision()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            DateTime t = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            w.SaveCanvas(World.CanvasDb(7, "QUJD"), "ACC1", t.AddMilliseconds(100));
            w.SaveCanvas(World.CanvasDb(3, "QUJE"), "ACC2", t.AddMilliseconds(900));
            await w.RunUntilStable();
            Assert.Contains("revision = 7", File.ReadAllText(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua")));
        }

        [Fact]
        public async Task A_newest_file_that_is_partial_is_not_passed_over_for_an_older_account()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            DateTime t = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
            w.SaveCanvas(World.CanvasDb(9, "QUJD"), "ACC1", t);
            w.SaveCanvas("CrucibleCanvasDB = {\n[\"chunks\"] = {\n\"cc1:5:1/2:QUJD\", -- [1]\n},\n}\n", "ACC2", t.AddMinutes(1));
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(0, r.RecoveryWritten);
            Assert.Equal("CrucibleCanvasRecovery = {}\n", File.ReadAllText(Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua")));
        }
        // ---- final fix wave (rulings P2-R8, P2-R12, P2-R4 refinement) ------------------------------------------------

        private static string RecoveryOf(World w) => Path.Combine(w.Wow, w.Product, "Interface", "AddOns", "CrucibleCanvas", "Recovery.lua");

        [Fact]
        public async Task Recovery_lua_is_written_while_the_server_is_down_and_an_upload_is_waiting()
        {
            // Ruling P2-R8: a down server at logout must not lose that session's layout.
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);                         // the canvas addon is installed
            w.IngestStatus = 503;
            w.Save("CrucibleDB = { one = 1 }\n");                 // an upload is waiting, so every pass stops at the server
            w.SaveCanvas(World.CanvasDb(3, "QUJD"));
            int written = 0;
            for (int i = 0; i < 10; i++)
            {
                w.Now = w.Now.AddSeconds(2);
                CycleReport r = await w.Cycle.RunAsync(false);
                written += r.RecoveryWritten;
                if (i >= 2) Assert.Equal(TrayState.Retrying, r.State);
            }
            Assert.Equal(1, written);
            Assert.Equal(RecoveryFile.Render(3, "QUJD"), File.ReadAllText(RecoveryOf(w)));
        }

        [Fact]
        public async Task Recovery_lua_is_written_while_paused()
        {
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            w.Config.Paused = true;
            w.SaveCanvas(World.CanvasDb(5, "QUJD"));
            CycleReport r = await w.RunUntilStable();
            Assert.Equal(TrayState.Paused, r.State);
            Assert.Equal(1, r.RecoveryWritten);
            Assert.Equal(RecoveryFile.Render(5, "QUJD"), File.ReadAllText(RecoveryOf(w)));
            Assert.Equal(1, w.CanvasRequests);                    // paused: nothing else was asked of the server
        }

        [Fact]
        public async Task A_missing_game_folder_still_says_so_and_paused_still_wins()
        {
            using var w = new World(); w.Config.CanvasEnabled = true; w.Config.WowFolder = Path.Combine(w.Dir.Path, "nowhere");
            Assert.Equal(TrayState.NeedsAttention, (await w.Cycle.RunAsync(false)).State);
            w.Config.Paused = true;
            Assert.Equal(TrayState.Paused, (await w.Cycle.RunAsync(false)).State);
        }

        [Fact]
        public async Task The_canvas_addon_follows_the_canvas_switch_even_with_AH_addon_updates_off()
        {
            // Ruling P2-R12: "Keep the Crucible addon up to date" is the AH addon's switch, not Canvas's.
            using var w = new World(); w.Config.CanvasEnabled = true; w.Config.KeepAddonUpToDate = false;
            CycleReport r = await w.Cycle.RunAsync(true);
            Assert.Equal("0.1.0", r.CanvasAddonInstalled);
        }

        [Fact]
        public async Task A_canvas_fetch_that_got_no_answer_is_tried_again_after_a_minute_and_logged_once()
        {
            // Ruling P2-R16: no answer is retried after one minute, not every pass; an answer keeps the full cadence.
            using var w = new World(); w.Config.CanvasEnabled = true; w.CanvasUnreachable = true;
            DateTime start = w.Now;
            await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.CanvasRequests);
            for (int i = 0; i < 29; i++) { w.Now = w.Now.AddSeconds(2); await w.Cycle.RunAsync(false); }   // up to 58 s
            w.Now = start.AddSeconds(59); await w.Cycle.RunAsync(false);
            Assert.Equal(1, w.CanvasRequests);
            w.Now = start.AddSeconds(61); await w.Cycle.RunAsync(false);
            Assert.Equal(2, w.CanvasRequests);
            Assert.Single(w.Log(), l => l.Contains("no canvas addon to install this time"));
            w.CanvasUnreachable = false;
            w.Now = start.AddSeconds(122); CycleReport r = await w.Cycle.RunAsync(false);
            Assert.Equal(3, w.CanvasRequests);
            Assert.Equal("0.1.0", r.CanvasAddonInstalled);
            w.Now = start.AddSeconds(190); await w.Cycle.RunAsync(false);
            Assert.Equal(3, w.CanvasRequests);                    // an answer: the full cadence again
        }

        [Fact]
        public async Task An_outside_reinstall_of_the_canvas_addon_gets_Recovery_lua_back()
        {
            // The revision on disk decides every pass: no remembered revision outlives the file it was written to.
            using var w = new World(); w.Config.CanvasEnabled = true;
            await w.Cycle.RunAsync(true);
            w.SaveCanvas(World.CanvasDb(3, "QUJD"));
            Assert.Equal(1, (await w.RunUntilStable()).RecoveryWritten);
            File.WriteAllText(RecoveryOf(w), "CrucibleCanvasRecovery = {}\n");   // the person reinstalled the addon by hand
            w.Now = w.Now.AddSeconds(2);
            Assert.Equal(1, (await w.Cycle.RunAsync(false)).RecoveryWritten);
            Assert.Equal(RecoveryFile.Render(3, "QUJD"), File.ReadAllText(RecoveryOf(w)));
        }
    }
}
