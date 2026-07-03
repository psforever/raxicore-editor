using System;
using System.Collections.Generic;
using System.Numerics;
using RaxicoreEditor.EngineAssets.IO;

namespace RaxicoreEditor.EngineAssets.Meshes
{
    public sealed class UberHeader
    {
        public uint Version1;
        public uint Version2;
        public uint RecordCount;
        public uint VertexCount;
        public uint UvCount;
        public uint IndexCount;
        public uint ConnCount;
        public uint BlobBytes;
        public uint PoolBytes;
        public uint SectionOffset;
    }

    public sealed class UberRecord
    {
        public string Name { get; init; } = "";
        public uint FirstIndex { get; init; }     // +0x40
        public uint FirstConn { get; init; }       // +0x44
        public uint FirstBlobByte { get; init; }   // +0x48
        public uint FirstVertex { get; init; }     // +0x4C (pool/ModelData offset per uber_geometry)
    }

    /// <summary>
    /// 'uber' UberMesh container (mapNN.ubr / uber.ubr), ported from the engine-derived reference
    /// implementation's <c>uber_mesh.h</c> + <c>uber_geometry</c>. Parses the 0x2C
    /// header, the 0x50-byte record table, and the shared body arrays (positions, uvs, 20-bit
    /// corner indices, connectivity). The faithful per-CMeshSection assembly lives in the engine's
    /// uber_model; for viewing we expose the shared arrays + a best-effort corner-triple triangle
    /// list (reads correctly for object sub-meshes).
    /// </summary>
    public sealed class UberMesh
    {
        public const int HeaderSize = 0x2C;
        public const int RecordSize = 0x50;
        public const uint CornerMask = 0x000FFFFFu; // 20-bit

        public UberHeader Header { get; } = new();
        public IReadOnlyList<UberRecord> Records => _records;
        public IReadOnlyList<Vector3> Positions => _positions;
        public IReadOnlyList<Vector2> Uvs => _uvs;
        public IReadOnlyList<uint> CornerIndices => _indices;
        public Vector3 BoundsMin { get; private set; }
        public Vector3 BoundsMax { get; private set; }
        public bool HasBounds { get; private set; }

        private readonly List<UberRecord> _records = new();
        private Vector3[] _positions = Array.Empty<Vector3>();
        private Vector2[] _uvs = Array.Empty<Vector2>();
        private uint[] _indices = Array.Empty<uint>();

        public static bool IsUberMesh(ReadOnlySpan<byte> data) =>
            data.Length >= 4 && data[0] == 'u' && data[1] == 'b' && data[2] == 'e' && data[3] == 'r';

        public static UberMesh Load(byte[] data)
        {
            var m = new UberMesh();
            m.Parse(data);
            return m;
        }

        private void Parse(byte[] data)
        {
            if (!IsUberMesh(data))
            {
                throw new InvalidOperationException("Not an 'uber' UberMesh");
            }

            var r = new ByteReader(data);
            r.Skip(4); // magic
            Header.Version1 = r.ReadUInt32();
            Header.Version2 = r.ReadUInt32();
            Header.RecordCount = r.ReadUInt32();
            Header.VertexCount = r.ReadUInt32();
            Header.UvCount = r.ReadUInt32();
            Header.IndexCount = r.ReadUInt32();
            Header.ConnCount = r.ReadUInt32();
            Header.BlobBytes = r.ReadUInt32();
            Header.PoolBytes = r.ReadUInt32();
            Header.SectionOffset = r.ReadUInt32();

            // Records (0x50 each).
            for (uint i = 0; i < Header.RecordCount; i++)
            {
                string name = r.ReadFixedString(64);
                uint firstIndex = r.ReadUInt32();
                uint firstConn = r.ReadUInt32();
                uint firstBlob = r.ReadUInt32();
                uint firstVertex = r.ReadUInt32();
                _records.Add(new UberRecord
                {
                    Name = name,
                    FirstIndex = firstIndex,
                    FirstConn = firstConn,
                    FirstBlobByte = firstBlob,
                    FirstVertex = firstVertex,
                });
            }

            // Vertices (vec3 f32).
            _positions = new Vector3[Header.VertexCount];
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            for (uint i = 0; i < Header.VertexCount; i++)
            {
                var v = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                _positions[i] = v;
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
            if (Header.VertexCount > 0)
            {
                BoundsMin = min;
                BoundsMax = max;
                HasBounds = true;
            }

            // UVs (vec2 f32).
            _uvs = new Vector2[Header.UvCount];
            for (uint i = 0; i < Header.UvCount; i++)
            {
                _uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
            }

            // Index stream: u32 each, low 20 bits = corner index.
            _indices = new uint[Header.IndexCount];
            for (uint i = 0; i < Header.IndexCount; i++)
            {
                _indices[i] = r.ReadUInt32() & CornerMask;
            }
        }

        /// <summary>
        /// DEPRECATED / INACCURATE — do not use for rendering. Treats consecutive corner indices as
        /// raw triangle triples, which is NOT how the engine assembles geometry. The faithful path is
        /// <see cref="UberModel"/> (per-CMeshSection u16 index buffers from MeshData + position via
        /// Vec3Data[LookupData[i]]). Retained only as a low-level diagnostic over the raw corner stream.
        /// </summary>
        public uint[] BuildTriangles(int recordIndex = -1)
        {
            long first = 0;
            long last = _indices.Length;
            if (recordIndex >= 0 && recordIndex < _records.Count)
            {
                first = _records[recordIndex].FirstIndex;
                last = recordIndex + 1 < _records.Count ? _records[recordIndex + 1].FirstIndex : _indices.Length;
                if (last > _indices.Length) last = _indices.Length;
                if (first > last) first = last;
            }

            var tris = new List<uint>((int)(last - first));
            uint vcount = (uint)_positions.Length;
            for (long i = first; i + 2 < last; i += 3)
            {
                uint a = _indices[i], b = _indices[i + 1], c = _indices[i + 2];
                if (a < vcount && b < vcount && c < vcount)
                {
                    tris.Add(a);
                    tris.Add(b);
                    tris.Add(c);
                }
            }
            return tris.ToArray();
        }
    }
}
