using System.Text.RegularExpressions;

namespace Crucible.Tray
{
    /// <summary>
    /// M112: telling the person that a newer tray app is on offer - never fetching it (C3: a notification, never a
    /// self-update). The daily version check hands the server's answer to <see cref="Offer"/>; this decides what the
    /// shell shows: the tooltip's first line, the attention badge and the menu line while a newer one is on offer, and
    /// ONE balloon per new version. The version a balloon was shown for is kept in the config, so a restart (every
    /// Windows start, with autostart on) does not show it again. The menu line opens a fixed address -
    /// <see cref="DownloadPage"/> of the website in the person's own settings file - never a URL from the server's answer.
    /// </summary>
    public sealed class UpdateNudge
    {
        /// <summary>The shape <see cref="ServerClient.LatestVersionAsync"/> accepts. \z, not $: no trailing newline.</summary>
        private static readonly Regex Shape = new Regex(@"^\d{1,4}\.\d{1,4}\.\d{1,4}\z", RegexOptions.CultureInvariant);

        private readonly string mine;

        /// <param name="mine">This build's version (<see cref="AppInfo.Version"/>).</param>
        /// <param name="alreadyNudged">The version the last balloon was for, from the config; anything not shaped like a version counts as none.</param>
        public UpdateNudge(string mine, string? alreadyNudged)
        {
            this.mine = mine;
            Nudged = IsVersion(alreadyNudged) ? alreadyNudged : null;
        }

        /// <summary>The newer version on offer, or null when there is none.</summary>
        public string? Available { get; private set; }

        /// <summary>The version the last balloon was for - for the config.</summary>
        public string? Nudged { get; private set; }

        public static bool IsVersion(string? s) => s != null && Shape.IsMatch(s);

        /// <summary>
        /// The server's answer to the daily check: a version, or null when it gave none (unreachable, refused, or an
        /// answer not shaped like a version) - then what was known is kept. True when a balloon is due now; <see
        /// cref="Nudged"/> has then moved to that version and the config should be saved.
        /// </summary>
        public bool Offer(string? offered)
        {
            if (offered == null) return false;
            if (!ServerClient.IsNewer(mine, offered))
            {
                Available = null;
                return false;
            }
            Available = offered;
            // Once per new version: not again for the same one, nor for an older one the server went back to.
            if (Nudged != null && !ServerClient.IsNewer(Nudged, offered)) return false;
            Nudged = offered;
            return true;
        }

        /// <summary>The tooltip's first line while a newer version is on offer.</summary>
        public string? TooltipLine => Available == null ? null : "Update available: " + Available;

        /// <summary>The right-click menu line that opens the download page.</summary>
        public string? MenuText => Available == null ? null : "Download Crucible " + Available + " (opens the download page)";

        public string? BalloonText => Available == null
            ? null
            : "Crucible " + Available + " is ready to download. Click here, or right-click the Crucible icon and choose Download, to open the download page.";

        /// <summary>The website's download page: the address from the person's own settings file, plus /setup.</summary>
        public static string DownloadPage(string ui) => ui.TrimEnd('/') + "/setup";
    }

    /// <summary>The tray icon's tooltip, in words. Windows takes at most 63 characters.</summary>
    public static class TrayText
    {
        public const int MaxTooltip = 63;

        /// <param name="updateLine">The first line while a newer version is on offer (<see cref="UpdateNudge.TooltipLine"/>), or null.</param>
        public static string Tooltip(TrayState state, string reason, System.DateTime? lastUploadUtc, System.DateTime? pricesAtUtc, string? updateLine, System.DateTime nowUtc)
        {
            string text;
            if (state == TrayState.Ok)
            {
                text = "Crucible - up to date";
                if (lastUploadUtc != null) text += ". Sent " + Ago(lastUploadUtc.Value, nowUtc);
                if (pricesAtUtc != null) text += "; prices " + Ago(pricesAtUtc.Value, nowUtc);
            }
            else
            {
                text = "Crucible - " + reason;
            }
            if (updateLine != null) text = updateLine + "\n" + text;
            return text.Length > MaxTooltip ? text.Substring(0, MaxTooltip - 1) + "…" : text;
        }

        public static string Ago(System.DateTime utc, System.DateTime nowUtc)
        {
            System.TimeSpan t = nowUtc - utc;
            if (t.TotalMinutes < 1) return "just now";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "m ago";
            if (t.TotalDays < 1) return (int)t.TotalHours + "h ago";
            return (int)t.TotalDays + "d ago";
        }
    }
}
