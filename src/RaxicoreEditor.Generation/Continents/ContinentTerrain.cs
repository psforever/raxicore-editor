using System.Numerics;
using System.Text.RegularExpressions;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Rasterizes a continent's per-tile terrain mesh into an <c>N x N</c> water mask.
    ///
    /// A continent's <c>mapNN.ubr</c> holds two relevant kinds of CMeshSystem: terrain tiles named
    /// <c>mapNN&lt;CC&gt;&lt;RR&gt;</c> (each 256x256 world units, placed at <c>WorldOffset =
    /// (CC*256, RR*256)</c> with tile-local vertex XY in <c>[0..256]</c> and a real Z height), and flat
    /// ocean planes named <c>mapNN_oc&lt;CC&gt;&lt;RR&gt;</c> sitting at a single sea-level Z. We take
    /// that ocean-plane Z as sea level, rasterize every terrain triangle into a height grid keeping the
    /// MAX terrain Z per cell, then mark a cell as water when its highest terrain is still below sea
    /// level (or no terrain covers it at all -- open ocean).
    /// </summary>
    internal sealed class ContinentTerrain
    {
        public int N { get; private init; }
        public int WorldSize { get; private init; }
        public float SeaLevel { get; private init; }
        public int TileCount { get; private init; }
        public int WaterCells { get; private init; }
        public int DeepCells { get; private init; }
        public byte[] PackedMask { get; private init; } = Array.Empty<byte>();
        public byte[] DeepMask { get; private init; } = Array.Empty<byte>();

        /// <summary>
        /// Depth (world units) at which standing infantry drown, so the deepest water still crossable on
        /// foot. Taken from the game's own <c>water_maxdragdepth</c> for a standing male avatar
        /// (PSF <c>GlobalDefinitions.avatar.MaxDepth = 1.609375</c>); water interaction/wading itself
        /// starts at 0.6x this. Below sea level but shallower than this = walkable shelf; deeper = open water.
        /// </summary>
        public const float WadeDepth = 1.609375f;

        public int LavaN { get; private init; }
        public int LavaCells { get; private init; }
        public byte[] LavaMask { get; private init; } = Array.Empty<byte>();

        /// <summary>Cavern walkable floor footprint (the navigable area of a vertical cave system).</summary>
        public int FloorCells { get; private init; }
        public byte[] FloorMask { get; private init; } = Array.Empty<byte>();

        /// <summary>Cavern pillar / crystal formations -- the massive vertical structures.</summary>
        public int PillarCells { get; private init; }
        public byte[] PillarMask { get; private init; } = Array.Empty<byte>();

        /// <summary>Per-cell terrain height, normalised 0..255 over [ElevMin, ElevMax] -- for contour lines.</summary>
        public byte[] Elevation { get; private init; } = Array.Empty<byte>();
        public float ElevMin { get; private init; }
        public float ElevMax { get; private init; }

        // A cave is far too vertical for a single land/water split to describe: the tile mesh stacks floor,
        // walls, ceiling and pillars on top of each other.
        //
        // The ACCESSIBLE area is found geometrically rather than by material name, because the names don't
        // partition cleanly (ugd01's single largest material is `cavern_ceiling_ul02+ugd01_mesas_floor` --
        // a walkable mesa carrying a "ceiling" base name). A surface is walkable when it faces up and isn't
        // steeper than a player can climb, so we keep triangles whose normal's Z clears MinWalkableNormalZ
        // -- which also correctly rejects walls, ceilings and the undersides of overhangs.
        private const float MinWalkableNormalZ = 0.7f;   // ~45 degrees of slope

        /// <summary>Pillar / crystal formations -- the massive vertical structures inside a cavern.</summary>
        private static readonly Regex PillarMaterial = new(@"^cavern_pillars|crystal", RegexOptions.IgnoreCase);

        // Overworld tiles are `mapNN<CC><RR>`; the cavern/VR branch names its grid `<stem>_<CC><RR>`
        // (underscore separator), so both spellings are accepted.
        private static readonly Regex TileName = new(@"^(?:map|ugd)\d{2}_?(\d{2})(\d{2})$", RegexOptions.IgnoreCase);
        private static readonly Regex OceanName = new(@"^(?:map|ugd)\d{2}_oc\d{2}\d{2}$", RegexOptions.IgnoreCase);

        // Lava pools are their own mesh records (e.g. map09_0..map09_7 on Searhus) whose sections carry a
        // `lava+lavalayer*` material -- NOT a terrain surface type. Matching the material is what finds the
        // actual rendered pools; the .srf `lava` surface is only lava-textured ground elsewhere on the map.
        private static readonly Regex LavaMaterial = new(@"lava|magma|molten", RegexOptions.IgnoreCase);

        public static ContinentTerrain Build(string ubrPath, int n, int lavaN)
        {
            var model = UberModel.Load(File.ReadAllBytes(ubrPath));

            // Pass 1: sea level (the flat ocean-plane Z) + world extent (max tile corner + one tile).
            float sea = float.NaN;
            int maxCorner = 0;
            var tileIdx = new List<int>();
            for (int i = 0; i < model.Records.Count; i++)
            {
                string name = model.Records[i].Name;
                if (TileName.IsMatch(name))
                {
                    tileIdx.Add(i);
                }
                else if (float.IsNaN(sea) && OceanName.IsMatch(name))
                {
                    var oc = model.FetchMeshSystemAt(i);
                    if (oc != null)
                    {
                        foreach (var mesh in oc.Meshes)
                        foreach (var s in mesh.Sections)
                        foreach (var v in s.Verts) { sea = v.Position.Z; break; }
                        if (!float.IsNaN(sea)) { /* flat plane -- one vertex is enough */ }
                    }
                }
            }

            // Determine world size from the tile grid (corner offsets are multiples of 256).
            foreach (int i in tileIdx)
            {
                var sys = model.FetchMeshSystemAt(i);
                if (sys == null) continue;
                var off = sys.WorldOffset;
                maxCorner = Math.Max(maxCorner, Math.Max((int)off.X, (int)off.Y));
            }
            int worldSize = maxCorner + 256;
            if (worldSize <= 256) worldSize = 8192; // degenerate guard
            if (float.IsNaN(sea)) sea = 0f;         // no ocean plane -> nothing is below sea

            float cell = (float)worldSize / n;
            var maxZ = new float[n * n];
            var covered = new bool[n * n];
            Array.Fill(maxZ, float.NegativeInfinity);

            // Lava pools live in their own (non-tile) records, so sweep EVERY record: terrain tiles feed the
            // height grid, and any section with a lava material paints the lava coverage grid.
            float lavaCell = (float)worldSize / lavaN;
            var lavaHit = new bool[lavaN * lavaN];
            var floorHit = new bool[lavaN * lavaN];
            var pillarHit = new bool[lavaN * lavaN];

            int tiles = 0;
            for (int i = 0; i < model.Records.Count; i++)
            {
                var sys = model.FetchMeshSystemAt(i);
                if (sys == null) continue;
                Vector3 off = sys.WorldOffset;
                bool isTile = TileName.IsMatch(model.Records[i].Name);
                if (isTile) tiles++;

                foreach (var mesh in sys.Meshes)
                foreach (var s in mesh.Sections)
                {
                    if (isTile) RasterizeSection(s, off, n, cell, maxZ, covered);
                    if (LavaMaterial.IsMatch(s.MaterialName)) RasterizeCoverage(s, off, lavaN, lavaCell, lavaHit);
                    if (PillarMaterial.IsMatch(s.MaterialName)) RasterizeCoverage(s, off, lavaN, lavaCell, pillarHit);
                    // Walkable surface -> the accessible area (only meaningful underground, but harmless
                    // elsewhere since a surface map's land already comes from the heightfield).
                    RasterizeCoverage(s, off, lavaN, lavaCell, floorHit, MinWalkableNormalZ);
                }
            }

            static (byte[] Packed, int Count) Pack(bool[] hits, int dim)
            {
                var packed = new byte[(dim * dim + 7) / 8];
                int count = 0;
                for (int idx = 0; idx < dim * dim; idx++)
                {
                    if (hits[idx]) { packed[idx >> 3] |= (byte)(1 << (idx & 7)); count++; }
                }
                return (packed, count);
            }

            var (lavaPacked, lavaCells) = Pack(lavaHit, lavaN);
            var (floorPacked, floorCells) = Pack(floorHit, lavaN);
            var (pillarPacked, pillarCells) = Pack(pillarHit, lavaN);

            // The terrain tiles are irregular TINs, so a cell centre can fall in a gap between triangles
            // even on solid high ground. Defaulting those uncovered cells to water would carve spurious
            // lakes/inlets right where facilities sit. Instead, flood every uncovered cell with the height
            // of its nearest COVERED cell (multi-source BFS) before thresholding: interior gaps inherit the
            // surrounding highland (-> land), while genuine open ocean inherits the low ocean-floor tiles
            // that do rasterize (-> water).
            FillUncovered(maxZ, covered, n);

            // Normalised height grid for contour lines: 0..255 over the min/max of the (now fully filled)
            // height field. Kept at the same resolution as the water mask.
            float minH = float.PositiveInfinity, maxH = float.NegativeInfinity;
            for (int idx = 0; idx < n * n; idx++)
            {
                float z = maxZ[idx];
                if (float.IsFinite(z)) { if (z < minH) minH = z; if (z > maxH) maxH = z; }
            }
            if (!float.IsFinite(minH)) { minH = 0f; maxH = 1f; }
            float elevRange = maxH - minH;
            if (elevRange <= 0f) elevRange = 1f;
            var elevation = new byte[n * n];
            for (int idx = 0; idx < n * n; idx++)
            {
                float z = maxZ[idx];
                float t = float.IsFinite(z) ? (z - minH) / elevRange : 0f;
                elevation[idx] = (byte)Math.Clamp((int)MathF.Round(t * 255f), 0, 255);
            }

            // Classify each cell three ways by how deep its highest ground sits below the water plane:
            //   dry            (maxZ >= sea)                  -> land
            //   submerged      (0 < depth <= WadeDepth)       -> shallow, still walkable (wadeable shelf)
            //   deeply drowned (depth > WadeDepth)            -> open water, not traversable on foot
            // WadeDepth is the game's own drowning threshold for standing infantry, so the shallow band is
            // exactly the water a player can stand up in rather than an arbitrary visual guess.
            var packed = new byte[(n * n + 7) / 8];
            var deepPacked = new byte[(n * n + 7) / 8];
            int waterCells = 0, deepCells = 0;
            for (int idx = 0; idx < n * n; idx++)
            {
                float depth = sea - maxZ[idx];
                if (depth <= 0f) continue;                       // dry land
                packed[idx >> 3] |= (byte)(1 << (idx & 7));
                waterCells++;
                if (depth > WadeDepth)
                {
                    deepPacked[idx >> 3] |= (byte)(1 << (idx & 7));
                    deepCells++;
                }
            }

            return new ContinentTerrain
            {
                LavaN = lavaN,
                LavaCells = lavaCells,
                LavaMask = lavaPacked,
                FloorCells = floorCells,
                FloorMask = floorPacked,
                PillarCells = pillarCells,
                PillarMask = pillarPacked,
                N = n,
                WorldSize = worldSize,
                SeaLevel = sea,
                TileCount = tiles,
                WaterCells = waterCells,
                DeepCells = deepCells,
                PackedMask = packed,
                DeepMask = deepPacked,
                Elevation = elevation,
                ElevMin = minH,
                ElevMax = maxH
            };
        }

        // Mark every cell whose centre falls inside one of the section's triangles. Used for flat liquid
        // surfaces (lava pools) where only the footprint matters, not the height.
        private static void RasterizeCoverage(UberModel.MeshSection s, Vector3 off, int n, float cell, bool[] hit,
            float minNormalZ = float.NegativeInfinity)
        {
            UberModel.UberVert[] v = s.Verts;
            ushort[] idx = s.Indices;
            if (v.Length == 0 || idx.Length < 3) return;

            void Tri2(UberModel.UberVert va, UberModel.UberVert vb, UberModel.UberVert vc)
            {
                if (minNormalZ > float.NegativeInfinity)
                {
                    // Face direction from the stored vertex normals (the winding alone can't tell a floor
                    // from the underside of the same slab).
                    float nz = (va.Normal.Z + vb.Normal.Z + vc.Normal.Z) / 3f;
                    if (nz < minNormalZ) return;
                }
                float ax = va.Position.X + off.X, ay = va.Position.Y + off.Y;
                float bx = vb.Position.X + off.X, by = vb.Position.Y + off.Y;
                float cx = vc.Position.X + off.X, cy = vc.Position.Y + off.Y;
                int i0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)) / cell));
                int i1 = Math.Min(n - 1, (int)MathF.Floor(MathF.Max(ax, MathF.Max(bx, cx)) / cell));
                int j0 = Math.Max(0, (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)) / cell));
                int j1 = Math.Min(n - 1, (int)MathF.Floor(MathF.Max(ay, MathF.Max(by, cy)) / cell));
                float d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
                if (MathF.Abs(d) < 1e-9f) return;
                for (int j = j0; j <= j1; j++)
                {
                    float py = (j + 0.5f) * cell;
                    for (int i = i0; i <= i1; i++)
                    {
                        float px = (i + 0.5f) * cell;
                        float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / d;
                        float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / d;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        hit[j * n + i] = true;
                    }
                }
            }

            if (s.IsTriStrip)
            {
                for (int k = 0; k + 2 < idx.Length; k++)
                {
                    int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                    if (a == b || b == c || a == c) continue;
                    Tri2(v[a], v[b], v[c]);
                }
            }
            else
            {
                for (int k = 0; k + 2 < idx.Length; k += 3) Tri2(v[idx[k]], v[idx[k + 1]], v[idx[k + 2]]);
            }
        }

        // Multi-source BFS: propagate each covered cell's height outward so every uncovered cell takes the
        // height of the nearest covered cell (4-connected). Fully fills the grid in one sweep.
        private static void FillUncovered(float[] maxZ, bool[] covered, int n)
        {
            var q = new Queue<int>();
            for (int idx = 0; idx < n * n; idx++)
            {
                if (covered[idx]) q.Enqueue(idx);
            }
            if (q.Count == 0 || q.Count == n * n) return;

            while (q.Count > 0)
            {
                int idx = q.Dequeue();
                int i = idx % n, j = idx / n;
                float z = maxZ[idx];
                void Visit(int ni, int nj)
                {
                    if (ni < 0 || nj < 0 || ni >= n || nj >= n) return;
                    int m = nj * n + ni;
                    if (covered[m]) return;
                    covered[m] = true;   // mark as filled so it's enqueued once
                    maxZ[m] = z;
                    q.Enqueue(m);
                }
                Visit(i - 1, j);
                Visit(i + 1, j);
                Visit(i, j - 1);
                Visit(i, j + 1);
            }
        }

        private static void RasterizeSection(UberModel.MeshSection s, Vector3 off, int n, float cell,
            float[] maxZ, bool[] covered)
        {
            UberModel.UberVert[] v = s.Verts;
            ushort[] idx = s.Indices;
            if (v.Length == 0 || idx.Length < 3) return;

            if (s.IsTriStrip)
            {
                for (int k = 0; k + 2 < idx.Length; k++)
                {
                    int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                    if (a == b || b == c || a == c) continue;          // degenerate strip joint
                    // Preserve consistent winding across the strip (irrelevant to rasterization, kept for clarity).
                    if ((k & 1) == 0) Tri(v[a], v[b], v[c], off, n, cell, maxZ, covered);
                    else Tri(v[b], v[a], v[c], off, n, cell, maxZ, covered);
                }
            }
            else
            {
                for (int k = 0; k + 2 < idx.Length; k += 3)
                {
                    Tri(v[idx[k]], v[idx[k + 1]], v[idx[k + 2]], off, n, cell, maxZ, covered);
                }
            }
        }

        // Scan-convert one triangle into the grid, writing the barycentric-interpolated Z (keeping the
        // per-cell max) for every cell whose centre falls inside it.
        private static void Tri(UberModel.UberVert va, UberModel.UberVert vb, UberModel.UberVert vc,
            Vector3 off, int n, float cell, float[] maxZ, bool[] covered)
        {
            float ax = va.Position.X + off.X, ay = va.Position.Y + off.Y, az = va.Position.Z;
            float bx = vb.Position.X + off.X, by = vb.Position.Y + off.Y, bz = vb.Position.Z;
            float cx = vc.Position.X + off.X, cy = vc.Position.Y + off.Y, cz = vc.Position.Z;

            float minX = MathF.Min(ax, MathF.Min(bx, cx));
            float maxX = MathF.Max(ax, MathF.Max(bx, cx));
            float minY = MathF.Min(ay, MathF.Min(by, cy));
            float maxY = MathF.Max(ay, MathF.Max(by, cy));

            int i0 = Math.Max(0, (int)MathF.Floor(minX / cell));
            int i1 = Math.Min(n - 1, (int)MathF.Floor(maxX / cell));
            int j0 = Math.Max(0, (int)MathF.Floor(minY / cell));
            int j1 = Math.Min(n - 1, (int)MathF.Floor(maxY / cell));
            if (i0 > i1 || j0 > j1) return;

            float d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (MathF.Abs(d) < 1e-9f) return; // degenerate

            for (int j = j0; j <= j1; j++)
            {
                float py = (j + 0.5f) * cell;
                for (int i = i0; i <= i1; i++)
                {
                    float px = (i + 0.5f) * cell;
                    float w0 = ((by - cy) * (px - cx) + (cx - bx) * (py - cy)) / d;
                    float w1 = ((cy - ay) * (px - cx) + (ax - cx) * (py - cy)) / d;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                    float z = w0 * az + w1 * bz + w2 * cz;
                    int m = j * n + i;
                    covered[m] = true;
                    if (z > maxZ[m]) maxZ[m] = z;
                }
            }
        }
    }
}
