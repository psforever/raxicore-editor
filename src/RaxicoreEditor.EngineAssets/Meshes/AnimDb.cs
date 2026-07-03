using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using RaxicoreEditor.EngineAssets.IO;

namespace RaxicoreEditor.EngineAssets.Meshes
{
    public sealed class AnimKey<T>
    {
        public float Time { get; init; }    // absolute seconds
        public T Value { get; init; } = default!;
    }

    public sealed class AnimTrack
    {
        public string Name { get; init; } = "";
        public uint PosFlags { get; init; }
        public uint PosSplineCount { get; init; }
        public uint RotFlags { get; init; }
        public uint RotSplineCount { get; init; }
        public bool HasPosSpline => (PosFlags & 0x10) != 0;
        public bool HasRotSpline => (RotFlags & 0x10) != 0;

        // Keyframe data (from the ANIM data stream).
        public Vector3 StaticPosition { get; set; }
        public Quaternion StaticRotation { get; set; } = Quaternion.Identity;
        public List<AnimKey<Vector3>> PosKeys { get; } = new();
        public List<AnimKey<Quaternion>> RotKeys { get; } = new();

        /// <summary>Sample the track's local position at <paramref name="t"/> seconds (lerp between keys).</summary>
        public Vector3 SamplePosition(float t)
        {
            if (PosKeys.Count == 0) return StaticPosition;
            if (t <= PosKeys[0].Time) return PosKeys[0].Value;
            for (int i = 1; i < PosKeys.Count; i++)
            {
                if (t <= PosKeys[i].Time)
                {
                    AnimKey<Vector3> a = PosKeys[i - 1], b = PosKeys[i];
                    float span = b.Time - a.Time;
                    float f = span > 1e-6f ? (t - a.Time) / span : 0f;
                    return Vector3.Lerp(a.Value, b.Value, f);
                }
            }
            return PosKeys[^1].Value;
        }

        /// <summary>Sample the track's local rotation at <paramref name="t"/> seconds (slerp between keys).</summary>
        public Quaternion SampleRotation(float t)
        {
            if (RotKeys.Count == 0) return StaticRotation;
            if (t <= RotKeys[0].Time) return RotKeys[0].Value;
            for (int i = 1; i < RotKeys.Count; i++)
            {
                if (t <= RotKeys[i].Time)
                {
                    AnimKey<Quaternion> a = RotKeys[i - 1], b = RotKeys[i];
                    float span = b.Time - a.Time;
                    float f = span > 1e-6f ? (t - a.Time) / span : 0f;
                    return Quaternion.Slerp(a.Value, b.Value, f);
                }
            }
            return RotKeys[^1].Value;
        }
    }

    public sealed class AnimRecord
    {
        public string Name { get; init; } = "";
        public float Duration { get; init; }
        public uint Field2 { get; init; }
        public List<AnimTrack> Tracks { get; } = new();
    }

    /// <summary>
    /// 'ANIM' UberAnim database (anims.ubr) — faithful port of the engine-derived reference
    /// implementation's <c>uber_anim.{h,cpp}</c> (RE task #199). Two-stream container: a
    /// directory stream from 0x14 (names + per-track flags/spline-counts) and a data stream from
    /// <c>0x14 + data_base_offset</c> (static pos 12B / static quat 16B / SoA splines, plain float32,
    /// times in absolute seconds). Header field roles: data_base_offset @0x08, data_stream_size @0x0C.
    /// </summary>
    public sealed class AnimDb
    {
        public const int HeaderSize = 0x14;
        public const uint HasSplineFlag = 0x10;

        public uint Version { get; private set; }
        public uint DataBaseOffset { get; private set; } // @0x08 — DIRECTORY size; data starts at 0x14+this
        public uint DataStreamSize { get; private set; } // @0x0C
        public uint RecordCount { get; private set; }
        public IReadOnlyList<AnimRecord> Records => _records;
        private readonly List<AnimRecord> _records = new();

        public static bool IsAnim(ReadOnlySpan<byte> d) =>
            d.Length >= 4 && d[0] == 'A' && d[1] == 'N' && d[2] == 'I' && d[3] == 'M';

        public static AnimDb Load(byte[] data)
        {
            var a = new AnimDb();
            a.Parse(data);
            return a;
        }

