using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using RaxicoreEditor.EngineAssets.Archives;

namespace RaxicoreEditor.EngineAssets.Databases
{
    /// <summary>
    /// The engine's atmosphere data for a zone's sky: the per-zone day/night lighting cycle
    /// (<c>timeofday.adb</c>) resolved against the named light definitions (<c>light3d.adb</c>) to give the
    /// horizon (fog) colour, sky/ambient colour, sun colour and sun direction that drive a procedural sky.
    /// Both databases are AsciiDatabase resources inside <c>startup.pak</c> (a symbol pool + name table +
    /// command-node stream, the same shape as <see cref="MaterialsAdb"/>).
    /// </summary>
    public sealed class SkyDatabase
    {
        /// <summary>A zone's daytime atmosphere: colours (0-1 RGB), sun direction, and the engine sky-dome
        /// panorama texture key (<c>skydome20</c> = the galaxy/space sky, <c>skyartic</c>, <c>skydeserta</c>).</summary>
        public readonly record struct SkyLight(Vector3 Horizon, Vector3 Sky, Vector3 Sun, Vector3 SunDir, string Name, string Texture);

        private readonly Dictionary<string, SkyLight> _lights = new(StringComparer.OrdinalIgnoreCase);
        // cycle name -> the light names bound across its day/night keyframes, in order.
        private readonly Dictionary<string, List<string>> _cycles = new(StringComparer.OrdinalIgnoreCase);

        public int LightCount => _lights.Count;
        public int CycleCount => _cycles.Count;

        /// <summary>All cycle names (for a manual override picker).</summary>
        public IReadOnlyCollection<string> Cycles => _cycles.Keys;

        public static SkyDatabase? TryLoad(string? assetDir)
        {
            if (string.IsNullOrEmpty(assetDir)) return null;
            string pakPath = Path.Combine(assetDir, "startup.pak");
            if (!File.Exists(pakPath)) return null;
            try
            {
                PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                byte[]? l3d = Extract(pak, "light3d.adb");
                byte[]? tod = Extract(pak, "timeofday.adb");
                if (l3d == null || tod == null) return null;
                var db = new SkyDatabase();
                db.ParseLights(l3d);
                db.ParseCycles(tod);
                return db.LightCount > 0 ? db : null;
            }
            catch { return null; }
        }

