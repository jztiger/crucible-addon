using System.Collections.Generic;

namespace Crucible.Tray
{
    /// <summary>One addon this program installs: its folder/toc/saved-file name and the generated files an update must carry across.</summary>
    public sealed class AddonSpec
    {
        public string Name { get; }
        public string TocName => Name + ".toc";
        public string SavedName => Name + ".lua";
        public IReadOnlyList<string> CarriedFiles { get; }
        private AddonSpec(string name, IReadOnlyList<string> carried) { Name = name; CarriedFiles = carried; }
        /// <summary>The AH addon: the player's own prices in Data.lua come across an update.</summary>
        public static readonly AddonSpec Crucible = new AddonSpec("Crucible", new[] { "Data.lua" });
        /// <summary>Crucible Canvas (spec 2026-10-15 §7.2): the tray's own two generated files come across.</summary>
        public static readonly AddonSpec Canvas = new AddonSpec("CrucibleCanvas", new[] { "Topology.lua", "Recovery.lua" });
    }
}