        private void Parse(byte[] data)
        {
            if (!IsAnim(data))
            {
                throw new InvalidOperationException("Not an 'ANIM' database");
            }
            var r = new ByteReader(data);
            r.Skip(4);                         // magic
            Version = r.ReadUInt32();          // @0x04
            DataBaseOffset = r.ReadUInt32();   // @0x08 (corrected: was read at 0x0C)
            DataStreamSize = r.ReadUInt32();   // @0x0C
            RecordCount = r.ReadUInt32();      // @0x10

            // Directory stream begins at 0x14; data stream at 0x14 + DataBaseOffset.
            int dataCursor = HeaderSize + (int)DataBaseOffset;
            if (dataCursor > data.Length)
            {
                throw new InvalidOperationException("ANIM: data base beyond EOF");
            }

            for (uint i = 0; i < RecordCount; i++)
            {
                var rec = new AnimRecord
                {
                    Name = r.ReadCString(),
                    Duration = r.ReadSingle(),
                    Field2 = r.ReadUInt32(),
                };
                uint trackCount = r.ReadUInt32();
                if (trackCount > 0x10000u)
                {
                    throw new InvalidOperationException("ANIM: implausible track count");
                }

                var tracks = new List<AnimTrack>((int)trackCount);
                for (uint t = 0; t < trackCount; t++)
                {
                    string name = r.ReadCString();
                    uint posFlags = r.ReadUInt32();
                    uint posKeys = (posFlags & HasSplineFlag) != 0 ? r.ReadUInt32() : 0;
                    uint rotFlags = r.ReadUInt32();
                    uint rotKeys = (rotFlags & HasSplineFlag) != 0 ? r.ReadUInt32() : 0;
                    tracks.Add(new AnimTrack
                    {
                        Name = name,
                        PosFlags = posFlags,
                        PosSplineCount = posKeys,
                        RotFlags = rotFlags,
                        RotSplineCount = rotKeys,
                    });
                }

                // Data stream: per track, static pos (12B) [+ SoA pos spline] + static rot (16B)
                // [+ SoA rot spline]. Times are f32 absolute seconds; quats xyzw, renormalized.
                foreach (AnimTrack tk in tracks)
                {
                    tk.StaticPosition = ReadVec3(data, ref dataCursor);
                    if (tk.HasPosSpline)
                    {
                        int n = (int)tk.PosSplineCount;
                        var times = new float[n];
                        for (int k = 0; k < n; k++) times[k] = ReadF32(data, ref dataCursor);
                        for (int k = 0; k < n; k++)
                        {
                            tk.PosKeys.Add(new AnimKey<Vector3> { Time = times[k], Value = ReadVec3(data, ref dataCursor) });
                        }
                    }
                    tk.StaticRotation = ReadQuat(data, ref dataCursor);
                    if (tk.HasRotSpline)
                    {
                        int n = (int)tk.RotSplineCount;
                        var times = new float[n];
                        for (int k = 0; k < n; k++) times[k] = ReadF32(data, ref dataCursor);
                        for (int k = 0; k < n; k++)
                        {
                            tk.RotKeys.Add(new AnimKey<Quaternion> { Time = times[k], Value = ReadQuat(data, ref dataCursor) });
                        }
                    }
                    rec.Tracks.Add(tk);
                }

                _records.Add(rec);
            }

            DataCursorEnd = (uint)dataCursor; // exposed for validation (should equal file length)
        }

        /// <summary>Where the data cursor finished — equals the file length on a well-formed ANIM.</summary>
        public uint DataCursorEnd { get; private set; }

        private static float ReadF32(byte[] d, ref int p)
        {
            float v = BitConverter.UInt32BitsToSingle(
                (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24)));
            p += 4;
            return v;
        }

        private static Vector3 ReadVec3(byte[] d, ref int p)
        {
            float x = ReadF32(d, ref p), y = ReadF32(d, ref p), z = ReadF32(d, ref p);
            return new Vector3(x, y, z);
        }

        private static Quaternion ReadQuat(byte[] d, ref int p)
        {
            // ANIM stores xyzw. Renormalize per the binary: q *= (3 - |q|^2) * 0.5.
            float x = ReadF32(d, ref p), y = ReadF32(d, ref p), z = ReadF32(d, ref p), w = ReadF32(d, ref p);
            var q = new Quaternion(x, y, z, w);
            float lenSq = q.LengthSquared();
            float s = (3f - lenSq) * 0.5f;
            return new Quaternion(q.X * s, q.Y * s, q.Z * s, q.W * s);
        }

        public string ToText(int maxRecords = 2000)
        {
            var sb = new StringBuilder();
            sb.Append("ANIM v").Append(Version).Append(", ").Append(RecordCount).Append(" animations\n\n");
            int shown = 0;
            foreach (AnimRecord rec in _records)
            {
                sb.Append(rec.Name).Append("  (").Append(rec.Duration.ToString("0.###"))
                  .Append("s, ").Append(rec.Tracks.Count).Append(" tracks)\n");
                if (++shown >= maxRecords)
                {
                    sb.Append("… ").Append(_records.Count - shown).Append(" more\n");
                    break;
                }
            }
            return sb.ToString();
        }
    }
}
