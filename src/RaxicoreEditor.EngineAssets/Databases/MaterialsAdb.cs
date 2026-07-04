using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaxicoreEditor.EngineAssets.Archives;

namespace RaxicoreEditor.EngineAssets.Databases
{
    /// <summary>
    /// The engine's material database (<c>materials.adb</c>, packed inside <c>startup.pak</c>). Maps a
    /// mesh section's material NAME to the texture the engine actually draws for it, plus whether the
    /// material is translucent. This is the authoritative name→texture link: a section material name
    /// frequently differs from its texture (e.g. <c>building_surface</c> → <c>alien1</c>,
    /// <c>105mm_cannon_lod1</c> → <c>105mm_cannon</c>, <c>glass</c> → <c>cubeface01</c>), so resolving
    /// by name identity picks the wrong texture.
    ///
    /// <para>Format: the <c>chunky/asciidatabase</c> wrapper holds a <c>mat_begin</c> container: a string
    /// pool, a name index of <c>(nameOffset, recOffset)</c> pairs, then a command stream of records
    /// (<c>mat_surface</c>, <c>mat_pipeline</c>, <c>mat_texture1..4</c>, <c>mat_anim1..4</c>,
    /// <c>mat_stage*</c>, … <c>mat_end</c>) separated by a zero word. A name's record begins at
    /// <c>commandStart + (recOffset − 1) × 4</c> (<c>recOffset</c> is a 1-based u32-word offset; verified
    /// to land on a record boundary for 4911/4912 shipped materials). Each record's <c>mat_texture1</c>
    /// is the base surface texture; animated materials (water) carry <c>mat_anim1</c> instead; many
    /// materials are bare <c>mat_surface … mat_end</c> stubs whose texture is simply the material name.</para>
    /// </summary>
    public sealed class MaterialsAdb
    {
        /// <summary>The base texture the engine binds for a material (null for texture-less stubs), and
        /// whether the material draws translucent (<c>mat_pipeline alpha_sort/effect</c> or a blend flag).</summary>
        public readonly record struct MaterialDef(string? Texture, bool Translucent);

        private readonly Dictionary<string, MaterialDef> _byName = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Number of material definitions parsed.</summary>
        public int Count => _byName.Count;

