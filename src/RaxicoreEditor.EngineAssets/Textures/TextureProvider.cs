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
            // Authoritative: materials.adb states the exact texture the engine binds for this material
            // (often a differently-named texture than the material — e.g. "building_surface" → "alien1").
            if (_materials?.Lookup(materialName) is MaterialsAdb.MaterialDef def && def.Texture != null &&
                _index.ContainsKey(def.Texture))
            {
                DdsImage? img = Get(def.Texture);
                if (img != null) return (img, def.Texture);
            }
            // Fallback heuristic: the base-name-as-texture case (materials.adb stubs whose texture is just
            // the material name), terrain blends, and mask overlays.
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
            if (_materials?.Lookup(materialName) is MaterialsAdb.MaterialDef def && def.Texture != null &&
                _index.ContainsKey(def.Texture))
            {
                return true;
            }
            foreach (string candidate in Candidates(materialName))
            {
                if (_index.ContainsKey(candidate)) return true;
            }
            return false;
        }

        // Candidate texture keys, in priority order.
        //
        // A section's material name is <base>+<suffix>. The engine looks the whole name up in
        // materials.adb and uses its mat_texture1 (the BASE surface texture). Two shipped naming forms
        // determine which half is the base:
        //   • object materials:  "<baseTexture>+<lightmap>"  — the lightmap suffix starts with '_'
        //                         (e.g. "rails01+_tower_b_shell_ne" → base "rails01"); the BASE is the prefix.
        //   • terrain blends:    "<mapCell>+<mapBlend>"       — (e.g. "map01+map010000" → "map010000");
        //                         the BASE is the suffix.
        // So try the base half first per that rule, then the other half. (Trying the lightmap suffix first
        // — as this used to — applied ~1000 tower/facility surfaces as their shadow lightmap, not their
        // actual texture.) Finally, a last-resort "mask_" insertion for translucent overlays whose texture
        // omits a token from the material name (shield domes: "force_dome_amp_inner" → "force_dome_mask_amp_inner").
        private static IEnumerable<string> Candidates(string material)
        {
            yield return material;
            int plus = material.LastIndexOf('+');
            if (plus >= 0)
            {
                string prefix = plus > 0 ? material.Substring(0, plus) : "";
                string suffix = plus + 1 < material.Length ? material.Substring(plus + 1) : "";
                // "<base>+null" has no lightmap, so the base (prefix) is the texture. "null" is a sentinel,
                // NOT a real texture — even though a blank texture literally named "null" is shipped — so it
                // must never be tried as one (that painted the sea and other "+null" surfaces white).
                bool suffixIsBase = suffix.StartsWith("_", StringComparison.Ordinal) || IsNullToken(suffix);
                string first = suffixIsBase ? prefix : suffix;   // the base texture half
                string second = suffixIsBase ? suffix : prefix;
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
