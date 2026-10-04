namespace Crucible.Tray
{
    /// <summary>What this build is. The server compares Version with what it has on offer.</summary>
    public static class AppInfo
    {
        public const string Name = "Crucible";
        public const string Version = "0.99.1";
        /// <summary>An ordinary User-Agent: Cloudflare challenges odd ones.</summary>
        public const string UserAgent = "Crucible-Tray/" + Version;
    }
}
