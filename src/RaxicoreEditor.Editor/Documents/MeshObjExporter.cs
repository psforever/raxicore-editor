using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Exports a <see cref="MeshPart"/> to Wavefront OBJ + MTL. Geometry is the part's bind-pose
    /// view-space vertices (Y-up). The text generation is pure (no UI deps) so it is headlessly
    /// testable; the caller writes the .obj/.mtl files and any texture PNG sidecars.
    /// </summary>
    public static class MeshObjExporter
    {
        public sealed class TextureSidecar
        {
            public required string FileName { get; init; }   // e.g. "bridge_road.png"
            public required byte[] Bgra { get; init; }
            public required int Width { get; init; }
            public required int Height { get; init; }
        }

        public sealed class Result
        {
            public required string Obj { get; init; }
            public required string Mtl { get; init; }
            public required IReadOnlyList<TextureSidecar> Textures { get; init; }
        }

        /// <summary>Build OBJ + MTL text and the list of texture PNG sidecars. Convenience wrapper over
        /// <see cref="Write"/> that materialises the text as strings — only use it for small parts; a large
        /// part (a whole-scene aggregate) can exceed .NET's ~2 GB single-object limit and throw
        /// <see cref="OutOfMemoryException"/> even with RAM to spare. Prefer <see cref="Write"/> to files.</summary>
        public static Result Build(MeshPart part, string baseName)
        {
            var obj = new StringWriter();
            var mtl = new StringWriter();
            IReadOnlyList<TextureSidecar> textures = Write(part, baseName, obj, mtl);
            return new Result { Obj = obj.ToString(), Mtl = mtl.ToString(), Textures = textures };
        }

        /// <summary>Stream OBJ to <paramref name="obj"/> and MTL to <paramref name="mtl"/>, returning the
        /// texture PNG sidecars to write. Writing incrementally keeps peak memory flat, so it handles meshes
        /// of any size (unlike <see cref="Build"/>, which accumulates the whole OBJ as one string).</summary>
        public static IReadOnlyList<TextureSidecar> Write(MeshPart part, string baseName, TextWriter obj, TextWriter mtl)
        {
            var textures = new List<TextureSidecar>();
            var matWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            obj.Write("# Raxicore Editor OBJ export — "); obj.Write(part.Name); obj.Write('\n');
            obj.Write("# coordinates: Y-up (as displayed in the viewport)\n");
            obj.Write("mtllib "); obj.Write(baseName); obj.Write(".mtl\n");
            obj.Write("o "); obj.Write(Sanitize(part.Name)); obj.Write('\n');

            long vbase = 1; // OBJ indices are 1-based and cumulative across the file
            int submeshNo = 0;
            foreach (MeshSubmesh sm in part.Submeshes)
            {
                int vcount = sm.VertexCount;
                if (vcount == 0 || sm.Indices.Length < 3)
                {
                    continue;
                }
                float[] v = sm.Vertices; // stride 8: px,py,pz, nx,ny,nz, u,v
                for (int i = 0; i < vcount; i++)
                {
                    int o = i * 8;
                    obj.Write("v "); obj.Write(F(v[o])); obj.Write(' '); obj.Write(F(v[o + 1])); obj.Write(' '); obj.Write(F(v[o + 2])); obj.Write('\n');
                }
                for (int i = 0; i < vcount; i++)
                {
                    int o = i * 8;
                    // OBJ texture origin is bottom-left; the engine's is top-left → flip V.
                    obj.Write("vt "); obj.Write(F(v[o + 6])); obj.Write(' '); obj.Write(F(1f - v[o + 7])); obj.Write('\n');
                }
                for (int i = 0; i < vcount; i++)
                {
                    int o = i * 8;
                    obj.Write("vn "); obj.Write(F(v[o + 3])); obj.Write(' '); obj.Write(F(v[o + 4])); obj.Write(' '); obj.Write(F(v[o + 5])); obj.Write('\n');
                }

                string mat = MaterialId(sm.Material, submeshNo);
                obj.Write("usemtl "); obj.Write(mat); obj.Write('\n');
                uint[] idx = sm.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    long a = vbase + idx[t], b = vbase + idx[t + 1], c = vbase + idx[t + 2];
                    obj.Write("f ");
                    obj.Write(a); obj.Write('/'); obj.Write(a); obj.Write('/'); obj.Write(a); obj.Write(' ');
                    obj.Write(b); obj.Write('/'); obj.Write(b); obj.Write('/'); obj.Write(b); obj.Write(' ');
                    obj.Write(c); obj.Write('/'); obj.Write(c); obj.Write('/'); obj.Write(c); obj.Write('\n');
                }
                vbase += vcount;

                if (matWritten.Add(mat))
                {
                    mtl.Write("newmtl "); mtl.Write(mat); mtl.Write('\n');
                    mtl.Write("Kd 0.8 0.8 0.8\n");
                    if (sm.HasTexture && sm.TextureBgra != null)
                    {
                        string png = mat + ".png";
                        mtl.Write("map_Kd "); mtl.Write(png); mtl.Write('\n');
                        textures.Add(new TextureSidecar
                        {
                            FileName = png, Bgra = sm.TextureBgra, Width = sm.TextureWidth, Height = sm.TextureHeight,
                        });
                    }
                    mtl.Write('\n');
                }
                submeshNo++;
            }

            return textures;
        }

        private static string MaterialId(string material, int submeshNo)
        {
            string s = Sanitize(material);
            return s.Length == 0 ? $"material_{submeshNo}" : s;
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                sb.Append(char.IsLetterOrDigit(c) || c is '_' or '-' or '+' or '.' ? c : '_');
            }
            return sb.ToString();
        }

        private static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);
    }
}
