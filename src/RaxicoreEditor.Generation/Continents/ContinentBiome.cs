using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Surfaces;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Classifies a zone's ground into a handful of broad biome classes so the map can tint grass green,
    /// sand tan, rock grey and so on.
    ///
    /// This is deliberately a CLASSIFICATION, not a colour sample: it emits one class byte per cell and
    /// leaves the actual colours to whatever renders the map. The classes come from the per-continent
    /// surface name list in <c>mapNN_srf.pak</c> (<c>earth_grass</c>, <c>desert_scree</c>,
    /// <c>arctic_snow</c>, ...), with the same name-index+1 cell-type correction documented on
    /// <see cref="ContinentRoads"/>.
    /// </summary>
    internal sealed class ContinentBiome
    {
        /// <summary>Broad ground classes; a consumer maps each to whatever tint it likes.</summary>
        public enum Class : byte
        {
            None = 0,
            Default = 1,
            Grass = 2,
            Trees = 3,
            Sand = 4,
            Rock = 5,
            Dirt = 6,
            Snow = 7,
            Shore = 8,
            Water = 9,
            Lava = 10,
            Base = 11,
            Road = 12
        }

        /// <summary>
        /// The zone's dominant biome family, from the surface-name prefixes (<c>desert_grass</c>,
        /// <c>arctic_snow</c>, ...). The ground class alone is not enough to tint by: Ishundar's
        /// <c>desert_grass</c> is scrub, not lawn, so the family decides which palette the classes use.
        /// </summary>
        public string Family { get; private init; } = "earth";

        public int N { get; private init; }
        /// <summary>Row-major class byte per cell; i = world +X, j = world +Y.</summary>
        public byte[] Cells { get; private init; } = Array.Empty<byte>();

        /// <summary>
        /// Map a surface-type name to a class. Order matters: the specific ground word wins over the
        /// biome prefix, so <c>desert_rock</c> is rock (not sand) and <c>volcanic_dirt</c> is dirt.
        /// </summary>
        private static Class Classify(string name)
        {
            string s = name.ToLowerInvariant();
            if (s.Contains("lava") || s.Contains("scorched")) return Class.Lava;
            if (s == "road") return Class.Road;
            if (s == "base") return Class.Base;
            if (s.Contains("ocean") || s.Contains("water") || s.Contains("lakebed")) return Class.Water;
            if (s.Contains("shore")) return Class.Shore;
            if (s.Contains("snow") || s.Contains("ice")) return Class.Snow;
            if (s.Contains("trees") || s.Contains("frond") || s.Contains("brush")) return Class.Trees;
            if (s.Contains("grass")) return Class.Grass;
            if (s.Contains("rock") || s.Contains("scree")) return Class.Rock;
            if (s.Contains("dirt")) return Class.Dirt;
            if (s.Contains("sand") || s.Contains("desert")) return Class.Sand;
            if (s == "default") return Class.Default;
            return Class.Default;
        }

        public static ContinentBiome? Build(string srfPakPath, string baseName, int n, int worldSize)
        {
            PakArchive pak;
            try { pak = PakArchive.Load(File.ReadAllBytes(srfPakPath)); }
            catch { return null; }

            string[] names = ContinentRoads.ReadTypeNames(pak, baseName);
            if (names.Length == 0) return null;

            // Dominant prefix across the named ground types.
            var families = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string nm in names)
            {
                foreach (string f in new[] { "desert", "arctic", "volcanic", "earth" })
                {
                    if (nm.StartsWith(f, StringComparison.OrdinalIgnoreCase))
                    {
                        families.TryGetValue(f, out int c);
                        families[f] = c + 1;
                    }
                }
            }
            string family = families.Count == 0 ? "earth" : families.OrderByDescending(k => k.Value).First().Key;

            // cell type byte -> class (index + 1; see ContinentRoads).
            var byType = new Class[256];
            for (int i = 0; i < names.Length && i + 1 < 256; i++) byType[i + 1] = Classify(names[i]);

            int cell = worldSize / n;
            if (cell <= 0) return null;

            // Per output cell, count each class and keep the most common -- a cell spans many surface
            // samples and the dominant ground is what should drive its tint.
            var votes = new int[n * n * 13];
            foreach (var e in pak.Entries)
            {
                if (!e.Name.EndsWith(".srf", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(e.Name, baseName + ".srf", StringComparison.OrdinalIgnoreCase)) continue;

                string stem = e.Name[..^4];
                if (stem.Length < 9) continue;
                if (!int.TryParse(stem.AsSpan(5, 2), out int col) || !int.TryParse(stem.AsSpan(7, 2), out int row)) continue;

                SurfaceTile tile;
                try { tile = SurfaceTile.Parse(pak.Extract(e.Name)); }
                catch { continue; }
                if (!tile.IsFull) continue;

                for (int r = 0; r < SurfaceTile.GridDim; r++)
                {
                    int gj = Math.Min(n - 1, (row * 256 + r * 2) / cell);
                    for (int c = 0; c < SurfaceTile.GridDim; c++)
                    {
                        var cls = byType[tile.GetCell(r, c).Type];
                        if (cls == Class.None) continue;
                        int gi = Math.Min(n - 1, (col * 256 + c * 2) / cell);
                        votes[(gj * n + gi) * 13 + (int)cls]++;
                    }
                }
            }

            var cells = new byte[n * n];
            bool any = false;
            for (int m = 0; m < n * n; m++)
            {
                int best = 0, bestCount = 0;
                for (int k = 1; k < 13; k++)
                {
                    // Roads and bases are drawn as their own layers, so they shouldn't win the ground tint.
                    if (k == (int)Class.Road || k == (int)Class.Base) continue;
                    int v = votes[m * 13 + k];
                    if (v > bestCount) { bestCount = v; best = k; }
                }
                cells[m] = (byte)best;
                if (best != 0) any = true;
            }
            return any ? new ContinentBiome { N = n, Cells = cells, Family = family } : null;
        }
    }
}
