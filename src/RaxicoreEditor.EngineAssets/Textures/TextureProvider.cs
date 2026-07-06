using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaxicoreEditor.EngineAssets.Databases;

namespace RaxicoreEditor.EngineAssets.Textures
{
    /// <summary>
    /// Resolves CMeshSection material names to decoded DDS textures by indexing the FLAT (.fat) texture
    /// archives in an asset directory. Engine-derived names map directly: material "bridge_road" →
    /// "bridge_road.dds"; map blend materials "map14+map140000" → "map140000.dds" (suffix after '+').
    ///
    /// The .fat archives total &gt;1 GB, so this only streams each archive's directory (seeking past
    /// payloads) to build a name→(file, offset, length) index, then reads + decodes a single texture on
    /// demand (cached). Construction is cheap; textures are decoded lazily.
    /// </summary>
    public sealed class TextureProvider
    {
        private readonly struct Entry
        {
            public Entry(string fat, long offset, int length) { Fat = fat; Offset = offset; Length = length; }
            public string Fat { get; }
            public long Offset { get; }
            public int Length { get; }
        }

        private readonly Dictionary<string, Entry> _index = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DdsImage?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private readonly MaterialsAdb? _materials;

        public int IndexedTextureCount => _index.Count;

        /// <summary>The engine material database (startup.pak → materials.adb), or null if unavailable. Its
        /// mat_texture1 is the authoritative name→texture link and its pipeline gives translucency.</summary>
        public MaterialsAdb? Materials => _materials;

        /// <summary>Every indexed texture key (base name, no <c>.dds</c>), for a picker UI.</summary>
        public IReadOnlyCollection<string> TextureNames => _index.Keys;

        /// <summary>True if a texture with this exact key (base name, no extension) is indexed.</summary>
        public bool Contains(string textureKey) => !string.IsNullOrEmpty(textureKey) && _index.ContainsKey(textureKey);

        /// <summary>Index every <c>*.fat</c> archive under <paramref name="assetDir"/>, including archives
        /// nested in subdirectories such as <c>pack/</c>, <c>expansion1/</c>, <c>patch1-5/</c>, <c>maps/</c>,
        /// and <c>patchmap/map9x/</c> — most of the engine's texture archives live outside the top level.</summary>
        public TextureProvider(string? assetDir)
        {
            if (string.IsNullOrEmpty(assetDir) || !Directory.Exists(assetDir))
            {
                return;
            }
            foreach (string fat in Directory.EnumerateFiles(assetDir, "*.fat", SearchOption.AllDirectories))
            {
                try { IndexArchive(fat); }
                catch { /* skip an unreadable/foreign archive */ }
            }
            _materials = MaterialsAdb.TryLoad(assetDir);
        }

        private void IndexArchive(string fatPath)
        {
            using FileStream fs = File.OpenRead(fatPath);
            using var br = new BinaryReader(fs);
            Span<byte> magic = stackalloc byte[4];
            if (br.Read(magic) < 4 || magic[0] != 'F' || magic[1] != 'L' || magic[2] != 'A' || magic[3] != 'T')
            {
                return;
            }
            br.ReadUInt32();              // version
            br.ReadUInt32();              // reserved
            uint count = br.ReadUInt32(); // entry count
            br.ReadUInt32();              // total size

            for (uint i = 0; i < count; i++)
            {
                uint nameLen = br.ReadUInt32();
                byte[] nameBytes = br.ReadBytes((int)nameLen);
                br.ReadByte();            // NUL terminator
                uint dataLen = br.ReadUInt32();
                long dataOffset = fs.Position;
                fs.Seek(dataLen, SeekOrigin.Current); // skip the payload

                string key = ToKey(Encoding.Latin1.GetString(nameBytes));
                if (key.Length > 0 && !_index.ContainsKey(key))
                {
                    _index[key] = new Entry(fatPath, dataOffset, (int)dataLen);
                }
            }
        }

        /// <summary>Resolve a section material name to a decoded DDS, or null if not found/decodable.</summary>
        public DdsImage? Resolve(string materialName) => ResolveNamed(materialName).Image;

