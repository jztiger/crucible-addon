using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Crucible.Tray
{
    public enum TrayState
    {
        /// <summary>Green: nothing waiting, nothing wrong.</summary>
        Ok,
        /// <summary>
        /// Yellow: something that may pass, each with its own words - the server could not be reached or asked us to
        /// wait, Data.lua could not be written, or the data file looked wrong (all tried again by itself); or the server
        /// turned down a saved file, which stays yellow until a later file goes through.
        /// </summary>
        Retrying,
        /// <summary>Red: it will not fix itself - the folder is gone, or the credentials are refused.</summary>
        NeedsAttention,
        /// <summary>Grey: the person paused it.</summary>
        Paused,
    }

    public sealed class CycleReport
    {
        public int Uploaded { get; set; }
        public int Rejected { get; set; }
        public int Wrote { get; set; }
        /// <summary>The addon version installed this pass, or null when nothing was installed.</summary>
        public string? AddonInstalled { get; set; }
        /// <summary>The server sent a different list of product folders; it is in the config now, to be saved.</summary>
        public bool ProductsChanged { get; set; }
        /// <summary>Spec 2026-10-12: a verified newer tray is written beside the exe and waits for a quiet moment.</summary>
        public string? TrayStaged { get; set; }
        public Refusal? TrayUpdateFailed { get; set; }
        /// <summary>The Canvas addon version installed this pass (spec 2026-10-15 §7.2), or null when nothing was installed.</summary>
        public string? CanvasAddonInstalled { get; set; }
        /// <summary>How many Recovery.lua files were written this pass (spec §6.6).</summary>
        public int RecoveryWritten { get; set; }
        public TrayState State { get; set; } = TrayState.Ok;
        /// <summary>Plain words for the tooltip when the state is not Ok.</summary>
        public string Reason { get; set; } = "";
    }

    /// <summary>
    /// One pass: send every saved file that is quiet and new, then - when "Bring data back" is on - fetch the data
    /// file and put it where the addon's own Data.lua already is. The shell calls this every two seconds; it does
    /// nothing at all most of the time.
    /// </summary>
    public sealed class Cycle
    {
        public static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(30);
        /// <summary>An addon version comes out rarely; the check is cheap but there is no point being eager.</summary>
        public static readonly TimeSpan AddonEvery = TimeSpan.FromHours(6);
        /// <summary>Ruling P2-R16: a canvas manifest request that got no answer is tried again this much later.</summary>
        public static readonly TimeSpan CanvasRetryAfter = TimeSpan.FromMinutes(1);
        /// <summary>The owner's item cache changes as they play; there is no point sending it more often (spec 2026-09-26).</summary>
        public static readonly TimeSpan ItemCacheEvery = TimeSpan.FromHours(6);
        /// <summary>The server's own cap on a saved file. A bigger one is not ours.</summary>
        private const long MaxSavedBytes = 64L * 1024 * 1024;
        private static readonly Regex PricesAt = new Regex(@"\bpricesAt = (\d{9,11}),", RegexOptions.CultureInvariant);

        // The yellow states' words (tray G3): each says what actually happened. The tooltip has room for about 50.
        public const string CannotReach = "Cannot reach the server - trying again soon";
        public const string CannotWriteData = "Cannot write Data.lua - trying again soon";
        public const string BadDataFile = "The data file looked wrong - trying again soon";
        public const string FileTurnedDown = "The server turned down a saved file - see the log";

        private readonly TrayConfig config;
        private readonly ServerClient client;
        private readonly SentLog sent;
        private readonly Stability stability;
        private readonly TrayLog log;
        private readonly Func<DateTime> nowUtc;
        private readonly Backoff backoff = new Backoff();
        private readonly Dictionary<string, (long length, DateTime modified, string sha)> hashed =
            new Dictionary<string, (long, DateTime, string)>(StringComparer.OrdinalIgnoreCase);

        private DateTime notBeforeUtc = DateTime.MinValue;
        /// <summary>Why we are waiting: the words of the pass that set <see cref="notBeforeUtc"/>, said again until it passes.</summary>
        private string waitWords = CannotReach;
        /// <summary>The server turned down a saved file (400, 413, 422) and none has gone through since.</summary>
        private bool turnedDown;
        private bool refused;
        private string? etag;
        private byte[]? lastLua;
        private string? addonEtag;
        private DateTime? addonCheckedUtc;
        private bool addonUnsure;
        private string? canvasEtag;
        private DateTime? canvasCheckedUtc;
        /// <summary>The canvas fetch got no answer and that was logged: said once until an answer comes.</summary>
        private bool canvasUnansweredLogged;
        /// <summary>Each Canvas saved file read once per version, like <see cref="hashed"/>: a stable file is stable every
        /// pass, and a partial one must not be re-read and logged every two seconds until the game writes it again.</summary>
        private readonly Dictionary<string, (long length, DateTime modified, Extracted? got)> canvasRead =
            new Dictionary<string, (long, DateTime, Extracted?)>(StringComparer.OrdinalIgnoreCase);
        private DateTime? itemCacheCheckedUtc;
        private readonly string? exePath;
        private readonly ISigner? signer;
        private bool selfSignerKnown;
        private SignerInfo? selfSigner;
        private DateTime? trayCheckedUtc;
        /// <summary>The version waiting in <see cref="UpdatePaths.Staged"/>, once a pass verified and wrote it.</summary>
        public string? StagedVersion { get; private set; }
        public UpdatePaths? UpdatePaths => exePath == null ? null : SelfUpdate.Plan(exePath);
        public TrayConfig Config => config;
        /// <summary>The running build's signer, read once; null means an unsigned build, which never updates itself.</summary>
        public SignerInfo? SelfSigner
        {
            get
            {
                if (!selfSignerKnown)
                {
                    selfSignerKnown = true;
                    try { selfSigner = exePath != null && signer != null ? signer.SignerOf(exePath) : null; }
                    catch (Exception) { selfSigner = null; }
                }
                return selfSigner;
            }
        }
        /// <summary>The swap failed: the staged version is forgotten so the next pass does not try again.</summary>
        public void ForgetStaged() { StagedVersion = null; }
        private int trayMisses;
        /// <summary>Three checks in a row wanted an update and could not get it (review I4a): the nudge takes over.</summary>
        public bool TrayStalled => trayMisses >= 3;

        public DateTime? LastUploadUtc { get; private set; }
        public DateTime? LastFetchUtc { get; private set; }
        /// <summary>When the prices in the data file were scanned, for "prices from 3h ago".</summary>
        public DateTime? PricesAtUtc { get; private set; }

        /// <param name="exePath">The running program's own file, for the self-update (spec 2026-10-12); null switches it off.</param>
        /// <param name="signer">The shell's Authenticode check; null switches the self-update off.</param>
        public Cycle(TrayConfig config, ServerClient client, SentLog sent, Stability stability, TrayLog log, Func<DateTime> nowUtc, string? exePath = null, ISigner? signer = null)
        {
            this.config = config;
            this.client = client;
            this.sent = sent;
            this.stability = stability;
            this.log = log;
            this.nowUtc = nowUtc;
            this.exePath = exePath;
            this.signer = signer;
            log.Hide(config.ClientSecret, config.UploadKey);
        }

        /// <param name="force">The person asked ("Upload now"): forget a refusal and any waiting, and fetch whatever the clock says.</param>
        public async Task<CycleReport> RunAsync(bool force)
        {
            var report = new CycleReport();
            DateTime now = nowUtc();

            bool wow = GameFolders.LooksLikeWow(config.WowFolder);
            // Ruling P2-R8: every pass, Paused, refused and waiting ones included - it is local, only reads the game's saved
            // file and only writes Recovery.lua, and a server that is down at logout must not lose that session's layout.
            // Before the Canvas install below, in the same thread: the install carries Recovery.lua across.
            if (wow) KeepRecoveryCurrent(report, now);
            if (config.Paused) return Done(report, TrayState.Paused, "Paused");
            if (!wow) return Done(report, TrayState.NeedsAttention, "The World of Warcraft folder is not there");
            if (force)
            {
                refused = false;
                notBeforeUtc = DateTime.MinValue;
            }
            if (refused) return Done(report, TrayState.NeedsAttention, "The server refused this PC's credentials");
            if (now < notBeforeUtc) return Done(report, TrayState.Retrying, waitWords);

            foreach (FileInfo file in QuietSavedFiles(now))
            {
                byte[] bytes;
                try { bytes = ReadShared(file.FullName); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                string sha = HashOf(file, bytes);
                if (sent.Has(sha)) continue;

                (SendResult result, int retryAfter) = await client.SendAsync(Payload.Gzip(bytes)).ConfigureAwait(false);
                string what = file.Name + " (" + bytes.Length + " bytes, sha " + sha.Substring(0, 8) + ")";
                switch (result)
                {
                    case SendResult.Sent:
                        sent.Add(sha);
                        turnedDown = false;
                        report.Uploaded++;
                        LastUploadUtc = now;
                        log.Write("sent " + what);
                        break;
                    case SendResult.Rejected:
                        sent.Add(sha);
                        turnedDown = true;
                        report.Rejected++;
                        log.Write("the server would not take " + what + ": " + client.LastError + " - it will not be sent again");
                        break;
                    case SendResult.Refused:
                        refused = true;
                        log.Write("refused: " + client.LastError + " - stopped until asked again");
                        return Done(report, TrayState.NeedsAttention, "The server refused this PC's credentials");
                    case SendResult.Later:
                        return Wait(report, now, TimeSpan.FromSeconds(retryAfter), "asked to wait " + retryAfter + " s");
                    default:
                        return Wait(report, now, backoff.Next(), client.LastError);
                }
            }

            // Before the data file, so a fresh install's empty Data.lua is filled in this same pass. Also when updates
            // are switched off: the manifest is where the product folders come from (nothing is installed then).
            string? stop = await KeepAddonCurrent(report, now, force).ConfigureAwait(false);
            if (stop != null)
            {
                refused = true;
                log.Write("refused: " + stop + " - stopped until asked again");
                return Done(report, TrayState.NeedsAttention, "The server refused this PC's credentials");
            }
            // After this pass's recovery write (at the top): the install copies the installed Recovery.lua and Topology.lua
            // into the new folder, so nothing written before it is lost.
            await KeepCanvasAddonCurrent(report, now, force).ConfigureAwait(false);

            await KeepItemCacheSent(now, force).ConfigureAwait(false);
            await KeepTrayCurrent(report, now, force).ConfigureAwait(false);

            bool due = LastFetchUtc == null || now - LastFetchUtc.Value >= FetchEvery;
            if (config.BringDataBack && (report.Uploaded > 0 || force || due))
            {
                (FetchResult result, byte[]? lua, string? tag) = await client.FetchDataFileAsync(etag).ConfigureAwait(false);
                switch (result)
                {
                    case FetchResult.Changed:
                        lastLua = lua;
                        etag = tag;
                        LastFetchUtc = now;
                        ReadPricesAt(lua!);
                        break;
                    case FetchResult.NotModified:
                        LastFetchUtc = now;
                        break;
                    case FetchResult.Refused:
                        refused = true;
                        log.Write("refused: " + client.LastError + " - stopped until asked again");
                        return Done(report, TrayState.NeedsAttention, "The server refused this PC's credentials");
                    case FetchResult.Invalid:
                        // The server answered - just not with our file (a proxy's page, say).
                        return Wait(report, now, backoff.Next(), client.LastError, BadDataFile);
                    default:
                        return Wait(report, now, backoff.Next(), client.LastError);
                }
                if (lastLua != null && !PutInPlace(lastLua, report))
                    return Wait(report, now, backoff.Next(), "Data.lua could not be written", CannotWriteData);
            }

            backoff.Reset();
            // Not green over a scan that never arrived: yellow until a later file goes through (the log says which).
            return turnedDown ? Done(report, TrayState.Retrying, FileTurnedDown) : report;
        }

        /// <summary>
        /// Spec 2026-10-12: fetches a newer signed tray and stages it beside the exe, verified in the spec's order. The
        /// shell swaps it in after a quiet pass. A download that fails any check is deleted and its version remembered,
        /// so it is never fetched again; an unsigned running build never fetches at all.
        /// </summary>
        private async Task KeepTrayCurrent(CycleReport report, DateTime now, bool force)
        {
            if (exePath == null || signer == null || StagedVersion != null) return;
            bool signedBuild = SelfSigner != null;
            UpdateStep step = SelfUpdate.Decide(AppInfo.Version, null, config.FailedUpdateVersion, trayCheckedUtc, now, force, config.Paused, signedBuild);
            if (step == UpdateStep.Unsigned || step == UpdateStep.Paused || step == UpdateStep.NotDue) return;
            trayCheckedUtc = now;
            ServerClient.TrayOffer? offer = await client.LatestTrayAsync().ConfigureAwait(false);
            string? version = offer?.Version;
            step = SelfUpdate.Decide(AppInfo.Version, version, config.FailedUpdateVersion, null, now, true, config.Paused, signedBuild);
            if (step != UpdateStep.Fetch || version == null) return;
            if (offer?.Sha256 == null)
            {
                trayMisses++;
                log.Write("tray " + version + " is on offer without a hash - not fetched");
                return;
            }
            (FetchResult result, byte[]? body) = await client.FetchTrayAsync().ConfigureAwait(false);
            if (result != FetchResult.Changed)
            {
                trayMisses++;
                log.Write("tray " + version + " not fetched: " + client.LastError);
                if (result == FetchResult.Invalid) Fail(report, version, Refusal.TooLarge);
                return;
            }
            trayMisses = 0;
            Refusal verdict = SelfUpdate.Verify(body, offer.Sha256, SelfUpdate.MaxBytes);
            if (verdict != Refusal.Ok) { Fail(report, version, verdict); return; }
            UpdatePaths paths = SelfUpdate.Plan(exePath);
            try
            {
                if (File.Exists(paths.Staged)) File.Delete(paths.Staged);
                AtomicFile.Write(paths.Staged, body!);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                log.Write("tray " + version + " could not be staged: " + e.GetType().Name);
                Fail(report, version, Refusal.StagingFailed);
                return;
            }
            SignerInfo? signedBy;
            try { signedBy = signer.SignerOf(paths.Staged); }
            catch (Exception) { signedBy = null; }
            verdict = SelfUpdate.CheckSigner(signedBy, SelfSigner, version);
            if (verdict != Refusal.Ok)
            {
                try { File.Delete(paths.Staged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                Fail(report, version, verdict);
                return;
            }
            StagedVersion = version;
            report.TrayStaged = version;
            log.Write("tray " + version + " staged, signed by " + signedBy!.SimpleName); // a checked publisher name: safe to log
        }

        private void Fail(CycleReport report, string version, Refusal why)
        {
            config.FailedUpdateVersion = version;
            report.TrayUpdateFailed = why;
            log.Write("tray " + version + " refused: " + why);
        }

        /// <summary>
        /// Learns the product folders from the manifest, and - when that is switched on - installs the addon when it
        /// is missing and replaces it when a newer version is published. Returns null normally, or the reason when
        /// the server refused this PC - anything else is logged and let go, because a server with no addon to offer
        /// must not stop the uploads.
        /// </summary>
        private async Task<string?> KeepAddonCurrent(CycleReport report, DateTime now, bool force)
        {
            bool install = config.KeepAddonUpToDate;
            // Where it already is - those get replaced. Only when it is nowhere does a first install pick a folder,
            // and then never on a guess: a Forever addon does not belong in somebody's retail game.
            IReadOnlyList<string> folders = GameFolders.AddonFolders(config.WowFolder, config.Products);
            bool missing = folders.Count == 0;
            bool canPlace = !missing || GameFolders.InstallTarget(config.WowFolder, config.Products) != null;
            bool due = force || addonCheckedUtc == null || now - addonCheckedUtc.Value >= AddonEvery;
            // A missing addon with somewhere to go is fetched at once. Everything else waits for the cadence - also a
            // PC where none of the listed folders is there, which asks only to learn whether the list has changed.
            if (!due && !(install && missing && canPlace)) return null;

            // A missing addon has to be fetched whole, whatever our etag says about the last one we saw.
            (FetchResult result, AddonManifest? manifest, string? tag) =
                await client.FetchAddonAsync(install && missing && canPlace ? null : addonEtag).ConfigureAwait(false);
            addonCheckedUtc = now;
            if (result == FetchResult.Refused) return client.LastError;
            if (result == FetchResult.NotModified) return null;
            if (result != FetchResult.Changed || manifest == null)
            {
                log.Write("no addon to install this time: " + client.LastError);
                return null;
            }

            IReadOnlyList<string> products = ForeverProducts.Choose(config.Products, manifest.Products);
            if (!ForeverProducts.Same(products, config.Products))
            {
                config.Products = products;
                report.ProductsChanged = true;
                addonUnsure = false;
                log.Write("the game's folders are now " + string.Join(", ", products)); // checked names: safe to log
            }
            // An etag stands for "acted on": with installing switched off, the next manifest must still be read whole
            // once it is switched back on.
            if (!install) return null;
            addonEtag = tag;

            folders = GameFolders.AddonFolders(config.WowFolder, config.Products);
            if (folders.Count == 0)
            {
                string? fresh = GameFolders.InstallTarget(config.WowFolder, config.Products);
                if (fresh == null)
                {
                    if (!addonUnsure)
                    {
                        addonUnsure = true;
                        log.Write("the addon is not installed and none of the game's folders (" + string.Join(", ", config.Products) + ") is here - install it once yourself");
                    }
                    return null;
                }
                folders = new[] { fresh };
            }

            foreach (string folder in folders)
            {
                if (AddonInstaller.InstalledVersion(folder) == manifest.Version) continue;
                try
                {
                    log.Write(AddonInstaller.Install(folder, manifest));
                    report.AddonInstalled = manifest.Version;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    log.Write("could not install the addon: " + e.Message);
                }
            }
            return null;
        }

        /// <summary>Spec 2026-10-15 §7.2: the Canvas addon is installed and kept current only while the person has Canvas switched
        /// on, whatever the AH addon's update switch says (ruling P2-R12); switched off, the folder stays and is left alone.
        /// Checked on the AH addon's cadence (the first check at once); a request that got no answer is tried again after
        /// <see cref="CanvasRetryAfter"/> (ruling P2-R16).</summary>
        private async Task KeepCanvasAddonCurrent(CycleReport report, DateTime now, bool force)
        {
            if (!config.CanvasEnabled) return;
            IReadOnlyList<string> folders = GameFolders.AddonFolders(config.WowFolder, config.Products, AddonSpec.Canvas);
            bool missing = folders.Count == 0;
            bool canPlace = !missing || GameFolders.InstallTarget(config.WowFolder, config.Products, AddonSpec.Canvas) != null;
            // Ruling P2-R4: after any answered check - a 404 before the server offers Canvas included - the next one waits
            // the cadence; "Upload now" (force) retries sooner. Ruling P2-R16: a request that got no answer at all is tried
            // again after a minute (not every pass), and said in the log once until an answer comes.
            bool due = force || canvasCheckedUtc == null || now - canvasCheckedUtc.Value >= AddonEvery;
            if (!due) return;
            (FetchResult result, AddonManifest? manifest, string? tag) = await client.FetchCanvasAddonAsync(missing && canPlace ? null : canvasEtag).ConfigureAwait(false);
            if (!client.ManifestAnswered)
            {
                if (!canvasUnansweredLogged) log.Write("no canvas addon to install this time: " + client.LastError);
                canvasUnansweredLogged = true;
                canvasCheckedUtc = now - AddonEvery + CanvasRetryAfter;
                return;
            }
            canvasUnansweredLogged = false;
            canvasCheckedUtc = now;
            if (result != FetchResult.Changed || manifest == null)
            {
                if (result != FetchResult.NotModified) log.Write("no canvas addon to install this time: " + client.LastError);
                return;
            }
            canvasEtag = tag;
            if (folders.Count == 0)
            {
                string? fresh = GameFolders.InstallTarget(config.WowFolder, config.Products, AddonSpec.Canvas);
                if (fresh == null) return;
                folders = new[] { fresh };
            }
            foreach (string folder in folders)
            {
                if (AddonInstaller.InstalledVersion(folder, AddonSpec.Canvas) == manifest.Version) continue;
                try
                {
                    log.Write("canvas: " + AddonInstaller.Install(folder, manifest, AddonSpec.Canvas));
                    report.CanvasAddonInstalled = manifest.Version;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidOperationException)
                {
                    // The installer's own refusals carry checked names only; an OS message can carry a path.
                    log.Write("could not install the canvas addon: " + (e is InvalidOperationException ? e.Message : e.GetType().Name));
                }
            }
        }

        /// <summary>Spec §6.6: the newest account's revision of the Canvas saved file (the stable file written last), lifted by
        /// regex once the file is stable, becomes Recovery.lua in every installed Canvas folder - only when that revision is higher
        /// than the one in the Recovery.lua on disk, read every pass (an outside reinstall puts the placeholder back). A partial
        /// file is refused whole and simply tried again next pass.</summary>
        private void KeepRecoveryCurrent(CycleReport report, DateTime now)
        {
            if (!config.CanvasEnabled) return;
            IReadOnlyList<string> folders = GameFolders.AddonFolders(config.WowFolder, config.Products, AddonSpec.Canvas);
            if (folders.Count == 0) return;
            // Ruling P2-R5: the stable file written last decides the account (ties to the second: the higher revision).
            // An older account's higher revision never wins; when the deciding file is not usable, nothing is written
            // this pass - it is tried again on the next.
            Extracted? best = null;
            long newestSecond = long.MinValue;
            foreach (string path in GameFolders.SavedFilesOf(config.WowFolder, config.Products, AddonSpec.Canvas))
            {
                long second = long.MinValue;
                Extracted? e = null;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length == 0 || info.Length > RecoveryFile.MaxSavedBytes) continue;
                    if (!stability.IsStable(path, info.Length, info.LastWriteTimeUtc, now)) continue;
                    second = info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerSecond;
                    if (canvasRead.TryGetValue(path, out var seen) && seen.length == info.Length && seen.modified == info.LastWriteTimeUtc)
                        e = seen.got;
                    else
                    {
                        e = RecoveryFile.Extract(Encoding.UTF8.GetString(ReadShared(path)), out string reason);
                        canvasRead[path] = (info.Length, info.LastWriteTimeUtc, e);
                        if (e == null) log.Write("canvas saved file not usable yet: " + reason);
                    }
                }
                // A stable file that cannot be read still counts as written last: it is not passed over for an older one.
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (second == long.MinValue) continue;
                if (second > newestSecond) { newestSecond = second; best = e; }
                else if (second == newestSecond && e != null && (best == null || e.Revision > best.Revision)) best = e;
            }
            if (best == null) return;
            foreach (string folder in folders)
            {
                string target = Path.Combine(folder, RecoveryFile.Name);
                if (best.Revision <= RevisionOnDisk(target)) continue;
                try
                {
                    AtomicFile.Write(target, RecoveryFile.Bytes(RecoveryFile.Render(best.Revision, best.Base64)));
                    report.RecoveryWritten++;
                    log.Write("wrote Recovery.lua, revision " + best.Revision);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log.Write("could not write Recovery.lua: " + e.GetType().Name);
                }
            }
        }

        private static readonly Regex RecoveryRevision = new Regex(@"revision = ([0-9]{1,9}),", RegexOptions.CultureInvariant);
        private static int RevisionOnDisk(string path)
        {
            try
            {
                if (!File.Exists(path)) return -1;
                Match m = RecoveryRevision.Match(File.ReadAllText(path, Encoding.UTF8));
                // [0-9], not \d, and TryParse: a hand-edited file must never throw out of the pass.
                return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : -1;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return -1; }
        }

        /// <summary>
        /// Only when the server says this is the owner's install (spec 2026-09-26, decision 6): the item rows of the game's
        /// own cache, filtered here so nothing else in that file ever leaves this PC. Anything that goes wrong is logged and
        /// let go - the item cache must never stop the uploads that matter more.
        /// </summary>
        private async Task KeepItemCacheSent(DateTime now, bool force)
        {
            IReadOnlyList<(string Product, string Path)> files = GameFolders.ItemCacheFiles(config.WowFolder, config.Products);
            if (files.Count == 0) return;
            if (!force && itemCacheCheckedUtc != null && now - itemCacheCheckedUtc.Value < ItemCacheEvery) return;
            itemCacheCheckedUtc = now;
            if (!await client.ItemCacheWantedAsync().ConfigureAwait(false)) return;
            foreach ((string product, string path) in files)
            {
                byte[]? rows;
                try
                {
                    // The saved-file cap: a bigger DBCache.bin is not one we should be reading at all.
                    if (new FileInfo(path).Length > MaxSavedBytes)
                    {
                        log.Write("the game's item cache for " + product + " is too large to send");
                        continue;
                    }
                    rows = ItemCache.Filter(ReadShared(path));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    log.Write("could not read the game's item cache for " + product + ": " + e.GetType().Name);
                    continue;
                }
                if (rows == null) continue; // nothing of the allowed tables in it - not a failure, so silent
                string sha = "itemcache" + Payload.Sha256Hex(rows); // no separator: SentLog keeps letters and digits only
                if (sent.Has(sha)) continue;
                (SendResult result, int _) = await client.SendItemCacheAsync(product, Payload.Gzip(rows)).ConfigureAwait(false);
                switch (result)
                {
                    case SendResult.Sent:
                        sent.Add(sha);
                        log.Write("sent the game's item cache for " + product + " (" + rows.Length + " bytes)"); // a checked product name
                        break;
                    case SendResult.Rejected:
                        sent.Add(sha);
                        log.Write("the server would not take the item cache for " + product + ": " + client.LastError + " - it will not be sent again");
                        break;
                    default:
                        log.Write("the item cache was not taken: " + client.LastError);
                        break;
                }
            }
        }

        private IEnumerable<FileInfo> QuietSavedFiles(DateTime now)
        {
            var quiet = new List<FileInfo>();
            foreach (string path in GameFolders.SavedFiles(config.WowFolder, config.Products))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length == 0 || info.Length > MaxSavedBytes) continue;
                    if (stability.IsStable(path, info.Length, info.LastWriteTimeUtc, now)) quiet.Add(info);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return quiet.OrderByDescending(f => f.LastWriteTimeUtc);
        }

        /// <summary>Hashed once per version of the file, not every two seconds.</summary>
        private string HashOf(FileInfo file, byte[] bytes)
        {
            if (hashed.TryGetValue(file.FullName, out var h) && h.length == file.Length && h.modified == file.LastWriteTimeUtc) return h.sha;
            string sha = Payload.Sha256Hex(bytes);
            hashed[file.FullName] = (file.Length, file.LastWriteTimeUtc, sha);
            return sha;
        }

        /// <summary>Every Data.lua that already exists and differs. False when one could not be written.</summary>
        private bool PutInPlace(byte[] lua, CycleReport report)
        {
            bool ok = true;
            foreach (string path in GameFolders.DataFiles(config.WowFolder, config.Products))
            {
                try
                {
                    if (ReadShared(path).SequenceEqual(lua)) continue;
                    AtomicFile.Write(path, lua);
                    report.Wrote++;
                    log.Write("wrote Data.lua (" + lua.Length + " bytes)");
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    ok = false;
                    log.Write("could not write Data.lua: " + e.GetType().Name);
                }
            }
            return ok;
        }

        private void ReadPricesAt(byte[] lua)
        {
            Match m = PricesAt.Match(Encoding.UTF8.GetString(lua));
            PricesAtUtc = m.Success && long.TryParse(m.Groups[1].Value, out long at)
                ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(at)
                : (DateTime?)null;
        }

        private static byte[] ReadShared(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        private CycleReport Wait(CycleReport report, DateTime now, TimeSpan wait, string why, string words = CannotReach)
        {
            notBeforeUtc = now + wait;
            waitWords = words;
            log.Write("waiting " + (int)wait.TotalSeconds + " s: " + why);
            return Done(report, TrayState.Retrying, words);
        }

        private static CycleReport Done(CycleReport report, TrayState state, string reason)
        {
            report.State = state;
            report.Reason = reason;
            return report;
        }
    }
}
