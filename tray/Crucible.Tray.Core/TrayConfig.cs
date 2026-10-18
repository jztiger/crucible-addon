using System.Collections.Generic;

namespace Crucible.Tray
{
    /// <summary>Everything the tray app is told or asked. The three credentials come from the member's own download.</summary>
    public sealed class TrayConfig
    {
        /// <summary>Where tray apps talk to the server (its own Cloudflare Access application).</summary>
        public string Api { get; set; } = "";
        /// <summary>The website, for "Open Crucible". Never sent anything by this program.</summary>
        public string Ui { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string ClientSecret { get; set; } = "";
        public string UploadKey { get; set; } = "";

        /// <summary>The folder the person picked. Never detected, never guessed (C4).</summary>
        public string WowFolder { get; set; } = "";
        public bool BringDataBack { get; set; } = true;
        /// <summary>Install the addon when it is missing, and replace it when a newer version is published.</summary>
        public bool KeepAddonUpToDate { get; set; } = true;
        public bool StartWithWindows { get; set; } = true;
        /// <summary>Which version of the notice they accepted. 0 means they have not seen one.</summary>
        public int AcceptedNoticeVersion { get; set; }
        public bool Paused { get; set; }
        /// <summary>The tray version the last "update available" balloon was for (M112), so it is shown once; "" for none.</summary>
        public string NudgedVersion { get; set; } = "";
        /// <summary>Spec 2026-10-12: the tray version whose download or swap last failed - never tried again until a newer one is offered. "" = none.</summary>
        public string FailedUpdateVersion { get; set; } = "";
        /// <summary>
        /// Forever's product folders: the last list the server sent, so this works offline too. Replaced whole,
        /// never changed in place - a pass reads it on another thread.
        /// </summary>
        public IReadOnlyList<string> Products { get; set; } = ForeverProducts.Default;

        /// <summary>Spec 2026-10-15 (Crucible Canvas): the Displays window's choices. All off/empty until the person turns it on.</summary>
        public bool CanvasEnabled { get; set; }
        public string CanvasMode { get; set; } = "DUAL";
        public IReadOnlyList<string> CanvasDisplays { get; set; } = new string[0];
        public string CanvasMainhand { get; set; } = "";
        public string CanvasSplitSide { get; set; } = "left";
        public bool CanvasAutoSpan { get; set; }
        public int CanvasSpanDelaySeconds { get; set; } = 10;
        /// <summary>The notice version last shown in the Close-only window (spec §8); 0 = never.</summary>
        public int NoticeShownVersion { get; set; }
        public CanvasChoice CanvasChoice() => new CanvasChoice { Mode = CanvasMode, DisplayIds = CanvasDisplays, MainhandId = CanvasMainhand.Length == 0 ? null : CanvasMainhand, SplitSide = CanvasSplitSide };

        /// <summary>Safe to log: no credential, no folder.</summary>
        public override string ToString() =>
            "TrayConfig(api=" + Api + ", bringDataBack=" + BringDataBack + ", paused=" + Paused + ")";
    }
}
