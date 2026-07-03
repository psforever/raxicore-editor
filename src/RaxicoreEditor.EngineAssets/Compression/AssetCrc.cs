using System;

namespace RaxicoreEditor.EngineAssets.Compression
{
    /// <summary>
    /// CRC routines ported from the engine-derived reference implementation's <c>asset_crc.cpp</c>.
    /// </summary>
    public static class AssetCrc
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint c = i;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                }
                t[i] = c;
            }
            return t;
        }

        /// <summary>Standard reflected CRC-32 (poly 0xEDB88320, init/final 0xFFFFFFFF).</summary>
        public static uint Crc32(ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
            }
            return ~crc;
        }

        /// <summary>
        /// <c>.pak</c> record integrity CRC: seed = stream length, NO init/final inversion, final
        /// mask 0x7FFFFFFF, computed over the COMPRESSED bytes.
        /// </summary>
        public static uint PakCrc(ReadOnlySpan<byte> compressed)
        {
            uint acc = (uint)compressed.Length;
            foreach (byte b in compressed)
            {
                acc = (acc >> 8) ^ Table[(acc ^ b) & 0xFF];
            }
            return acc & 0x7FFFFFFFu;
        }
    }
}
