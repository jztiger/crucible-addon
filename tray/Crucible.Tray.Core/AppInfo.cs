namespace Crucible.Tray
{
    /// <summary>What this build is. The server compares Version with what it has on offer.</summary>
    public static class AppInfo
    {
        public const string Name = "Crucible";
        public const string Version = "0.99.4";
        /// <summary>
        /// The signer Windows shows for this program - the simple name on the owner's Azure Artifact Signing
        /// certificate (CI writes the same value to publisher.txt). A download whose signer is anyone else is
        /// not an update (spec 2026-10-12, section 2).
        /// </summary>
        public const string Publisher = "Jason Howard";
        /// <summary>An ordinary User-Agent: Cloudflare challenges odd ones.</summary>
        public const string UserAgent = "Crucible-Tray/" + Version;
    }
}
