using System;
using System.Collections.Generic;
using System.Numerics;
using RaxicoreEditor.EngineAssets.IO;

namespace RaxicoreEditor.EngineAssets.Maps
{
    /// <summary>
    /// One placed scene object from a <c>map_objects</c> record (40 bytes): an index into the
    /// <c>map_names</c> list, a world position, a per-axis scale, and a 2-component rotation.
    /// </summary>
    public readonly record struct MapObject(
        int NameIndex, string Name, Vector3 Position, Vector3 Scale, Vector2 Rotation);

    /// <summary>
    /// <c>contents_mapNN.mpo</c> — the continent's tile/object manifest (packed inside
    /// <c>maps/map_resources.pak</c>). It is a <c>chunky</c> container: a 16-byte magic field,
    /// a <c>u16</c> version and <c>u32</c> section count, then a flat list of sections, each a
    /// <c>[16-byte NUL-padded keyword][u16 flag][u32 byteLen][payload]</c>.
    ///
    /// Layout confirmed byte-for-byte against the shipped files (see
    /// <c>docs/rendering/continent-scene.md §4</c>): <c>map_names</c> = <c>u32 count</c> + a
    /// NUL-delimited name list; <c>map_objects</c> = <c>u32 count</c> + <c>count</c>×40-byte
    /// placement records (<c>u16 name_index, u16 flag, f32 pos[3], f32 scale[3], f32 rot[2],
    /// 4-byte tail</c>); <c>map_sections</c>/<c>map_water</c> = <c>u32 count</c> + <c>u32</c> packed
    /// cell ids (<c>col = id &amp; 0x1F, row = id &gt;&gt; 5</c>); <c>map_header</c> = <c>f32[2]</c>.
    /// </summary>
    public sealed class MpoFile
    {
        private const int KeywordField = 16;
        private const int ObjectRecord = 40;

        public IReadOnlyList<string> Names => _names;
        public IReadOnlyList<MapObject> Objects => _objects;
        public IReadOnlyList<uint> TerrainTileIds => _terrainIds; // map_sections
        public IReadOnlyList<uint> WaterCellIds => _waterIds;     // map_water
        public float HeaderA { get; private set; }                // map_header f32[0]
        public float HeaderB { get; private set; }                // map_header f32[1]

        private readonly List<string> _names = new();
        private readonly List<MapObject> _objects = new();
        private readonly List<uint> _terrainIds = new();
        private readonly List<uint> _waterIds = new();

        public static bool IsChunky(ReadOnlySpan<byte> d) =>
            d.Length >= 6 && d[0] == 'c' && d[1] == 'h' && d[2] == 'u' && d[3] == 'n' && d[4] == 'k' && d[5] == 'y';

        public static MpoFile Parse(byte[] data)
        {
            var m = new MpoFile();
            m.ParseInternal(data);
            return m;
        }

        /// <summary>Unpack a packed cell id: <c>col = id &amp; 0x1F</c>, <c>row = id &gt;&gt; 5</c>.</summary>
        public static (int Col, int Row) UnpackCell(uint id) => ((int)(id & 0x1F), (int)(id >> 5));

        private void ParseInternal(byte[] data)
        {
            if (!IsChunky(data))
            {
                throw new InvalidOperationException("Not a 'chunky' .mpo");
            }

            var r = new ByteReader(data) { Position = KeywordField }; // skip the 16-byte 'chunky' field
            r.ReadUInt16();                       // version (=1)
            uint sectionCount = r.ReadUInt32();

            for (uint s = 0; s < sectionCount; s++)
            {
                if (r.Remaining < KeywordField + 2 + 4)
                {
                    break;
                }
                string keyword = r.ReadFixedString(KeywordField);
                r.ReadUInt16();                   // flag (=1)
                uint byteLen = r.ReadUInt32();
                int payloadStart = r.Position;
                if (byteLen > (uint)r.Remaining)
                {
                    break; // truncated/corrupt section table
                }

                switch (keyword)
                {
                    case "map_names": ReadNames(r); break;
                    case "map_objects": ReadObjects(r); break;
                    case "map_sections": ReadIds(r, _terrainIds); break;
                    case "map_water": ReadIds(r, _waterIds); break;
                    case "map_header": ReadHeader(r, byteLen); break;
                    default: break; // map_lakes etc. — skipped
                }

                r.Position = payloadStart + (int)byteLen; // resync regardless of how much a reader consumed
            }

            // map_objects may appear before map_names; re-resolve any names left unresolved.
            for (int i = 0; i < _objects.Count; i++)
            {
                MapObject o = _objects[i];
                if (o.Name.Length == 0 && o.NameIndex >= 0 && o.NameIndex < _names.Count)
                {
                    _objects[i] = o with { Name = _names[o.NameIndex] };
                }
            }
        }

        private void ReadNames(ByteReader r)
        {
            uint count = r.ReadUInt32();
            for (uint i = 0; i < count && !r.EndOfData; i++)
            {
                _names.Add(r.ReadCString());
            }
        }

        private void ReadObjects(ByteReader r)
        {
            uint count = r.ReadUInt32();
            for (uint i = 0; i < count; i++)
            {
                if (r.Remaining < ObjectRecord)
                {
                    break;
                }
                int nameIndex = r.ReadUInt16();
                r.ReadUInt16();                                   // flag
                var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var scale = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                var rot = new Vector2(r.ReadSingle(), r.ReadSingle());
                r.Skip(4);                                        // tail
                string name = nameIndex >= 0 && nameIndex < _names.Count ? _names[nameIndex] : "";
                _objects.Add(new MapObject(nameIndex, name, pos, scale, rot));
            }
        }

        private static void ReadIds(ByteReader r, List<uint> outIds)
        {
            uint count = r.ReadUInt32();
            for (uint i = 0; i < count && r.Remaining >= 4; i++)
            {
                outIds.Add(r.ReadUInt32());
            }
        }

        private void ReadHeader(ByteReader r, uint byteLen)
        {
            if (byteLen >= 8)
            {
                HeaderA = r.ReadSingle();
                HeaderB = r.ReadSingle();
            }
        }
    }
}