        /// <summary>Load and parse <c>&lt;assetDir&gt;/startup.pak → materials.adb</c>, or null if it is
        /// absent or unparseable (the caller then falls back to name-based texture resolution).</summary>
        public static MaterialsAdb? TryLoad(string? assetDir)
        {
            if (string.IsNullOrEmpty(assetDir)) return null;
            string pakPath = Path.Combine(assetDir, "startup.pak");
            if (!File.Exists(pakPath)) return null;
            try
            {
                PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                int idx = pak.IndexOf("materials.adb");
                if (idx < 0) return null;
                var m = new MaterialsAdb();
                m.Parse(pak.Extract(idx));
                return m.Count > 0 ? m : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// The material definition for a section material, trying the whole name first, then the base part
        /// before a <c>'+'</c> (section names are <c>&lt;baseMaterial&gt;+&lt;lightmap&gt;</c>). Returns null
        /// when the material is unknown or carries no texture of its own (a stub) — the caller then resolves
        /// the texture from the material/base name directly.
        /// </summary>
        public MaterialDef? Lookup(string material)
        {
            if (string.IsNullOrEmpty(material)) return null;
            if (_byName.TryGetValue(material, out MaterialDef d) && d.Texture != null) return d;
            // Fall back to the base material only for object "<base>+<lightmap>" names (the lightmap suffix
            // starts with '_'). Terrain blends "<cell>+<blend>" must resolve to the blend (the suffix, which
            // the caller's name heuristic handles) — NOT the shared base cell material, or every tile would
            // get one texture.
            int plus = material.IndexOf('+');
            if (plus > 0 && plus + 1 < material.Length && material[plus + 1] == '_'
                && _byName.TryGetValue(material.Substring(0, plus), out MaterialDef b) && b.Texture != null) return b;
            return null;
        }

        /// <summary>True if the material (or its base before '+') is defined translucent, even when it
        /// carries no texture of its own.</summary>
        public bool IsTranslucent(string material)
        {
            if (string.IsNullOrEmpty(material)) return false;
            if (_byName.TryGetValue(material, out MaterialDef d)) return d.Translucent;
            int plus = material.IndexOf('+');
            if (plus > 0 && plus + 1 < material.Length && material[plus + 1] == '_'
                && _byName.TryGetValue(material.Substring(0, plus), out MaterialDef b)) return b.Translucent;
            return false;
        }

        private void Parse(byte[] data)
        {
            int adb = Find(data, "asciidatabase", 0);
            if (adb < 0) return;
            // root tag(13) + NUL(1) + 4 header bytes + u32 payloadSize(4)
            int o = adb + 13 + 1 + 4 + 4;
            while (o < data.Length && data[o] != 0) o++; // container keyword ("mat_begin")
            o++;                                          // NUL
            if (o + 4 > data.Length) return;
            uint poolSize = U32(data, o); o += 4;
            int poolBase = o;
            long poolEnd = (long)poolBase + poolSize;
            if (poolEnd + 12 > data.Length) return;

            int p = (int)poolEnd;
            uint nameCount = U32(data, p); p += 12; // count + reserved + version
            if (nameCount == 0 || (long)p + (long)nameCount * 8 > data.Length) return;
            int cmdStart = p + (int)nameCount * 8;

            string Sym(uint rel)
            {
                if (rel >= poolSize) return "";
                int a = poolBase + (int)rel, end = a, lim = poolBase + (int)poolSize;
                while (end < lim && data[end] != 0) end++;
                return Encoding.Latin1.GetString(data, a, end - a);
            }

            for (uint i = 0; i < nameCount; i++, p += 8)
            {
                string name = Sym(U32(data, p));
                uint recOff = U32(data, p + 4);
                if (name.Length == 0 || recOff == 0 || _byName.ContainsKey(name)) continue;
                int pos = cmdStart + ((int)recOff - 1) * 4;
                if (pos < cmdStart || pos + 4 > data.Length) continue;
                _byName[name] = ReadRecord(data, pos, Sym);
            }
        }

        private static MaterialDef ReadRecord(byte[] data, int pos, Func<uint, string> sym)
        {
            string? tex1 = null, anim1 = null;
            bool translucent = false;
            int q = pos, guard = 0;
            while (q + 4 <= data.Length && guard++ < 300)
            {
                uint argc = U32(data, q);
                if (argc == 0 || argc > 64 || (long)q + 4 + (long)argc * 4 > data.Length) break;
                string cmd = sym(U32(data, q + 4));
                string Arg1() => argc >= 2 ? sym(U32(data, q + 8)) : "";
                switch (cmd)
                {
                    case "mat_texture1": tex1 = Arg1(); break;
                    case "mat_anim1": anim1 = Arg1(); break;
                    case "mat_pipeline": { string v = Arg1(); if (v == "alpha_sort" || v == "effect") translucent = true; break; }
                    case "mat_alphablend":
                    case "mat_sortalpha": translucent = true; break;
                }
                q += 4 + (int)argc * 4;
                if (cmd == "mat_end") break;
            }
            string? tex = !string.IsNullOrEmpty(tex1) ? tex1 : (!string.IsNullOrEmpty(anim1) ? anim1 : null);
            // Don't surface a texture that isn't a usable flat albedo:
            //  • "null" — the engine's no-texture sentinel (a blank texture named "null" is even shipped),
            //    used by e.g. the "water+null"/"tide" surfaces; treating it as a texture paints them white.
            //  • reflection cube-maps (environment maps applied via a texgen stage, e.g. glass → cubeface01)
            //    and placeholder/debug maps, which read wrong as flat textures.
            // In each case the caller falls back to resolving the albedo from the material name instead.
            if (tex != null && IsNonAlbedo(tex)) tex = null;
            return new MaterialDef(tex, translucent);
        }

        private static bool IsNonAlbedo(string texture) =>
            texture.Equals("null", StringComparison.OrdinalIgnoreCase)
            || texture.StartsWith("cubemap", StringComparison.OrdinalIgnoreCase)
            || texture.StartsWith("cubeface", StringComparison.OrdinalIgnoreCase)
            || texture.Contains("debug", StringComparison.OrdinalIgnoreCase);

        private static uint U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static int Find(byte[] hay, string needle, int from)
        {
            for (int i = from; i + needle.Length <= hay.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < needle.Length; k++)
                {
                    if (hay[i + k] != (byte)needle[k]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
