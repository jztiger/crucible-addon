using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace Crucible.Tray
{
    /// <summary>
    /// The config file: the member's download once (plain, beside the exe), then %APPDATA%\Crucible\config.json
    /// for good, with the two credentials sealed by <see cref="ISecretProtector"/>.
    /// </summary>
    public static class ConfigStore
    {
        /// <summary>Marks a sealed value, so a plain one is never mistaken for it (and the other way round).</summary>
        private const string Sealed = "p1:";

        [DataContract]
        private sealed class Stored
        {
            [DataMember(Name = "api", Order = 1)] public string? Api { get; set; }
            [DataMember(Name = "ui", Order = 2)] public string? Ui { get; set; }
            [DataMember(Name = "clientId", Order = 3)] public string? ClientId { get; set; }
            [DataMember(Name = "clientSecret", Order = 4)] public string? ClientSecret { get; set; }
            [DataMember(Name = "uploadKey", Order = 5)] public string? UploadKey { get; set; }
            [DataMember(Name = "wowFolder", Order = 6, EmitDefaultValue = false)] public string? WowFolder { get; set; }
            [DataMember(Name = "bringDataBack", Order = 7, EmitDefaultValue = false)] public bool? BringDataBack { get; set; }
            [DataMember(Name = "keepAddonUpToDate", Order = 12, EmitDefaultValue = false)] public bool? KeepAddonUpToDate { get; set; }
            [DataMember(Name = "startWithWindows", Order = 8, EmitDefaultValue = false)] public bool? StartWithWindows { get; set; }
            [DataMember(Name = "acceptedNotice", Order = 9, EmitDefaultValue = false)] public bool? AcceptedNotice { get; set; }
            [DataMember(Name = "acceptedNoticeVersion", Order = 11, EmitDefaultValue = false)] public int? AcceptedNoticeVersion { get; set; }
            [DataMember(Name = "paused", Order = 10, EmitDefaultValue = false)] public bool? Paused { get; set; }
            [DataMember(Name = "products", Order = 13, EmitDefaultValue = false)] public List<string?>? Products { get; set; }
            [DataMember(Name = "updateNudged", Order = 14, EmitDefaultValue = false)] public string? UpdateNudged { get; set; }
            [DataMember(Name = "failedUpdate", Order = 15, EmitDefaultValue = false)] public string? FailedUpdate { get; set; }
            // Spec 2026-10-15 (Crucible Canvas) section 6.7: all optional, so an older tray ignores them and a config never used with Canvas gains no keys.
            // The spec's names and spellings (ruling P2-R11): canvasMode is "dual"/"split" on disk, "DUAL"/"SPLIT" in the core.
            [DataMember(Name = "canvasEnabled", Order = 16, EmitDefaultValue = false)] public bool? CanvasEnabled { get; set; }
            [DataMember(Name = "canvasMode", Order = 17, EmitDefaultValue = false)] public string? CanvasMode { get; set; }
            [DataMember(Name = "canvasDisplays", Order = 18, EmitDefaultValue = false)] public List<string?>? CanvasDisplays { get; set; }
            [DataMember(Name = "canvasMainhand", Order = 19, EmitDefaultValue = false)] public string? CanvasMainhand { get; set; }
            [DataMember(Name = "canvasSplitSide", Order = 20, EmitDefaultValue = false)] public string? CanvasSplitSide { get; set; }
            [DataMember(Name = "canvasAutoSpan", Order = 21, EmitDefaultValue = false)] public bool? CanvasAutoSpan { get; set; }
            [DataMember(Name = "canvasSpanDelaySeconds", Order = 22, EmitDefaultValue = false)] public int? CanvasSpanDelaySeconds { get; set; }
            [DataMember(Name = "noticeShownVersion", Order = 23, EmitDefaultValue = false)] public int? NoticeShownVersion { get; set; }
        }

        /// <summary>The server's crucible.config.json. Null when it is not a complete one.</summary>
        public static TrayConfig? ImportDownload(string downloadJson)
        {
            Stored? s = Json.Read<Stored>(downloadJson);
            if (s == null) return null;
            TrayConfig c = FromStored(s, s.ClientSecret ?? "", s.UploadKey ?? "");
            return IsComplete(c) ? c : null;
        }

        /// <summary>Null when there is no file, it is damaged, or its secrets were not sealed by this user.</summary>
        public static TrayConfig? Load(string path, ISecretProtector protector)
        {
            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            Stored? s = Json.Read<Stored>(text);
            if (s == null) return null;
            string? secret = Open(s.ClientSecret, protector);
            string? key = Open(s.UploadKey, protector);
            if (secret == null || key == null) return null;
            TrayConfig c = FromStored(s, secret, key);
            return IsComplete(c) ? c : null;
        }

        public static void Save(string path, TrayConfig c, ISecretProtector protector)
        {
            var s = new Stored
            {
                Api = c.Api,
                Ui = c.Ui,
                ClientId = c.ClientId,
                ClientSecret = Sealed + protector.Protect(c.ClientSecret),
                UploadKey = Sealed + protector.Protect(c.UploadKey),
                WowFolder = c.WowFolder,
                BringDataBack = c.BringDataBack,
                KeepAddonUpToDate = c.KeepAddonUpToDate,
                StartWithWindows = c.StartWithWindows,
                AcceptedNoticeVersion = c.AcceptedNoticeVersion,
                Paused = c.Paused,
                Products = new List<string?>(c.Products),
                UpdateNudged = c.NudgedVersion.Length == 0 ? null : c.NudgedVersion,
                FailedUpdate = c.FailedUpdateVersion.Length == 0 ? null : c.FailedUpdateVersion,
                CanvasEnabled = c.CanvasEnabled ? true : (bool?)null,
                CanvasMode = c.CanvasEnabled || c.CanvasDisplays.Count > 0 ? (c.CanvasMode == "SPLIT" ? "split" : "dual") : null,
                CanvasDisplays = c.CanvasDisplays.Count == 0 ? null : new List<string?>(c.CanvasDisplays),
                CanvasMainhand = c.CanvasMainhand.Length == 0 ? null : c.CanvasMainhand,
                CanvasSplitSide = c.CanvasEnabled || c.CanvasDisplays.Count > 0 ? c.CanvasSplitSide : null,
                CanvasAutoSpan = c.CanvasAutoSpan ? true : (bool?)null,
                CanvasSpanDelaySeconds = c.CanvasEnabled || c.CanvasDisplays.Count > 0 ? c.CanvasSpanDelaySeconds : (int?)null,
                NoticeShownVersion = c.NoticeShownVersion == 0 ? (int?)null : c.NoticeShownVersion,
            };
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            AtomicFile.Write(path, Encoding.UTF8.GetBytes(Json.Write(s)));
        }

        /// <summary>
        /// The rename (2026-10-04): the first run of Crucible on a PC that ran Tallybook. The old program's config is read
        /// once - opened with ITS protector - and written under the new name, sealed with the new one; the old file stays
        /// where it is. Only when there is no new config at all: older settings never overwrite the new name's file, not
        /// even a damaged one. Null when nothing was moved. Remove at 1.0.0, when the old file is deleted.
        /// </summary>
        public static TrayConfig? MoveFromOldName(string oldPath, ISecretProtector oldProtector, string newPath, ISecretProtector newProtector)
        {
            try
            {
                if (File.Exists(newPath)) return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return null;
            }
            TrayConfig? old = Load(oldPath, oldProtector);
            if (old == null) return null;
            ReplaceOldHosts(old, false);
            Save(newPath, old, newProtector);
            return old;
        }

        private const string OldUiHost = "tally.tigerforge.io";
        private const string OldApiHost = "tally-api.tigerforge.io";
        private const string NewUiHost = "crucible.tigerforge.io";
        private const string NewApiHost = "crucible-api.tigerforge.io";

        /// <summary>
        /// The rename's addresses (2026-10-04): the two old hostnames are replaced by the new ones, matched as the whole host
        /// (scheme, port and path stay; any other host is left alone). Used only on a config the program itself carried over
        /// from the old name, never on one the member loaded. With <paramref name="bothMustBeOld"/> nothing changes unless
        /// BOTH addresses are exactly the two old ones (the 0.99.1 config that already went across). True when it changed
        /// something. Remove at 1.0.0 with the old hostnames.
        /// </summary>
        public static bool ReplaceOldHosts(TrayConfig c, bool bothMustBeOld)
        {
            string? api = WithHost(c.Api, OldApiHost, NewApiHost);
            string? ui = WithHost(c.Ui, OldUiHost, NewUiHost);
            if (bothMustBeOld && (api == null || ui == null)) return false;
            if (api != null) c.Api = api;
            if (ui != null) c.Ui = ui;
            return api != null || ui != null;
        }

        /// <summary>The address with its host replaced, or null when its host is not exactly <paramref name="from"/>.</summary>
        private static string? WithHost(string url, string from, string to)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u) || u.Host != from) return null;
            return new UriBuilder(u) { Host = to }.Uri.AbsoluteUri.TrimEnd('/');
        }

        /// <summary>
        /// Takes the credentials of a newer settings file and keeps what the person chose: the folder, the switches, the notice they accepted.
        /// A download does NOT retire anything: each settings file carries its own upload key and the others keep working
        /// (keys belong to installs, not members). Changes <paramref name="target"/> in
        /// place, so whatever already holds it - the running client - uses the new credentials at its next request.
        /// </summary>
        public static void ApplyCredentials(TrayConfig target, TrayConfig from)
        {
            target.Api = from.Api;
            target.Ui = from.Ui;
            target.ClientId = from.ClientId;
            target.ClientSecret = from.ClientSecret;
            target.UploadKey = from.UploadKey;
        }

        /// <summary>Two origins the credentials may be sent to, and the three credentials.</summary>
        public static bool IsComplete(TrayConfig c) =>
            IsSafeOrigin(c.Api) && IsSafeOrigin(c.Ui) && c.ClientId.Length > 0 && c.ClientSecret.Length > 0 && c.UploadKey.Length > 0;

        private static TrayConfig FromStored(Stored s, string clientSecret, string uploadKey) => new TrayConfig
        {
            Api = (s.Api ?? "").TrimEnd('/'),
            Ui = (s.Ui ?? "").TrimEnd('/'),
            ClientId = s.ClientId ?? "",
            ClientSecret = clientSecret,
            UploadKey = uploadKey,
            WowFolder = s.WowFolder ?? "",
            BringDataBack = s.BringDataBack ?? true,
            KeepAddonUpToDate = s.KeepAddonUpToDate ?? true,
            StartWithWindows = s.StartWithWindows ?? true,
            // A config written before the notice had versions: what they accepted was version 1.
            AcceptedNoticeVersion = s.AcceptedNoticeVersion ?? ((s.AcceptedNotice ?? false) ? 1 : 0),
            Paused = s.Paused ?? false,
            // A file from before the list has none; a hand-edited one may hold names that must never become paths.
            Products = ForeverProducts.Choose(ForeverProducts.Default, s.Products),
            // Only ever shown and compared, but a hand-edited file may hold anything: what is not a version is none.
            NudgedVersion = UpdateNudge.IsVersion(s.UpdateNudged) ? s.UpdateNudged! : "",
            FailedUpdateVersion = UpdateNudge.IsVersion(s.FailedUpdate) ? s.FailedUpdate! : "",
            // Canvas (spec 2026-10-15 section 6.7): a hand-edited file is held to the shapes; never null, since CanvasChoice() reads them.
            CanvasEnabled = s.CanvasEnabled ?? false,
            CanvasMode = string.Equals(s.CanvasMode, "split", StringComparison.OrdinalIgnoreCase) ? "SPLIT" : "DUAL",
            CanvasDisplays = (s.CanvasDisplays ?? new List<string?>()).Where(d => !string.IsNullOrEmpty(d) && d!.Length <= 512).Select(d => d!).ToList(),
            CanvasMainhand = s.CanvasMainhand != null && s.CanvasMainhand.Length <= 512 ? s.CanvasMainhand : "",
            CanvasSplitSide = s.CanvasSplitSide == "right" ? "right" : "left",
            CanvasAutoSpan = s.CanvasAutoSpan ?? false,
            CanvasSpanDelaySeconds = Math.Max(0, Math.Min(60, s.CanvasSpanDelaySeconds ?? 10)),
            NoticeShownVersion = Math.Max(0, s.NoticeShownVersion ?? 0),
        };

        private static string? Open(string? stored, ISecretProtector protector)
        {
            if (stored == null || !stored.StartsWith(Sealed, StringComparison.Ordinal)) return null;
            try { return protector.Unprotect(stored.Substring(Sealed.Length)); }
            catch (Exception) { return null; } // whatever the protector throws, the answer is "not a config"
        }

        /// <summary>
        /// https anywhere; plain http only to this PC itself (the rehearsal, where a stand-in for Cloudflare runs on
        /// a loopback port) - so the credentials can never cross a network in the clear.
        /// </summary>
        private static bool IsSafeOrigin(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? u)) return false;
            if (u.AbsolutePath != "/" || u.Query.Length != 0 || u.Fragment.Length != 0 || u.UserInfo.Length != 0 || u.Host.Length == 0) return false;
            if (u.Scheme == Uri.UriSchemeHttps) return true;
            return u.Scheme == Uri.UriSchemeHttp && (u.Host == "127.0.0.1" || u.Host == "localhost");
        }
    }
}
