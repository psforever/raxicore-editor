using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Media;

namespace RaxicoreEditor.Editor.ViewModels
{
    /// <summary>
    /// Vector file-type icons for the asset browser. Each category maps to a simple 16×16 path
    /// (drawn with EvenOdd fill so inner cut-outs show through) and a distinct colour, so file kinds
    /// are scannable at a glance. Geometries/brushes are parsed once and cached.
    /// </summary>
    internal static class FileIcons
    {
        // 16×16 path data per category.
        // "F0 " prefix = even-odd fill rule (so inner shapes cut holes through the outer shape).
        private const string FolderPath = "M1.5,4 H6 L7.5,5.5 H14.5 V13 H1.5 Z";
        private const string ArchivePath = "F0 M2,4 H14 V6 H2 Z M2.5,6.5 H13.5 V13.5 H2.5 Z M6.75,8 H9.25 V9.5 H6.75 Z";
        private const string TexturePath =
            "F0 M2,3 H14 V13 H2 Z M3.5,11.5 L6.5,7.5 L8.5,9.5 L11,6 L12.5,11.5 Z " +
            "M4.7,6 A1.3,1.3 0 1,0 7.3,6 A1.3,1.3 0 1,0 4.7,6 Z";
        private const string MeshPath =
            "M8,1.5 L14.5,5 L8,8.5 L1.5,5 Z M1.5,5 L8,8.5 L8,14.5 L1.5,11 Z M14.5,5 L8,8.5 L8,14.5 L14.5,11 Z";
        private const string SurfacePath = "M2,2 H7 V7 H2 Z M9,2 H14 V7 H9 Z M2,9 H7 V14 H2 Z M9,9 H14 V14 H9 Z";
        private const string AnimPath = "M4,3 L13,8 L4,13 Z";
        private const string DatabasePath = "M2.5,3 H13.5 V5.5 H2.5 Z M2.5,6.75 H13.5 V9.25 H2.5 Z M2.5,10.5 H13.5 V13 H2.5 Z";
        private const string TextPath =
            "F0 M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z M5,7 H11 V8 H5 Z M5,9.3 H11 V10.3 H5 Z M5,11.6 H9 V12.6 H5 Z";
        private const string FilePath = "M3.5,1.5 H9.5 L12.5,4.5 V14.5 H3.5 Z";

        private static readonly Dictionary<string, Geometry> GeometryCache = new();
        private static readonly Dictionary<uint, IBrush> BrushCache = new();

        public static (Geometry Geometry, IBrush Brush) For(BrowserNodeKind kind, string name)
        {
            if (kind == BrowserNodeKind.Folder)
            {
                return (Geo(FolderPath), Brush(0xFFD8B25A));
            }
            if (kind == BrowserNodeKind.Archive)
            {
                return (Geo(ArchivePath), Brush(0xFFC08457));
            }

            string ext = Path.GetExtension(name).ToLowerInvariant();
            string lower = name.ToLowerInvariant();
            switch (ext)
            {
                case ".pak":
                case ".fat":
                case ".fdx":
                    return (Geo(ArchivePath), Brush(0xFFC08457));
                case ".dds":
                    return (Geo(TexturePath), Brush(0xFF5BB98C));
                case ".ubr":
                    // .ubr is a mesh container OR an animation database (anims.ubr, anim_patchN.ubr).
                    return lower.Contains("anim")
                        ? (Geo(AnimPath), Brush(0xFFE08A5B))
                        : (Geo(MeshPath), Brush(0xFF4F9BE0));
                case ".srf":
                    return (Geo(SurfacePath), Brush(0xFFB07CD8));
                case ".adb":
                    return (Geo(DatabasePath), Brush(0xFF6FB0D8));
                case ".txt":
                case ".lst":
                case ".ini":
                case ".cfg":
                case ".log":
                    return (Geo(TextPath), Brush(0xFFAAB2BD));
                default:
                    return (Geo(FilePath), Brush(0xFF8A93A0));
            }
        }

        private static Geometry Geo(string data)
        {
            if (!GeometryCache.TryGetValue(data, out Geometry? g))
            {
                g = Geometry.Parse(data);
                GeometryCache[data] = g;
            }
            return g;
        }

        private static IBrush Brush(uint argb)
        {
            if (!BrushCache.TryGetValue(argb, out IBrush? b))
            {
                b = new SolidColorBrush(Color.FromUInt32(argb));
                BrushCache[argb] = b;
            }
            return b;
        }
    }
}
