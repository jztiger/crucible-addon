using System.Collections.Generic;
using System.IO;

namespace Tallybook.Tray
{
    /// <summary>
    /// The game's own item cache (spec 2026-09-26, decision 6): &lt;product&gt;\Cache\ADB\enUS\DBCache.bin, the rows
    /// Forever's server sends this client, cut down to the item tables' rows before anything leaves this PC. The file
    /// also holds Blizzard's encryption keys and dozens of other tables; none of that is ever copied into what is sent.
    /// Only the owner's install sends it, and only when the server asks (<see cref="ServerClient.ItemCacheWantedAsync"/>).
    /// </summary>
    public static class ItemCache
    {
        /// <summary>By WoWDBDefs table hash - the same list the server checks against (src/server/itemdata/item-cache.ts).</summary>
        private static readonly HashSet<uint> Allowed = new HashSet<uint>
        {
            0x919BE54E, // ItemSparse
            0x50238EC2, // Item
            0x95D8638F, // ItemSubClass
            0xE491AC55, // ItemModifiedAppearance
            0x42261B89, // ItemAppearance
            0x00CB674F, // ItemXItemEffect
            0x4002A5B1, // ItemEffect
            0xE111669E, // Spell
            0xF04238A5, // SpellEffect
            0x8318900A, // ItemBonus
            0x70C2E7FD, // ItemNameDescription
        };

        private const uint Magic = 0x48544658; // "XFTH", little-endian
        private const uint Version = 9;
        private const int HeaderBytes = 44;
        private const int EntryBytes = 32;
        private const byte Valid = 1;

        /// <summary>
        /// The cache's own 44-byte header, then only its whole, valid rows of the allowed tables, in file order. Null when
        /// the file is not an XFTH version 9 cache or holds no such row. A torn last entry - the game writing while this
        /// reads - ends the scan: only whole entries are ever kept.
        /// </summary>
        public static byte[]? Filter(byte[] file)
        {
            if (file.Length < HeaderBytes || U32(file, 0) != Magic || U32(file, 4) != Version) return null;
            using (var kept = new MemoryStream())
            {
                kept.Write(file, 0, HeaderBytes);
                int rows = 0;
                long offset = HeaderBytes;
                while (offset + EntryBytes <= file.Length && U32(file, (int)offset) == Magic)
                {
                    long end = offset + EntryBytes + U32(file, (int)offset + 24);
                    if (end > file.Length) break;
                    if (Allowed.Contains(U32(file, (int)offset + 16)) && file[offset + 28] == Valid)
                    {
                        kept.Write(file, (int)offset, (int)(end - offset));
                        rows++;
                    }
                    offset = end;
                }
                return rows == 0 ? null : kept.ToArray();
            }
        }

        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
    }
}