        /// <summary>
        /// Resolve a material name to a decoded DDS <em>and</em> the texture key it matched (so callers
        /// can later swap that texture — e.g. to another empire variant). Key is null when unresolved.
        /// </summary>
        public (DdsImage? Image, string? Key) ResolveNamed(string materialName)
        {
            if (string.IsNullOrEmpty(materialName))
            {
                return (null, null);
            }
            // Resolve the BASE surface material of the section — the prefix of an object "<base>+<lightmap>"
            // or the blend of a terrain "<mapCell>+<mapBlend>" — NOT the full "<base>+<lightmap>" section
            // name. The full section name's materials.adb entry names the section's baked LIGHTMAP, so
            // querying it painted warpgates and facility shells/walls with their dark lightmap instead of
            // their surface texture (their lightmap suffixes — warp_base_ne, aslod1_cap_ne, …— don't start
            // with '_', so the old "suffix starts with _" test misfiled them as terrain blends).
            string baseMat = BaseMaterial(materialName);

            // Authoritative: materials.adb states the exact texture the engine binds for the base material
            // (often a differently-named texture than the material — e.g. "building_surface" → "alien1").
            if (_materials?.Lookup(baseMat) is MaterialsAdb.MaterialDef def && def.Texture != null &&
                _index.ContainsKey(def.Texture))
            {
                DdsImage? img = Get(def.Texture);
                if (img != null) return (img, def.Texture);
            }
            // The base material is very often itself the texture key.
            if (_index.ContainsKey(baseMat))
            {
                DdsImage? img = Get(baseMat);
                if (img != null) return (img, baseMat);
            }
            // Fallback heuristic: the other half, and mask overlays.
            foreach (string candidate in Candidates(materialName))
            {
                if (_index.ContainsKey(candidate))
                {
                    DdsImage? img = Get(candidate);
                    if (img != null) return (img, candidate);
                }
            }
            return (null, null);
        }

        /// <summary>Decode a texture by its exact index key (base name, no extension); cached. Null if absent.</summary>
        public DdsImage? Get(string textureKey)
        {
            if (string.IsNullOrEmpty(textureKey))
            {
                return null;
            }
            if (_cache.TryGetValue(textureKey, out DdsImage? cached))
            {
                return cached;
            }
            DdsImage? result = null;
            if (_index.TryGetValue(textureKey, out Entry e))
            {
                try { result = DecodeEntry(e); }
                catch { result = null; }
            }
            _cache[textureKey] = result;
            return result;
        }

        private static readonly string[] Empires = { "nc", "tr", "vs" };

        /// <summary>The empire token (<c>nc</c>/<c>tr</c>/<c>vs</c>) embedded in a texture key, or null.
        /// Detected as the occurrence whose swap to another empire names a real, indexed texture — so a
        /// coincidental "tr"/"vs" substring that isn't an empire slot is ignored.</summary>
        public string? DetectEmpire(string textureKey)
        {
            if (string.IsNullOrEmpty(textureKey)) return null;
            string key = textureKey.ToLowerInvariant();
            for (int i = 0; i + 1 < key.Length; i++)
            {
                string tok = key.Substring(i, 2);
                if (Array.IndexOf(Empires, tok) < 0) continue;
                foreach (string other in Empires)
                {
                    if (other == tok) continue;
                    if (_index.ContainsKey(key[..i] + other + key[(i + 2)..])) return tok;
                }
            }
            return null;
        }

        /// <summary>
        /// The <paramref name="targetEmpire"/> (nc/tr/vs) variant key of <paramref name="textureKey"/>, or
        /// null if none is indexed. Swaps each nc/tr/vs occurrence in turn and returns the first swap that
        /// names a real indexed texture (robust to the several engine-derived naming forms — <c>amsnc_1</c>,
        /// <c>magrider_nc01</c>, <c>buggytr_lod</c>).
        /// </summary>
        public string? EmpireVariant(string textureKey, string targetEmpire)
        {
            if (string.IsNullOrEmpty(textureKey) || string.IsNullOrEmpty(targetEmpire)) return null;
            string key = textureKey.ToLowerInvariant();
            targetEmpire = targetEmpire.ToLowerInvariant();
            if (Array.IndexOf(Empires, targetEmpire) < 0) return null;
            for (int i = 0; i + 1 < key.Length; i++)
            {
                string tok = key.Substring(i, 2);
                if (Array.IndexOf(Empires, tok) < 0 || tok == targetEmpire) continue;
                string cand = key[..i] + targetEmpire + key[(i + 2)..];
                if (_index.ContainsKey(cand)) return cand;
            }
            return null;
        }

