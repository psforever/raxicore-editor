using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>Exports an animation clip's keyframes to CSV or JSON for evaluation in external tools
    /// (spreadsheets, scripts, DCC importers). Each bone's own key times are sampled for position and
    /// rotation; a static bone yields a single row/entry at t=0.</summary>
    public static class AnimClipExporter
    {
        /// <summary>Write the clip as CSV: one row per (bone, key-time) with sampled position + rotation.</summary>
        public static void WriteCsv(AnimRecord clip, TextWriter w)
        {
            var ci = CultureInfo.InvariantCulture;
            w.WriteLine("clip,bone,time,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w");
            foreach (AnimTrack tk in clip.Tracks)
            {
                foreach (float t in KeyTimes(tk))
                {
                    Vector3 p = tk.SamplePosition(t);
                    Quaternion q = tk.SampleRotation(t);
                    w.WriteLine(string.Create(ci,
                        $"{Csv(clip.Name)},{Csv(tk.Name)},{t:0.######},{p.X:0.######},{p.Y:0.######},{p.Z:0.######}," +
                        $"{q.X:0.######},{q.Y:0.######},{q.Z:0.######},{q.W:0.######}"));
                }
            }
        }

        /// <summary>Serialize the clip to indented JSON: name, duration, and per-bone key arrays.</summary>
        public static string ToJson(AnimRecord clip)
        {
            var tracks = new List<object>(clip.Tracks.Count);
            foreach (AnimTrack tk in clip.Tracks)
            {
                var keys = new List<object>();
                foreach (float t in KeyTimes(tk))
                {
                    Vector3 p = tk.SamplePosition(t);
                    Quaternion q = tk.SampleRotation(t);
                    keys.Add(new { time = t, pos = new[] { p.X, p.Y, p.Z }, rot = new[] { q.X, q.Y, q.Z, q.W } });
                }
                tracks.Add(new { bone = tk.Name, animated = tk.PosKeys.Count > 0 || tk.RotKeys.Count > 0, keys });
            }
            var dto = new { name = clip.Name, duration = clip.Duration, tracks };
            return JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
        }

        // The union of a track's own position + rotation key times (a static track gets a single row at 0).
        private static IEnumerable<float> KeyTimes(AnimTrack tk)
        {
            var set = new SortedSet<float>();
            foreach (AnimKey<Vector3> k in tk.PosKeys) set.Add(k.Time);
            foreach (AnimKey<Quaternion> k in tk.RotKeys) set.Add(k.Time);
            if (set.Count == 0) set.Add(0f);
            return set;
        }

        private static string Csv(string s) => s.Contains(',') || s.Contains('"')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;
    }
}
