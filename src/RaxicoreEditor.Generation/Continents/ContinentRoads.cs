using System.Text;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Surfaces;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Extracts a continent's road network from its per-tile surface grids (<c>mapNN_srf.pak</c>).
    ///
    /// Roads are painted into the surface layer, not modelled as geometry -- the terrain meshes carry no
    /// road material at all -- so they have to come from the <c>.srf</c> cells. The pak's first entry
    /// (<c>mapNN.srf</c>) holds the per-continent surface-type NAME list; every other entry is a
    /// <c>mapNN&lt;CC&gt;&lt;RR&gt;.srf</c> tile of 128x128 cells at 2 world units each.
    ///
    /// IMPORTANT -- the cell's <c>type</c> byte is the name-list index PLUS ONE, not the index itself.
    /// Verified against facility placements as ground truth: taking the byte as a direct index puts
    /// 0/17 of Cyssor's and 0/13 of Esamir's facilities on the <c>base</c> surface, while the +1 mapping
    /// puts 17/17 and 13/13 on it. Reading it directly also produced obvious nonsense (37.8% of arctic
    /// Esamir typed as "road", 0% of Cyssor), which the corrected mapping resolves to ~2% on both.
    /// </summary>
    internal sealed class ContinentRoads
    {
        public int N { get; private init; }
        public int Cells { get; private init; }
        public byte[] Mask { get; private init; } = Array.Empty<byte>();

        public static ContinentRoads? Build(string srfPakPath, string baseName, int n, int worldSize)
        {
            PakArchive pak;
            try { pak = PakArchive.Load(File.ReadAllBytes(srfPakPath)); }
            catch { return null; }

            string[] names = ReadTypeNames(pak, baseName);
            int roadIndex = Array.FindIndex(names, s => string.Equals(s, "road", StringComparison.OrdinalIgnoreCase));
            if (roadIndex < 0) return null;
            int roadCellType = roadIndex + 1;   // see the note above

            var hit = new bool[n * n];
            int cell = worldSize / n;
            if (cell <= 0) return null;

            foreach (var e in pak.Entries)
            {
                if (!e.Name.EndsWith(".srf", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(e.Name, baseName + ".srf", StringComparison.OrdinalIgnoreCase)) continue;

                string stem = e.Name[..^4]; // mapNNCCRR
                if (stem.Length < 9) continue;
                if (!int.TryParse(stem.AsSpan(5, 2), out int col) || !int.TryParse(stem.AsSpan(7, 2), out int row)) continue;

                SurfaceTile tile;
                try { tile = SurfaceTile.Parse(pak.Extract(e.Name)); }
                catch { continue; }
                if (!tile.IsFull) continue;

                for (int r = 0; r < SurfaceTile.GridDim; r++)
                {
                    int wy = row * 256 + r * 2;              // grid row -> world +Y (north)
                    int gj = Math.Min(n - 1, wy / cell);
                    for (int c = 0; c < SurfaceTile.GridDim; c++)
                    {
                        if (tile.GetCell(r, c).Type != roadCellType) continue;
                        int wx = col * 256 + c * 2;          // grid col -> world +X (east)
                        // A road is only a few cells wide, so ANY road sub-cell marks the output cell --
                        // a coverage threshold would break the network into dashes.
                        hit[gj * n + Math.Min(n - 1, wx / cell)] = true;
                    }
                }
            }

            var packed = new byte[(n * n + 7) / 8];
            int count = 0;
            for (int m = 0; m < n * n; m++)
            {
                if (hit[m]) { packed[m >> 3] |= (byte)(1 << (m & 7)); count++; }
            }
            return count == 0 ? null : new ContinentRoads { N = n, Cells = count, Mask = packed };
        }

        // Same chunky section grammar as the .mpo: 16-byte keyword field, u16 flag, u32 byteLen, then
        // the payload (u32 count + `count` NUL-terminated names).
        private const int KeywordField = 16;

        internal static string[] ReadTypeNames(PakArchive pak, string baseName)
        {
            int idx = pak.IndexOf(baseName + ".srf");
            if (idx < 0) return Array.Empty<string>();
            byte[] d = pak.Extract(idx);

            ReadOnlySpan<byte> kw = "groundcover"u8;
            int g = -1;
            for (int i = 0; i + kw.Length <= d.Length; i++)
            {
                if (d.AsSpan(i, kw.Length).SequenceEqual(kw)) { g = i; break; }
            }
            if (g < 0 || g + KeywordField + 10 > d.Length) return Array.Empty<string>();

            int p = g + KeywordField + 2 + 4;
            uint count = (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
            p += 4;
            if (count is < 1 or > 64) return Array.Empty<string>();

            var list = new List<string>((int)count);
            int q = p;
            for (int k = 0; k < count && q < d.Length; k++)
            {
                int s = q;
                while (q < d.Length && d[q] != 0) q++;
                if (q >= d.Length) break;
                list.Add(Encoding.ASCII.GetString(d, s, q - s));
                q++;
            }
            return list.ToArray();
        }
    }
}