        public bool CanResolve(string materialName)
        {
            string baseMat = BaseMaterial(materialName);
            if (_materials?.Lookup(baseMat) is MaterialsAdb.MaterialDef def && def.Texture != null &&
                _index.ContainsKey(def.Texture))
            {
                return true;
            }
            if (_index.ContainsKey(baseMat)) return true;
            foreach (string candidate in Candidates(materialName))
            {
                if (_index.ContainsKey(candidate)) return true;
            }
            return false;
        }

        /// <summary>
        /// The BASE surface material of a section name. A section is either an object material
        /// <c>"&lt;base&gt;+&lt;lightmap&gt;"</c> (the base is the PREFIX) or a terrain blend
        /// <c>"&lt;mapCell&gt;+&lt;mapBlend&gt;"</c> (the base is the SUFFIX). A terrain blend is the only
        /// case where the suffix is the surface — recognised structurally: the prefix is a map cell
        /// (<c>"map"</c>+digits, e.g. <c>map05</c>) and the suffix begins with it (<c>map050000</c>). Every
        /// other <c>+</c> form is an object material whose suffix is a lightmap, so the prefix is the base —
        /// regardless of whether the lightmap suffix starts with <c>'_'</c> (many don't: <c>warp_base_ne</c>,
        /// <c>aslod1_cap_ne</c>, <c>cf_cryo_wall1a_ne</c>). <c>"&lt;base&gt;+null"</c> has no lightmap → prefix.
        /// </summary>
        private static string BaseMaterial(string material)
        {
            int plus = material.LastIndexOf('+');
            if (plus <= 0 || plus + 1 >= material.Length)
            {
                return material;
            }
            string prefix = material.Substring(0, plus);
            string suffix = material.Substring(plus + 1);
            if (IsNullToken(suffix))
            {
                return prefix;
            }
            bool terrainBlend = IsMapCell(prefix) && suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            return terrainBlend ? suffix : prefix;
        }

        // A terrain map-cell token: "map" followed by digits ("map05", "map130024").
        private static bool IsMapCell(string s)
        {
            if (s.Length <= 3 || s[0] != 'm' || s[1] != 'a' || s[2] != 'p')
            {
                return false;
            }
            for (int i = 3; i < s.Length; i++)
            {
                if (!char.IsDigit(s[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // Candidate texture keys, in priority order — the fallback after BaseMaterial fails to resolve.
        // Same base-half rule as BaseMaterial (object → prefix, terrain blend → suffix), then the other
        // half, then a last-resort "mask_" insertion for translucent overlays whose texture omits a token
        // (shield domes: "force_dome_amp_inner" → "force_dome_mask_amp_inner").
        private static IEnumerable<string> Candidates(string material)
        {
            yield return material;
            int plus = material.LastIndexOf('+');
            if (plus >= 0)
            {
                string prefix = plus > 0 ? material.Substring(0, plus) : "";
                string suffix = plus + 1 < material.Length ? material.Substring(plus + 1) : "";
                // "null" is a no-texture sentinel — even though a blank texture literally named "null" is
                // shipped — so it must never be tried as one (that painted the sea and "+null" surfaces white).
                bool terrainBlend = IsMapCell(prefix) &&
                                    suffix.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                string first = terrainBlend ? suffix : prefix;   // the base texture half
                string second = terrainBlend ? prefix : suffix;
                if (first.Length > 0 && !IsNullToken(first)) yield return first;
                if (second.Length > 0 && !IsNullToken(second)) yield return second;
            }
            for (int i = 0; i < material.Length; i++)
            {
                if (material[i] == '_')
                {
                    yield return material.Substring(0, i + 1) + "mask_" + material.Substring(i + 1);
                }
            }
        }

        private static bool IsNullToken(string s) => s.Equals("null", StringComparison.OrdinalIgnoreCase);

        private static string ToKey(string entryName)
        {
            string n = entryName;
            if (n.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            {
                n = n.Substring(0, n.Length - 4);
            }
            return n;
        }

        private static DdsImage DecodeEntry(Entry e)
        {
            using FileStream fs = File.OpenRead(e.Fat);
            fs.Seek(e.Offset, SeekOrigin.Begin);
            var buf = new byte[e.Length];
            int read = 0;
            while (read < e.Length)
            {
                int r = fs.Read(buf, read, e.Length - read);
                if (r <= 0) break;
                read += r;
            }
            return DdsImage.Decode(buf);
        }
    }
}
