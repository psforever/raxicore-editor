using System.Numerics;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Maps;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Extracts an orthographic top-down "above ground" footprint for each facility type, so the map can
    /// draw the real building outline (the warpgate's three spokes, a tower's corner posts, a facility's
    /// courtyard walls) instead of a generic marker.
    ///
    /// The models live in the shared mesh libraries (<c>uber.ubr</c>; the cavern types in
    /// <c>expansion1/expansion1.ubr</c>) in model-local coordinates, roughly centred on the origin with
    /// Z=0 at ground level. We keep only triangles lying ENTIRELY above a per-type ground height and
    /// rasterize them onto the XY plane. Excluding the ground slab matters: every model sits on a flat
    /// apron that, if included, swallows the interesting outline -- the warpgate in particular reads as a
    /// solid blob until the pad is sliced away, and only then shows its three arms.
    /// </summary>
    internal sealed class FacilityFootprints
    {
        /// <summary>One type's footprint: an N x N coverage mask over [ox, ox+n*cell] x [oy, oy+n*cell].</summary>
        internal sealed record Footprint(float Ox, float Oy, float Cell, int N, byte[] Mask, int Cells);

        // type -> (mesh library, record name, ground height to slice above).
        // Ground heights are per-type because the aprons differ: the warpgate's pad is much taller than a
        // tower's, so a single cutoff either keeps the warpgate's pad or eats a tower's ground floor.
        private static readonly (string Type, string Lib, string Record, float Ground)[] Types =
        {
            ("warpgate",             "uber",       "warpgate",             3.0f),
            ("tower_a",              "uber",       "tower_a",              1.5f),
            ("tower_b",              "uber",       "tower_b",              1.5f),
            ("tower_c",              "uber",       "tower_c",              1.5f),
            ("amp_station",          "uber",       "amp_station",          1.5f),
            ("comm_station",         "uber",       "comm_station",         1.5f),
            ("comm_station_dsp",     "uber",       "comm_station_dsp",     1.5f),
            ("cryo_facility",        "uber",       "cryo_facility",        1.5f),
            ("tech_plant",           "uber",       "tech_plant",           1.5f),
            // Patch-added structures aren't in uber.ubr -- they ship in the numbered patch libraries.
            ("hst",                  "patch2",     "hst",                  1.5f),
            ("warpgate_small",       "patch5",     "warpgate_small",       1.5f),
            // Cavern-side structures ship in the expansion library.
            ("warpgate_cavern",      "expansion1", "warpgate_cavern",      1.5f),
            ("redoubt",              "expansion1", "redoubt",              1.5f),
            ("vanu_control_point",   "expansion1", "vanu_control_point",   1.5f),
            ("vanu_vehicle_station", "expansion1", "vanu_vehicle_station", 1.5f),
            // Sanctuary structures. The HART is the sanctuary's landmark; the VT pads are the vehicle,
            // dropship and spawn buildings that the sanctuary's terminals sit on. All three VT records
            // are empire-neutral shells -- only the umbrella VT_building_<empire> object is per-empire,
            // and it carries no geometry of its own, so the shells are what actually draws.
            ("orbital_building_nc",  "uber",       "orbital_building_nc",  1.5f),
            ("orbital_building_tr",  "uber",       "orbital_building_tr",  1.5f),
            ("orbital_building_vs",  "uber",       "orbital_building_vs",  1.5f),
            ("vt_vehicle",           "uber",       "vt_vehicle",           1.5f),
            ("vt_dropship",          "uber",       "vt_dropship",          1.5f),
            ("vt_spawn",             "uber",       "vt_spawn",             1.5f),
            // Sanctuary pads, their doors, and the terminals that stand on them. Unlike a facility these
            // are small and largely FLAT -- a creation pad lies in the ground plane and a terminal is only
            // a couple of units tall -- so the usual above-ground slice would erase them entirely. They
            // take the whole model instead, which is the point: the shape drawn is the pad and terminal
            // the player actually walks up to.
            ("vehicle_terminal",          "uber",   "vehicle_terminal",          NoSlice),
            ("dropship_vehicle_terminal", "uber",   "dropship_vehicle_terminal", NoSlice),
            ("bfr_terminal",              "patch4", "bfr_terminal",              NoSlice),
            ("mb_pad_creation",           "uber",   "mb_pad_creation",           NoSlice),
            ("dropship_pad_doors",        "uber",   "dropship_pad_doors",        NoSlice),
            ("pad_landing",               "uber",   "pad_landing",               NoSlice),
            ("pad_landing_tower_frame",   "uber",   "pad_landing_tower_frame",   NoSlice),
            ("spawn_tube_door",           "uber",   "spawn_tube_door",           NoSlice),

            // --- Combat-view entities: vehicles, soldiers and CE ------------------------------------
            // Drawn on the admin Combat map as real top-down silhouettes at world scale, so a Galaxy
            // reads as a Galaxy next to the base it is landing at. All take the whole model (NoSlice):
            // they sit on/near the ground plane and an above-ground slice would erase them. Keys match
            // the codenames the snapshot reports (Vehicle.Definition.Name), so lookup is direct.
            // Ground vehicles.
            ("ams",                       "patch1", "ams",                       NoSlice),
            ("ant",                       "patch1", "ant",                       NoSlice),
            ("apc",                       "patch1", "apc",                       NoSlice),
            ("battlewagontr",             "patch3", "battlewagontr",             NoSlice),
            ("flail",                     "patch2", "flail",                     NoSlice),
            ("lightning",                 "patch1", "lightning",                 NoSlice),
            ("magrider",                  "patch1", "magrider",                  NoSlice),
            ("mediumtransport",           "patch1", "mediumtransport",           NoSlice),
            ("prowler",                   "patch1", "prowler",                   NoSlice),
            ("quadassault",               "patch1", "quadassault",               NoSlice),
            ("quadstealth",               "patch1", "quadstealth",               NoSlice),
            ("router",                    "patch2", "router",                    NoSlice),
            ("skyguard",                  "patch1", "skyguard",                  NoSlice),
            ("switchblade",               "patch2", "switchblade",               NoSlice),
            ("threemanheavybuggy",        "patch1", "threemanheavybuggy",        NoSlice),
            ("twomanheavybuggy",          "patch1", "twomanheavybuggy",          NoSlice),
            ("twomanhoverbuggy",          "patch1", "twomanhoverbuggy",          NoSlice),
            ("two_man_assault_buggy",     "patch1", "two_man_assault_buggy",     NoSlice),
            ("vanguard",                  "patch1", "vanguard",                  NoSlice),
            // Aircraft.
            ("dropship",                  "patch1", "dropship",                  NoSlice),
            ("galaxy_gunship",            "patch5", "galaxy_gunship",            NoSlice),
            ("liberator",                 "patch1", "liberator",                 NoSlice),
            ("lightgunship",              "patch1", "lightgunship",              NoSlice),
            ("lodestar",                  "patch1", "lodestar",                  NoSlice),
            ("mosquito",                  "patch1", "mosquito",                  NoSlice),
            // NOTE: infantry are deliberately absent. The character records here are armour overlays and
            // skinned meshes that are not posed for a top-down view (oa_tr_std rasterizes to a 0.2 x 0.3
            // blob; trhev comes out 2.3 x 0.8 with its arms along X), so a footprint from them would be
            // misleading rather than informative. The map draws soldiers as a facing-aware marker instead.
            // CE / deployables.
            ("boomer",                    "uber",   "boomer_he_mine",            NoSlice),
            ("he_mine",                   "uber",   "he_mine",                   NoSlice),
            // jammer_mine has no model record of its own -- it reuses the HE mine shell.
            ("jammer_mine",               "uber",   "he_mine",                   NoSlice),
            ("motionalarmsensor",         "uber",   "motionalarmsensor",         NoSlice),
            ("sensor_shield",             "patch5", "sensor_shield",             NoSlice),
            ("spitfire_turret",           "uber",   "spitfire_turret",           NoSlice),
            ("spitfire_cloaked",          "patch5", "spitfire_cloaked",          NoSlice),
            ("spitfire_aa",               "patch5", "spitfire_aa",               NoSlice),
            ("portable_manned_turret",    "patch5", "portable_manned_turret",    NoSlice),
            ("portable_manned_turret_nc", "patch5", "portable_manned_turret",    NoSlice),
            ("portable_manned_turret_tr", "patch5", "portable_manned_turret",    NoSlice),
            ("portable_manned_turret_vs", "patch5", "portable_manned_turret",    NoSlice),
            ("router_telepad_deployable", "patch2", "router_telepad",            NoSlice),
            ("tank_traps",                "patch5", "tank_traps",                NoSlice),
            ("deployable_shield_generator", "patch5", "deployable_shield_generator", NoSlice),
        };

        /// <summary>Ground height that keeps every triangle, for models that sit at or below the plane.</summary>
        private const float NoSlice = float.NegativeInfinity;

        /// <summary>Grid resolution per footprint. ~1 world unit/cell at typical facility sizes.</summary>
        private const int GridN = 192;

        /// <summary>
        /// Structures drawn as a SIDE profile rather than a top-down footprint, with a material excluded.
        /// A monolith is a standing slab -- from above it is just a small blob, so it is marked with its
        /// silhouette instead. `dirt_mono` is the mound it sits in and is dropped, leaving the marble.
        /// </summary>
        private static readonly (string Type, string Lib, string Record, string DropMaterial)[] Profiles =
        {
            ("monolith", "patch4", "monolith", "dirt_mono")
        };

        /// <summary>
        /// Composite structures: a record that is only part of the real building, plus a
        /// <c>pse_relativeobject</c> list naming the rest. A warpgate is the flat pad (<c>warpgate</c>)
        /// PLUS three standing arches from <c>warpgate_1.lst</c> -- each arch seven <c>wg_arm_piece*</c>
        /// records listed at 0 / +120 / -120 degrees -- so the pad alone is not the whole gate.
        /// </summary>
        private static readonly Dictionary<string, string> PartLists =
            new(StringComparer.OrdinalIgnoreCase) { ["warpgate"] = "warpgate_1.lst" };

        /// <summary>
        /// Extra model records merged into a type's footprint at identity, for entities whose deployed
        /// form is several records. Their vertices are already expressed about the parent's origin, so no
        /// placement transform is needed -- adding the record as-is drops it in the right place.
        ///
        /// The AMS is the case that matters: the <c>ams</c> record is just the hull (3.2 units wide), while
        /// a deployed AMS also shows its spawn doors swung out to +/-1.9 and the equipment terminals along
        /// both flanks. Without these the footprint reads as a plain van and the side terminals -- the
        /// whole reason to park next to one -- are missing.
        /// </summary>
        private static readonly Dictionary<string, (string Lib, string Record)[]> ExtraParts =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ams"] = new[]
                {
                    ("patch1", "ams_spawndoors"),
                    ("patch1", "ams_spawntubes"),
                    ("patch3", "order_terminala"),
                    ("patch3", "order_terminalb"),
                    ("patch3", "matrix_terminalc")
                }
            };

        public static Dictionary<string, Footprint> Build(string planetsideDir)
        {
            var libs = new Dictionary<string, UberModel?>(StringComparer.OrdinalIgnoreCase);
            UberModel? Lib(string name)
            {
                if (libs.TryGetValue(name, out var cached)) return cached;
                // Model records live across stacked libraries: the base uber.ubr, the numbered patch
                // libraries (patch-added assets are NOT back-ported into uber), and the expansion.
                string path = name switch
                {
                    "uber" => Path.Combine(planetsideDir, "uber.ubr"),
                    "expansion1" => Path.Combine(planetsideDir, "expansion1", "expansion1.ubr"),
                    _ when name.StartsWith("patch", StringComparison.OrdinalIgnoreCase) =>
                        Path.Combine(planetsideDir, name, name + ".ubr"),
                    _ => ""
                };
                UberModel? m = null;
                if (File.Exists(path))
                {
                    try { m = UberModel.Load(File.ReadAllBytes(path)); }
                    catch { m = null; }
                }
                libs[name] = m;
                return m;
            }

            // Relative-part lists ship alongside the map contents.
            PakArchive? mapPak = null;
            string mapResources = Path.Combine(planetsideDir, "maps", "map_resources.pak");
            if (File.Exists(mapResources))
            {
                try { mapPak = PakArchive.Load(File.ReadAllBytes(mapResources)); }
                catch { mapPak = null; }
            }

            var result = new Dictionary<string, Footprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var (type, libName, record, ground) in Types)
            {
                var lib = Lib(libName);
                var sys = lib?.FetchMeshSystem(record);
                if (sys == null) continue;

                // The base record, in its own frame.
                var parts = new List<(UberModel.MeshSystem Sys, Matrix4x4 Xf)> { (sys, Matrix4x4.Identity) };

                // Plus any relative sub-objects that complete the structure.
                if (mapPak != null && PartLists.TryGetValue(type, out string? listName) &&
                    mapPak.IndexOf(listName) >= 0)
                {
                    IReadOnlyList<RelativeObject> rel;
                    try { rel = RelativeObjectList.Parse(mapPak.Extract(listName)).Objects; }
                    catch { rel = Array.Empty<RelativeObject>(); }

                    foreach (var p in rel)
                    {
                        var psys = lib?.FetchMeshSystem(p.Name);
                        if (psys == null) continue;
                        Matrix4x4 xf = Matrix4x4.CreateScale(p.Scale) *
                                       Matrix4x4.CreateRotationZ(p.Yaw) *
                                       Matrix4x4.CreateTranslation(p.Position);
                        parts.Add((psys, xf));
                    }
                }

                // Plus any explicitly-named extra records (already positioned about this origin).
                if (ExtraParts.TryGetValue(type, out var extras))
                {
                    foreach (var (extraLib, extraRecord) in extras)
                    {
                        var esys = Lib(extraLib)?.FetchMeshSystem(extraRecord);
                        if (esys != null) parts.Add((esys, Matrix4x4.Identity));
                    }
                }

                var fp = Rasterize(parts, ground);
                if (fp != null) result[type] = fp;
            }

            foreach (var (type, libName, record, dropMaterial) in Profiles)
            {
                var sys = Lib(libName)?.FetchMeshSystem(record);
                if (sys == null) continue;
                var fp = RasterizeProfile(sys, dropMaterial);
                if (fp != null) result[type] = fp;
            }
            return result;
        }

        /// <summary>
        /// Silhouette of a model viewed from the side: triangles are projected onto the vertical plane
        /// containing whichever horizontal axis the model is widest along, so the profile shows the
        /// structure's full face. Sections whose material matches <paramref name="dropMaterial"/> are
        /// skipped entirely.
        /// </summary>
        private static Footprint? RasterizeProfile(UberModel.MeshSystem sys, string dropMaterial)
        {
            // Pass 1: which horizontal axis is the model widest along?
            float mnX = float.MaxValue, mxX = float.MinValue, mnY = float.MaxValue, mxY = float.MinValue;
            foreach (var mesh in sys.Meshes)
            foreach (var s in mesh.Sections)
            {
                if (s.MaterialName.Contains(dropMaterial, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in s.Verts)
                {
                    mnX = MathF.Min(mnX, v.Position.X); mxX = MathF.Max(mxX, v.Position.X);
                    mnY = MathF.Min(mnY, v.Position.Y); mxY = MathF.Max(mxY, v.Position.Y);
                }
            }
            if (mnX > mxX) return null;
            bool useX = (mxX - mnX) >= (mxY - mnY);

            // Pass 2: gather the projected triangles.
            var tris = new List<(float ax, float ay, float bx, float by, float cx, float cy)>();
            float mnU = float.MaxValue, mxU = float.MinValue, mnV = float.MaxValue, mxV = float.MinValue;
            foreach (var mesh in sys.Meshes)
            foreach (var s in mesh.Sections)
            {
                if (s.MaterialName.Contains(dropMaterial, StringComparison.OrdinalIgnoreCase)) continue;
                var v = s.Verts;
                var idx = s.Indices;
                void Add(int a, int b, int c)
                {
                    if (a >= v.Length || b >= v.Length || c >= v.Length) return;
                    Vector3 A = v[a].Position, B = v[b].Position, C = v[c].Position;
                    float au = useX ? A.X : A.Y, bu = useX ? B.X : B.Y, cu = useX ? C.X : C.Y;
                    tris.Add((au, A.Z, bu, B.Z, cu, C.Z));
                    mnU = MathF.Min(mnU, MathF.Min(au, MathF.Min(bu, cu)));
                    mxU = MathF.Max(mxU, MathF.Max(au, MathF.Max(bu, cu)));
                    mnV = MathF.Min(mnV, MathF.Min(A.Z, MathF.Min(B.Z, C.Z)));
                    mxV = MathF.Max(mxV, MathF.Max(A.Z, MathF.Max(B.Z, C.Z)));
                }
                if (s.IsTriStrip)
                {
                    for (int k = 0; k + 2 < idx.Length; k++)
                    {
                        int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                        if (a == b || b == c || a == c) continue;
                        Add(a, b, c);
                    }
                }
                else
                {
                    for (int k = 0; k + 2 < idx.Length; k += 3) Add(idx[k], idx[k + 1], idx[k + 2]);
                }
            }
            if (tris.Count == 0) return null;

            float extent = MathF.Max(mxU - mnU, mxV - mnV) * 1.04f;
            if (extent <= 0f) return null;
            float cell = extent / GridN;
            float ox = (mnU + mxU) * 0.5f - extent * 0.5f;
            float oy = (mnV + mxV) * 0.5f - extent * 0.5f;

            var hit = new bool[GridN * GridN];
            foreach (var t in tris) ScanTri(t, ox, oy, cell, hit);

            var packed = new byte[(GridN * GridN + 7) / 8];
            int on = 0;
            for (int m = 0; m < GridN * GridN; m++)
            {
                if (hit[m]) { packed[m >> 3] |= (byte)(1 << (m & 7)); on++; }
            }
            return on == 0 ? null : new Footprint(ox, oy, cell, GridN, packed, on);
        }

        private static void ScanTri((float ax, float ay, float bx, float by, float cx, float cy) t,
            float ox, float oy, float cell, bool[] hit)
        {
            float d = (t.by - t.cy) * (t.ax - t.cx) + (t.cx - t.bx) * (t.ay - t.cy);
            if (MathF.Abs(d) < 1e-9f) return;
            int i0 = Math.Max(0, (int)((MathF.Min(t.ax, MathF.Min(t.bx, t.cx)) - ox) / cell));
            int i1 = Math.Min(GridN - 1, (int)((MathF.Max(t.ax, MathF.Max(t.bx, t.cx)) - ox) / cell));
            int j0 = Math.Max(0, (int)((MathF.Min(t.ay, MathF.Min(t.by, t.cy)) - oy) / cell));
            int j1 = Math.Min(GridN - 1, (int)((MathF.Max(t.ay, MathF.Max(t.by, t.cy)) - oy) / cell));
            for (int j = j0; j <= j1; j++)
            {
                float py = oy + (j + 0.5f) * cell;
                for (int i = i0; i <= i1; i++)
                {
                    float px = ox + (i + 0.5f) * cell;
                    float w0 = ((t.by - t.cy) * (px - t.cx) + (t.cx - t.bx) * (py - t.cy)) / d;
                    float w1 = ((t.cy - t.ay) * (px - t.cx) + (t.ax - t.cx) * (py - t.cy)) / d;
                    float w2 = 1f - w0 - w1;
                    if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                    hit[j * GridN + i] = true;
                }
            }
        }

        private static Footprint? Rasterize(List<(UberModel.MeshSystem Sys, Matrix4x4 Xf)> parts, float ground)
        {
            // Pass 1: gather above-ground triangles (projected to XY) and their bounds, with each part
            // carried through its own placement transform so composite structures come out whole.
            var tris = new List<(float ax, float ay, float bx, float by, float cx, float cy)>();
            float mnX = float.MaxValue, mxX = float.MinValue, mnY = float.MaxValue, mxY = float.MinValue;

            foreach (var (sys, xf) in parts)
            foreach (var mesh in sys.Meshes)
            foreach (var s in mesh.Sections)
            {
                var v = s.Verts;
                var idx = s.Indices;
                void Add(int a, int b, int c)
                {
                    if (a >= v.Length || b >= v.Length || c >= v.Length) return;
                    Vector3 A = Vector3.Transform(v[a].Position, xf);
                    Vector3 B = Vector3.Transform(v[b].Position, xf);
                    Vector3 C = Vector3.Transform(v[c].Position, xf);
                    if (MathF.Min(A.Z, MathF.Min(B.Z, C.Z)) < ground) return;   // not entirely above ground
                    tris.Add((A.X, A.Y, B.X, B.Y, C.X, C.Y));
                    mnX = MathF.Min(mnX, MathF.Min(A.X, MathF.Min(B.X, C.X)));
                    mxX = MathF.Max(mxX, MathF.Max(A.X, MathF.Max(B.X, C.X)));
                    mnY = MathF.Min(mnY, MathF.Min(A.Y, MathF.Min(B.Y, C.Y)));
                    mxY = MathF.Max(mxY, MathF.Max(A.Y, MathF.Max(B.Y, C.Y)));
                }

                if (s.IsTriStrip)
                {
                    for (int k = 0; k + 2 < idx.Length; k++)
                    {
                        int a = idx[k], b = idx[k + 1], c = idx[k + 2];
                        if (a == b || b == c || a == c) continue;
                        Add(a, b, c);
                    }
                }
                else
                {
                    for (int k = 0; k + 2 < idx.Length; k += 3) Add(idx[k], idx[k + 1], idx[k + 2]);
                }
            }
            if (tris.Count == 0) return null;

            // Square grid centred on the footprint, with a small margin so smoothing has room.
            float extent = MathF.Max(mxX - mnX, mxY - mnY) * 1.04f;
            if (extent <= 0f) return null;
            float cell = extent / GridN;
            float ox = (mnX + mxX) * 0.5f - extent * 0.5f;
            float oy = (mnY + mxY) * 0.5f - extent * 0.5f;

            var hit = new bool[GridN * GridN];
            foreach (var t in tris)
            {
                float d = (t.by - t.cy) * (t.ax - t.cx) + (t.cx - t.bx) * (t.ay - t.cy);
                if (MathF.Abs(d) < 1e-9f) continue;
                int i0 = Math.Max(0, (int)((MathF.Min(t.ax, MathF.Min(t.bx, t.cx)) - ox) / cell));
                int i1 = Math.Min(GridN - 1, (int)((MathF.Max(t.ax, MathF.Max(t.bx, t.cx)) - ox) / cell));
                int j0 = Math.Max(0, (int)((MathF.Min(t.ay, MathF.Min(t.by, t.cy)) - oy) / cell));
                int j1 = Math.Min(GridN - 1, (int)((MathF.Max(t.ay, MathF.Max(t.by, t.cy)) - oy) / cell));
                for (int j = j0; j <= j1; j++)
                {
                    float py = oy + (j + 0.5f) * cell;
                    for (int i = i0; i <= i1; i++)
                    {
                        float px = ox + (i + 0.5f) * cell;
                        float w0 = ((t.by - t.cy) * (px - t.cx) + (t.cx - t.bx) * (py - t.cy)) / d;
                        float w1 = ((t.cy - t.ay) * (px - t.cx) + (t.ax - t.cx) * (py - t.cy)) / d;
                        float w2 = 1f - w0 - w1;
                        if (w0 < -1e-4f || w1 < -1e-4f || w2 < -1e-4f) continue;
                        hit[j * GridN + i] = true;
                    }
                }
            }

            var packed = new byte[(GridN * GridN + 7) / 8];
            int on = 0;
            for (int m = 0; m < GridN * GridN; m++)
            {
                if (hit[m]) { packed[m >> 3] |= (byte)(1 << (m & 7)); on++; }
            }
            return on == 0 ? null : new Footprint(ox, oy, cell, GridN, packed, on);
        }
    }
}