        private static byte[]? Extract(PakArchive pak, string name)
        {
            foreach (PakEntry e in pak.Entries)
                if (e.Name.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                    return pak.Extract(e.Name);
            return null;
        }

        /// <summary>
        /// The sky for a zone stem (<c>map03</c>, <c>ugd01</c>). Resolves the zone's cycle — an exact
        /// <c>&lt;stem&gt;</c> or <c>&lt;stem&gt;cycle</c> match, else the shared daylight default for a
        /// surface zone (the engine's zone→cycle table is compiled into the client, so unmatched surface
        /// zones fall back to the common daylight). Returns null only when no sky data is available.
        /// </summary>
        public SkyLight? ForZone(string stem)
        {
            string? cyc = ResolveCycle(stem);
            if (cyc == null) return null;
            return DayLightOf(cyc);
        }

        /// <summary>The day sky for a specific cycle name (for the override picker).</summary>
        public SkyLight? ForCycle(string cycle) => _cycles.ContainsKey(cycle) ? DayLightOf(cycle) : null;

        private string? ResolveCycle(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            stem = stem.ToLowerInvariant();
            if (_cycles.ContainsKey(stem)) return stem;
            if (_cycles.ContainsKey(stem + "cycle")) return stem + "cycle";
            // Caverns: ugdNN -> its own cycle, else the shared 'underground'.
            if (stem.StartsWith("ugd"))
                return _cycles.ContainsKey("underground") ? "underground" : null;
            // Surface continents without a named cycle use the common daylight (map01).
            if (stem.StartsWith("map"))
                return _cycles.ContainsKey("map01") ? "map01" : null;
            return null;
        }

        // Pick a cycle's daytime light: the keyframe light whose name reads as day/noon, else the first.
        private SkyLight? DayLightOf(string cycle)
        {
            if (!_cycles.TryGetValue(cycle, out List<string>? refs) || refs.Count == 0) return null;
            string pick = refs.Find(r => r.Contains("day", StringComparison.OrdinalIgnoreCase))
                          ?? refs.Find(r => r.Contains("atmos", StringComparison.OrdinalIgnoreCase))
                          ?? refs[0];
            if (!_lights.TryGetValue(pick, out SkyLight l)) return null;
            return l with { Texture = SkyTextureFor(cycle, refs) };
        }

        // Choose the sky-dome panorama for a cycle. The exact zone→skydome map is compiled into the client,
        // so map by climate — arctic cycles use skyartic, desert cycles skydeserta — and everything else the
        // common space sky (skydome20, the galaxy/dust panorama). Users can override via the picker.
        private static string SkyTextureFor(string cycle, List<string> lights)
        {
            bool Any(string k) => cycle.Contains(k, StringComparison.OrdinalIgnoreCase)
                                  || lights.Exists(l => l.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (Any("artic") || Any("arctic")) return "skyartic";
            if (Any("desert")) return "skydeserta";
            return "skydome20";
        }

        /// <summary>The sky-dome panorama texture keys that ship (for a manual override picker).</summary>
        public static readonly string[] SkyTextures =
        {
            "skydome20", "skyartic", "skydeserta", "skydome5", "skydome10", "skydome11",
            "skydome12", "skydome13", "skydome16", "skyplains_layera",
        };

        // ---- parsing ---------------------------------------------------------------------------

        private void ParseLights(byte[] d)
        {
            foreach ((string name, List<(string cmd, string[] args)> cmds) in Records(d))
            {
                Vector3 fog = default, amb = default, dif = default, dir = default;
                foreach ((string c, string[] a) in cmds)
                {
                    if (c == "lc_fogcolor" && a.Length > 0) fog = Rgb(a[0]);
                    else if (c == "lc_ambient" && a.Length > 0) amb = Rgb(a[0]);
                    else if (c == "lc_diffuse" && a.Length > 0) dif = Rgb(a[0]);
                    else if (c == "lc_direction" && a.Length >= 3) dir = Vec(a[0], a[1], a[2]);
                }
                _lights[name] = new SkyLight(fog, amb, dif, dir, name, "");
            }
        }

        private void ParseCycles(byte[] d)
        {
            foreach ((string name, List<(string cmd, string[] args)> cmds) in Records(d))
            {
                var refs = new List<string>();
                foreach ((string c, string[] a) in cmds)
                    if (c == "tod_time")
                        foreach (string t in a)
                            if (_lights.ContainsKey(t)) refs.Add(t);
                if (refs.Count > 0) _cycles[name] = refs;
            }
        }

        // Walk an AsciiDatabase: symbol pool + name table + per-record command-node stream.
        private static IEnumerable<(string name, List<(string cmd, string[] args)> cmds)> Records(byte[] d)
        {
            int adb = Find(d, "asciidatabase", 0);
            if (adb < 0) yield break;
            int o = adb + 13 + 1 + 4 + 4;
            while (o < d.Length && d[o] != 0) o++;
            o++;
            uint poolSize = U32(d, o); o += 4;
            int poolBase = o;
            long poolEnd = poolBase + poolSize;
            if (poolEnd + 12 > d.Length) yield break;
            int p = (int)poolEnd;
            uint nameCount = U32(d, p); p += 12;
            if ((long)p + (long)nameCount * 8 > d.Length) yield break;
            int cmdStart = p + (int)nameCount * 8;

            string Sym(uint rel)
            {
                if (rel >= poolSize) return "";
                int a = poolBase + (int)rel, e = a, lim = poolBase + (int)poolSize;
                while (e < lim && d[e] != 0) e++;
                return Encoding.Latin1.GetString(d, a, e - a);
            }

            for (uint i = 0; i < nameCount; i++, p += 8)
            {
                string name = Sym(U32(d, p));
                uint recOff = U32(d, p + 4);
                if (name.Length == 0 || recOff == 0) continue;
                int pos = cmdStart + ((int)recOff - 1) * 4;
                if (pos < cmdStart || pos + 4 > d.Length) continue;
                var cmds = new List<(string, string[])>();
                int q = pos, guard = 0;
                while (q + 4 <= d.Length && guard++ < 500)
                {
                    uint argc = U32(d, q);
                    if (argc == 0 || argc > 64 || (long)q + 4 + (long)argc * 4 > d.Length) break;
                    string cmd = Sym(U32(d, q + 4));
                    var args = new string[argc - 1];
                    for (int a = 1; a < argc; a++) args[a - 1] = Sym(U32(d, q + 4 + a * 4));
                    cmds.Add((cmd, args));
                    q += 4 + (int)argc * 4;
                    if (cmd.EndsWith("_end", StringComparison.Ordinal)) break;
                }
                yield return (name, cmds);
            }
        }

        // "AARRGGBB" or "RRGGBB" hex -> 0-1 RGB.
        private static Vector3 Rgb(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return default;
            if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return default;
            if (hex.Length <= 6) v &= 0xFFFFFF;
            return new Vector3(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }

        private static Vector3 Vec(string x, string y, string z) => new(F(x), F(y), F(z));
        private static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;

        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static int Find(byte[] hay, string needle, int from)
        {
            for (int i = from; i + needle.Length <= hay.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < needle.Length; k++)
                    if (hay[i + k] != (byte)needle[k]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }
    }
}
