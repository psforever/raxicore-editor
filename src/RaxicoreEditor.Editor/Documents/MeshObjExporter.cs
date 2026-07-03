using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>Build OBJ + MTL text and the list of texture PNG sidecars to write.</summary>
        public static Result Build(MeshPart part, string baseName)
        {
            var obj = new StringBuilder();
            var mtl = new StringBuilder();
            var textures = new List<TextureSidecar>();
            var matWritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            obj.Append("# Raxicore Editor OBJ export — ").Append(part.Name).Append('\n');
            obj.Append("# coordinates: Y-up (as displayed in the viewport)\n");
            obj.Append("mtllib ").Append(baseName).Append(".mtl\n");
            obj.Append("o ").Append(Sanitize(part.Name)).Append('\n');

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
                    obj.Append("v ").Append(F(v[o])).Append(' ').Append(F(v[o + 1])).Append(' ').Append(F(v[o + 2])).Append('\n');
                }
                for (int i = 0; i < vcount; i++)
                {
                    int o = i * 8;
                    // OBJ texture origin is bottom-left; the engine's is top-left → flip V.
                    obj.Append("vt ").Append(F(v[o + 6])).Append(' ').Append(F(1f - v[o + 7])).Append('\n');
                }
                for (int i = 0; i < vcount; i++)
                {
                    int o = i * 8;
                    obj.Append("vn ").Append(F(v[o + 3])).Append(' ').Append(F(v[o + 4])).Append(' ').Append(F(v[o + 5])).Append('\n');
                }

                string mat = MaterialId(sm.Material, submeshNo);
                obj.Append("usemtl ").Append(mat).Append('\n');
                uint[] idx = sm.Indices;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    long a = vbase + idx[t], b = vbase + idx[t + 1], c = vbase + idx[t + 2];
                    obj.Append("f ")
                       .Append(a).Append('/').Append(a).Append('/').Append(a).Append(' ')
                       .Append(b).Append('/').Append(b).Append('/').Append(b).Append(' ')
                       .Append(c).Append('/').Append(c).Append('/').Append(c).Append('\n');
                }
                vbase += vcount;

                if (matWritten.Add(mat))
                {
                    mtl.Append("newmtl ").Append(mat).Append('\n');
                    mtl.Append("Kd 0.8 0.8 0.8\n");
                    if (sm.HasTexture && sm.TextureBgra != null)
                    {
                        string png = mat + ".png";
                        mtl.Append("map_Kd ").Append(png).Append('\n');
                        textures.Add(new TextureSidecar
                        {
                            FileName = png, Bgra = sm.TextureBgra, Width = sm.TextureWidth, Height = sm.TextureHeight,
                        });
                    }
                    mtl.Append('\n');
                }
                submeshNo++;
            }

            return new Result { Obj = obj.ToString(), Mtl = mtl.ToString(), Textures = textures };
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
