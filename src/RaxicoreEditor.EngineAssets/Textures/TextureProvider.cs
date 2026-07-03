using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

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

        public int IndexedTextureCount => _index.Count;

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
            foreach (string candidate in Candidates(materialName))
            {
                if (_index.ContainsKey(candidate)) return true;
            }
            return false;
        }

        // Candidate texture keys, in priority order: the full material, then the suffix after the last
        // '+' (map blend materials), then the prefix before it.
        private static IEnumerable<string> Candidates(string material)
        {
            yield return material;
            int plus = material.LastIndexOf('+');
            if (plus >= 0)
            {
                if (plus + 1 < material.Length) yield return material.Substring(plus + 1);
                if (plus > 0) yield return material.Substring(0, plus);
            }
        }

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
