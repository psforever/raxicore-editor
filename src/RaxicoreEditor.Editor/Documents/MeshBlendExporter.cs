using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Exports a <see cref="MeshPart"/> to a Blender <c>.blend</c> file. A <c>.blend</c> is Blender's own
    /// internal format, so nothing but Blender can write one — this reuses <see cref="MeshObjExporter"/> to
    /// stage the mesh as OBJ + MTL + PNGs in a temp folder, then drives a headless Blender
    /// (<c>blender --background --python …</c>) to import it and save a self-contained <c>.blend</c> with the
    /// textures packed in. Requires a local Blender install (auto-detected, or set explicitly).
    /// </summary>
    public static class MeshBlendExporter
    {
        // Imports the staged OBJ into an empty scene and saves it as a packed .blend. Kept tolerant of the
        // operator rename across Blender versions (wm.obj_import is 3.3+; import_scene.obj is older).
        private const string PythonScript = @"
import bpy, sys
argv = sys.argv
sep = argv.index('--')
obj_path, blend_path = argv[sep + 1], argv[sep + 2]
bpy.ops.wm.read_factory_settings(use_empty=True)
try:
    bpy.ops.wm.obj_import(filepath=obj_path)
except AttributeError:
    bpy.ops.import_scene.obj(filepath=obj_path)
try:
    bpy.ops.file.pack_all()   # embed the texture images so the .blend is self-contained
except Exception:
    pass
# Continents span thousands of units; Blender's 1 km factory viewport clip would slice a whole map into
# flat clipped bands on open. Widen the saved clip range (and the camera's) so it opens showing the model.
try:
    for scr in bpy.data.screens:
        for area in scr.areas:
            if area.type == 'VIEW_3D':
                for sp in area.spaces:
                    if sp.type == 'VIEW_3D':
                        sp.clip_start = 0.05
                        sp.clip_end = 500000.0
                        sp.shading.type = 'MATERIAL'   # show the packed textures, not flat solid grey
    for cam in bpy.data.cameras:
        cam.clip_start = 0.05
        cam.clip_end = 500000.0
except Exception:
    pass
bpy.ops.wm.save_as_mainfile(filepath=blend_path)
";

        /// <summary>True when a Blender executable can be located (used to enable/label the menu).</summary>
        public static bool IsAvailable(string? overridePath) => FindBlender(overridePath) != null;

        /// <summary>Locate a Blender executable. Order: explicit override, then PATH, then the standard
        /// Windows install locations (newest version first). Returns null if none is found.</summary>
        public static string? FindBlender(string? overridePath)
        {
            if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            {
                return overridePath;
            }

            string exeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
            string? onPath = FindOnPath(exeName);
            if (onPath != null)
            {
                return onPath;
            }

            var roots = new List<string>();
            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string pf = Environment.GetFolderPath(sf);
                if (!string.IsNullOrEmpty(pf))
                {
                    roots.Add(Path.Combine(pf, "Blender Foundation"));
                    roots.Add(Path.Combine(pf, "Steam", "steamapps", "common", "Blender"));
                }
            }
            foreach (string root in roots)
            {
                if (!Directory.Exists(root)) continue;
                // A version-named subfolder (Blender Foundation\Blender 5.1\blender.exe), newest first…
                foreach (string dir in Directory.EnumerateDirectories(root).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string exe = Path.Combine(dir, exeName);
                    if (File.Exists(exe)) return exe;
                }
                // …or directly in the folder (the Steam layout).
                string direct = Path.Combine(root, exeName);
                if (File.Exists(direct)) return direct;
            }
            return null;
        }

        private static string? FindOnPath(string exeName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path)) return null;
            foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string cand = Path.Combine(dir.Trim('"'), exeName);
                    if (File.Exists(cand)) return cand;
                }
                catch { /* skip a malformed PATH entry */ }
            }
            return null;
        }

        /// <summary>
        /// Export <paramref name="part"/> to <paramref name="blendPath"/> via <paramref name="blenderExe"/>.
        /// <paramref name="writePng"/> encodes a BGRA texture to a PNG file; it defaults to the built-in
        /// dependency-free encoder (thread-safe, so callers can run this off the UI thread). Throws with
        /// Blender's output on failure.
        /// </summary>
        public static void Export(MeshPart part, string blendPath, string blenderExe,
            Action<string, byte[], int, int>? writePng = null)
        {
            writePng ??= WriteBgraPng;
            string work = Path.Combine(Path.GetTempPath(), "raxicore_blend_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string baseName = SanitizeFile(part.Name);
                if (baseName.Length == 0) baseName = "mesh";

                // Stream the OBJ/MTL straight to disk — a large part's OBJ text can exceed .NET's ~2 GB
                // single-object cap and OOM if built as one string (this is what "insufficient memory" was).
                string objPath = Path.Combine(work, baseName + ".obj");
                IReadOnlyList<MeshObjExporter.TextureSidecar> texList;
                using (var objW = new StreamWriter(objPath, false))
                using (var mtlW = new StreamWriter(Path.Combine(work, baseName + ".mtl"), false))
                {
                    texList = MeshObjExporter.Write(part, baseName, objW, mtlW);
                }
                foreach (MeshObjExporter.TextureSidecar tex in texList)
                {
                    try { writePng(Path.Combine(work, tex.FileName), tex.Bgra, tex.Width, tex.Height); }
                    catch { /* skip an unencodable texture; geometry still exports */ }
                }
                string scriptPath = Path.Combine(work, "export.py");
                File.WriteAllText(scriptPath, PythonScript);

                var psi = new ProcessStartInfo
                {
                    FileName = blenderExe,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = work,
                };
                foreach (string a in new[] { "--background", "--factory-startup", "--python", scriptPath, "--", objPath, blendPath })
                {
                    psi.ArgumentList.Add(a);
                }

                using Process p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Blender.");
                // Drain both pipes concurrently — reading one fully before the other can deadlock if Blender
                // fills the other pipe's buffer while blocked on it.
                System.Threading.Tasks.Task<string> outTask = p.StandardOutput.ReadToEndAsync();
                System.Threading.Tasks.Task<string> errTask = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(180_000))
                {
                    try { p.Kill(true); } catch { }
                    throw new TimeoutException("Blender did not finish within 3 minutes.");
                }
                string stdout = outTask.GetAwaiter().GetResult();
                string stderr = errTask.GetAwaiter().GetResult();
                if (p.ExitCode != 0 || !File.Exists(blendPath))
                {
                    throw new InvalidOperationException(
                        $"Blender exited with code {p.ExitCode} and produced no file. " + Tail(stderr + "\n" + stdout));
                }
            }
            finally
            {
                try { Directory.Delete(work, recursive: true); } catch { /* temp; leave it if locked */ }
            }
        }

        /// <summary>Dependency-free BGRA→PNG encoder (RGBA, 8-bit, keeps alpha) — used as the default texture
        /// writer so the export needs no UI/image library and is safe to run on a background thread.</summary>
        public static void WriteBgraPng(string path, byte[] bgra, int w, int h)
        {
            using var fs = new FileStream(path, FileMode.Create);
            fs.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
            var ih = new byte[13];
            BE(ih, 0, w); BE(ih, 4, h); ih[8] = 8; ih[9] = 6; // 8-bit, colour type 6 (RGBA)
            Chunk(fs, "IHDR", ih);
            // filter byte 0 per scanline, pixels swizzled BGRA→RGBA
            var raw = new byte[h * (1 + w * 4)];
            for (int y = 0; y < h; y++)
            {
                int dst = y * (1 + w * 4) + 1;
                int src = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    raw[dst + x * 4 + 0] = bgra[src + x * 4 + 2];
                    raw[dst + x * 4 + 1] = bgra[src + x * 4 + 1];
                    raw[dst + x * 4 + 2] = bgra[src + x * 4 + 0];
                    raw[dst + x * 4 + 3] = bgra[src + x * 4 + 3];
                }
            }
            Chunk(fs, "IDAT", Zlib(raw));
            Chunk(fs, "IEND", Array.Empty<byte>());

            static void BE(byte[] a, int o, int v) { a[o] = (byte)(v >> 24); a[o + 1] = (byte)(v >> 16); a[o + 2] = (byte)(v >> 8); a[o + 3] = (byte)v; }
            static void Chunk(Stream s, string t, byte[] d)
            {
                var l = new byte[4]; BE(l, 0, d.Length); s.Write(l);
                byte[] tb = System.Text.Encoding.ASCII.GetBytes(t); s.Write(tb); s.Write(d);
                uint c = Crc(tb, Crc(d, 0xFFFFFFFFu)) ^ 0xFFFFFFFFu;
                var cb = new byte[4]; BE(cb, 0, (int)c); s.Write(cb);
            }
            static uint Crc(byte[] d, uint c)
            {
                for (int i = 0; i < d.Length; i++) { c ^= d[i]; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1; }
                return c;
            }
            static byte[] Zlib(byte[] d)
            {
                using var ms = new MemoryStream();
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var df = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) df.Write(d, 0, d.Length);
                uint a = 1, b = 0;
                foreach (byte by in d) { a = (a + by) % 65521; b = (b + a) % 65521; }
                uint ad = (b << 16) | a;
                ms.WriteByte((byte)(ad >> 24)); ms.WriteByte((byte)(ad >> 16)); ms.WriteByte((byte)(ad >> 8)); ms.WriteByte((byte)ad);
                return ms.ToArray();
            }
        }

        private static string SanitizeFile(string name)
        {
            var sb = new System.Text.StringBuilder(name.Length);
            foreach (char c in name)
            {
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }
            return sb.ToString().Trim();
        }

        private static string Tail(string s)
        {
            s = s.Trim();
            const int max = 600;
            return s.Length <= max ? s : "…" + s[^max..];
        }
    }
}
