using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Xunit;

namespace Crucible.Tray.Tests
{
    /// <summary>Client caches built byte by byte in the XFTH version 9 layout - never a real client file.</summary>
    internal static class Xfth
    {
        public static byte[] Cache(uint build, params (uint Table, byte Status, int Size)[] entries)
        {
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("XFTH"));
            bytes.AddRange(BitConverter.GetBytes(9u));
            bytes.AddRange(BitConverter.GetBytes(build));
            bytes.AddRange(new byte[32]);
            for (int i = 0; i < entries.Length; i++)
            {
                bytes.AddRange(Encoding.ASCII.GetBytes("XFTH"));
                bytes.AddRange(BitConverter.GetBytes(-1));
                bytes.AddRange(BitConverter.GetBytes(0x1000000 + i));
                bytes.AddRange(BitConverter.GetBytes((uint)i));
                bytes.AddRange(BitConverter.GetBytes(entries[i].Table));
                bytes.AddRange(BitConverter.GetBytes((uint)(90000 + i)));
                bytes.AddRange(BitConverter.GetBytes((uint)entries[i].Size));
                bytes.Add(entries[i].Status);
                bytes.AddRange(new byte[3]);
                for (int j = 0; j < entries[i].Size; j++) bytes.Add((byte)(i + 1));
            }
            return bytes.ToArray();
        }

        /// <summary>Each entry's (table hash, first data byte) - enough to say which rows a cache holds.</summary>
        public static List<(uint Table, byte First)> Rows(byte[] cache)
        {
            var rows = new List<(uint, byte)>();
            int offset = 44;
            while (offset + 32 <= cache.Length)
            {
                uint table = BitConverter.ToUInt32(cache, offset + 16);
                int size = (int)BitConverter.ToUInt32(cache, offset + 24);
                rows.Add((table, size > 0 ? cache[offset + 32] : (byte)0));
                offset += 32 + size;
            }
            return rows;
        }
    }

    public class ItemCacheTests
    {
        private const uint ItemSparse = 0x919BE54E, Item = 0x50238EC2, TactKey = 0xDF2F53CF, BroadcastText = 0x021826BB;

        [Fact]
        public void Only_whole_valid_rows_of_the_item_tables_are_kept_behind_the_same_header()
        {
            byte[] file = Xfth.Cache(70009, (TactKey, 1, 24), (ItemSparse, 1, 10), (BroadcastText, 1, 5), (Item, 1, 4), (ItemSparse, 3, 0));
            byte[] kept = ItemCache.Filter(file)!;
            Assert.Equal(file.Take(44), kept.Take(44));
            // entries 1 and 3 of the file: their own bytes, in file order - never the keys, never another table, never a dead row
            Assert.Equal(new[] { (ItemSparse, (byte)2), (Item, (byte)4) }, Xfth.Rows(kept));
        }

        [Fact]
        public void A_torn_last_entry_is_left_behind()
        {
            byte[] file = Xfth.Cache(70009, (ItemSparse, 1, 10), (Item, 1, 4));
            byte[] torn = file.Take(file.Length - 2).ToArray();
            Assert.Equal(new[] { (ItemSparse, (byte)1) }, Xfth.Rows(ItemCache.Filter(torn)!));
        }

        [Fact]
        public void Anything_else_gives_nothing_to_send()
        {
            Assert.Null(ItemCache.Filter(Xfth.Cache(70009, (TactKey, 1, 24), (BroadcastText, 1, 5))));
            Assert.Null(ItemCache.Filter(Xfth.Cache(70009)));
            byte[] other = Xfth.Cache(70009, (ItemSparse, 1, 10));
            other[4] = 8; // another format version
            Assert.Null(ItemCache.Filter(other));
            Assert.Null(ItemCache.Filter(Encoding.ASCII.GetBytes("CrucibleDB = {}")));
        }
    }
}
