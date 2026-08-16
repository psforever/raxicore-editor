using System.Text.Json;
using System.Text.RegularExpressions;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Reads every continent's <c>mapNN.ubr</c> (the reference client's per-tile terrain mesh) via the
    /// faithful UberModel decoder and emits one JSON file per continent describing its terrain (a
    /// coarse water/lava/floor/pillar mask, a road network, broad ground classes, bridge decks, and a
    /// per-cell height grid), plus one shared file of per-facility-type top-down footprints.
    ///
    /// This data is generic: nothing here assumes a particular consumer or how the output gets used --
    /// a renderer, a map viewer, a game server, anything that wants continent geometry can read it. One
    /// example of what a consumer might do with the water/lava/floor masks is turn them into a
    /// coastline (ocean vs. inland water, land silhouette) via ocean-vs-lake classification and
    /// marching-squares contouring, entirely downstream of this tool.
    ///
    /// Why the mesh and not <c>contents_mapNN.mpo</c>: the MPO's <c>map_water</c> layer only flags
    /// tiles that carry an <c>_oc</c> water-plane record (coast, rivers and lakes alike -- ~74% of
    /// tiles), so it does NOT separate ocean from land. The real silhouette is in the per-tile terrain
    /// heights, thresholded at the <c>_oc</c> plane -- verified: the plane is a single flat Z per map
    /// (map03 = 29.5) and all 1024 tiles decode.
    ///
    /// Shared between the <c>tools/ContinentExport</c> CLI and the Editor's Generate menu -- one
    /// implementation, two front ends.
    /// </summary>
    public static class ContinentExportTool
    {
        /// <param name="PlanetSideDir">The read-only reference client folder. Nothing under it is written.</param>
        /// <param name="OutDir">Where the per-continent JSON and footprints.json go.</param>
        /// <param name="TerrainOutDir">
        /// Optional: writes the same per-continent height grid the main output's <c>elevation</c> field
        /// already carries, a second time, to a separate folder. The main output is aimed at rendering a
        /// map (contour lines, tinting); this is the raw grid itself, for anything that needs to look up
        /// real per-cell ground height directly -- ground-truth data instead of an unverified caller's
        /// claim about where the ground is, e.g. for placing an effect or a decal accurately. Any
        /// consumer that wants this can read from here; it has no dependency on the main output folder.
        /// </param>
        public sealed record Options(string PlanetSideDir, string OutDir, string? TerrainOutDir = null);

        public sealed record Result(int ContinentsExported, int FacilityFootprintTypes);

        // Resolution of the exported water masks, in cells per axis. 512 -> 16 world units per cell on a
        // full 8192 continent. The wadeable shelf is a narrow depth band (a couple of world units), so it
        // needs a finer grid than the plain coastline did to resolve at all.
        private const int MaskN = 512;

        // Lava pools are small (a few hundred world units across), so sample them finer than the
        // coastline. 512 -> 16 world units per cell.
        private const int LavaN = 512;

        // Roads are only a few world units wide, so they need a fine grid or the network breaks into
        // dashes.
        private const int RoadN = 512;

        // Biome class grid. This only drives a broad tint, so it stays coarse (64 -> 128 world units/cell).
        private const int BiomeN = 64;

        private static readonly Regex ZoneUbr = new(@"^(map\d{2}|ugd\d{2})\.ubr$", RegexOptions.IgnoreCase);

        /// <summary>
        /// Runs the export, reporting one line per notable event to <paramref name="log"/> -- the same
        /// lines the CLI prints to stdout/stderr. Throws <see cref="ArgumentException"/> for a bad
        /// options value (missing directories) rather than a usage-error exit code, since a GUI caller
        /// has no exit code to read; throws <see cref="OperationCanceledException"/> if
        /// <paramref name="ct"/> is cancelled between continents.
        /// </summary>
        public static Result Run(Options options, IProgress<string> log, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(options.PlanetSideDir))
            {
                throw new ArgumentException("a PlanetSide reference client folder is required", nameof(options));
            }
            if (string.IsNullOrWhiteSpace(options.OutDir))
            {
                throw new ArgumentException("an output folder is required", nameof(options));
            }

            string planetside = options.PlanetSideDir;
            string outDir = options.OutDir;
            string terrainOutDir = options.TerrainOutDir ?? "";

            Directory.CreateDirectory(outDir);
            if (terrainOutDir.Length > 0)
            {
                Directory.CreateDirectory(terrainOutDir);
            }

            // The .srf surface grids and the .mpo object lists live alongside the .ubr in the reference
            // folder.
            string mapResources = Path.Combine(planetside, "maps", "map_resources.pak");

            // Zone meshes live in three places: the overworld continents + sanctuaries as loose
            // mapNN.ubr, the battle islands under patchmap/mapNN/, and the Core Combat caverns as
            // expansion1/ugdNN.ubr.
            var ubrPaths = new List<string>();
            ubrPaths.AddRange(Directory.EnumerateFiles(planetside, "map*.ubr"));
            string patchmap = Path.Combine(planetside, "patchmap");
            if (Directory.Exists(patchmap))
            {
                foreach (string dir in Directory.EnumerateDirectories(patchmap))
                {
                    ubrPaths.AddRange(Directory.EnumerateFiles(dir, "map*.ubr"));
                }
            }
            string expansion1 = Path.Combine(planetside, "expansion1");
            if (Directory.Exists(expansion1))
            {
                ubrPaths.AddRange(Directory.EnumerateFiles(expansion1, "ugd*.ubr"));
            }
            int exported = 0;

            log.Report($"{"continent",-10} {"tiles",6} {"sea",7} {"terrain split",30}  overlays");
            log.Report(new string('-', 60));

            foreach (string ubrPath in ubrPaths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                string file = Path.GetFileName(ubrPath);
                Match m = ZoneUbr.Match(file);
                if (!m.Success)
                {
                    continue;
                }
                string baseName = m.Groups[1].Value.ToLowerInvariant();

                ContinentTerrain terrain;
                try
                {
                    terrain = ContinentTerrain.Build(ubrPath, MaskN, LavaN);
                }
                catch (Exception e)
                {
                    log.Report($"skip {file}: {e.Message}");
                    continue;
                }

                // Road network from the per-tile surface grids (surface maps only; the caverns ship no
                // surface pak). The pak sits beside its .ubr, which for the battle islands is
                // patchmap/mapNN/ rather than the reference folder root.
                ContinentRoads? roads = null;
                string srfPak = Path.Combine(Path.GetDirectoryName(ubrPath) ?? planetside, baseName + "_srf.pak");
                if (File.Exists(srfPak))
                {
                    try
                    {
                        roads = ContinentRoads.Build(srfPak, baseName, RoadN, terrain.WorldSize);
                    }
                    catch (Exception e)
                    {
                        log.Report($"  {baseName} roads: {e.Message}");
                    }
                }

                // Broad ground classes (grass / sand / rock / ...) for tinting, from the same surface pak.
                ContinentBiome? biome = null;
                if (File.Exists(srfPak))
                {
                    try
                    {
                        biome = ContinentBiome.Build(srfPak, baseName, BiomeN, terrain.WorldSize);
                    }
                    catch (Exception e)
                    {
                        log.Report($"  {baseName} biome: {e.Message}");
                    }
                }

                // Bridge deck polylines from the object list. The overworld shares maps/map_resources.pak,
                // but each battle island ships its own mapNN_resources.pak beside its .ubr -- Extinction
                // and Desolation both have bridges that the shared pak knows nothing about.
                string ownResources = Path.Combine(Path.GetDirectoryName(ubrPath) ?? planetside, baseName + "_resources.pak");
                string bridgePak = File.Exists(ownResources) ? ownResources : mapResources;
                List<List<float[]>> bridges = File.Exists(bridgePak)
                    ? ContinentBridges.Build(bridgePak, baseName)
                    : new List<List<float[]>>();

                var doc = new
                {
                    @base = baseName,
                    worldSize = terrain.WorldSize,
                    sea = terrain.SeaLevel,
                    maskN = terrain.N,
                    // Row-major (j*N + i) bit-packed masks, base64. i indexes world +X (east), j world
                    // +Y (north).
                    //   mask     : bit set == below sea level (submerged at all)
                    //   deepMask : bit set == deeper than the wade threshold (not walkable -> open ocean)
                    // A cell submerged but NOT deep is the shallow, still-walkable shelf.
                    mask = Convert.ToBase64String(terrain.PackedMask),
                    deepMask = Convert.ToBase64String(terrain.DeepMask),
                    wadeDepth = ContinentTerrain.WadeDepth,
                    // Lava-pool overlay: same bit-packing at lavaN resolution (empty on non-volcanic
                    // continents).
                    lavaN = terrain.LavaN,
                    lava = Convert.ToBase64String(terrain.LavaMask),
                    // Cavern-only layers: the walkable floor (navigable area of a vertical cave) and the
                    // pillar/crystal formations. Empty on surface maps.
                    floor = Convert.ToBase64String(terrain.FloorMask),
                    pillars = Convert.ToBase64String(terrain.PillarMask),
                    // Road network: bit-packed at roadN resolution (null where the continent ships no
                    // surface pak).
                    roadN = roads?.N,
                    roads = roads != null ? Convert.ToBase64String(roads.Mask) : null,
                    // Ground class per cell (see ContinentBiome.Class) -- a consumer picks the tint
                    // colours.
                    biomeN = biome?.N,
                    biome = biome != null ? Convert.ToBase64String(biome.Cells) : null,
                    biomeFamily = biome?.Family,
                    // Per-cell terrain height, normalised 0..255, base64 (same N as the water mask). For
                    // an optional elevation contour overlay, or anything else that wants coarse height.
                    elevationN = terrain.N,
                    elevation = Convert.ToBase64String(terrain.Elevation),
                    elevMin = terrain.ElevMin,
                    elevMax = terrain.ElevMax,
                    // Bridge deck runs as [[x,y],...] world-coord polylines.
                    bridges
                };

                File.WriteAllText(
                    Path.Combine(outDir, baseName + ".json"),
                    JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = false }));

                if (terrainOutDir.Length > 0)
                {
                    // Same grid, same encoding, same (worldSize, n) -> cell -> row-major m=j*n+i
                    // convention as `elevation` above -- this is that field again, just also written to
                    // its own folder for a consumer that only wants the raw heights. Nothing here is
                    // re-derived or re-sampled.
                    var heightDoc = new
                    {
                        @base = baseName,
                        worldSize = terrain.WorldSize,
                        n = terrain.N,
                        elevMin = terrain.ElevMin,
                        elevMax = terrain.ElevMax,
                        elevation = Convert.ToBase64String(terrain.Elevation)
                    };
                    File.WriteAllText(
                        Path.Combine(terrainOutDir, baseName + ".json"),
                        JsonSerializer.Serialize(heightDoc, new JsonSerializerOptions { WriteIndented = false }));
                }

                exported++;

                int cells = terrain.N * terrain.N;
                log.Report(
                    $"{baseName,-10} {terrain.TileCount,6} {terrain.SeaLevel,7:F1} " +
                    $"deep={100.0 * terrain.DeepCells / cells,5:F1}% " +
                    $"shallow={100.0 * (terrain.WaterCells - terrain.DeepCells) / cells,5:F1}% " +
                    $"land={100.0 * (cells - terrain.WaterCells) / cells,5:F1}%  " +
                    $"lava={terrain.LavaCells,5} floor={terrain.FloorCells,5} road={roads?.Cells ?? 0,6} br={bridges.Count,2}");
            }

            log.Report(new string('-', 52));
            log.Report($"exported {exported} continents to {outDir}");

            // Per-facility-type top-down footprints (shared across continents -> one file).
            ct.ThrowIfCancellationRequested();
            Dictionary<string, FacilityFootprints.Footprint> footprints = FacilityFootprints.Build(planetside);
            if (footprints.Count > 0)
            {
                Dictionary<string, object> fpDoc = footprints.ToDictionary(
                    kv => kv.Key,
                    kv => (object)new
                    {
                        ox = kv.Value.Ox,
                        oy = kv.Value.Oy,
                        cell = kv.Value.Cell,
                        n = kv.Value.N,
                        mask = Convert.ToBase64String(kv.Value.Mask)
                    });
                File.WriteAllText(
                    Path.Combine(outDir, "footprints.json"),
                    JsonSerializer.Serialize(fpDoc, new JsonSerializerOptions { WriteIndented = false }));
                log.Report($"footprints: {string.Join(", ", footprints.Select(k => $"{k.Key}({k.Value.Cells})"))}");
            }

            return new Result(exported, footprints.Count);
        }
    }
}
