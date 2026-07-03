using System;
using System.Collections.Generic;
using System.Numerics;

namespace RaxicoreEditor.EngineAssets.Meshes
{
    /// <summary>
    /// Faithful port of the engine-derived reference implementation's <c>uber_model.{h,cpp}</c> — the binary-confirmed
    /// per-CMeshSection geometry assembly for the .ubr container.
    ///
    /// Where <see cref="UberMesh"/> handles the 0x2C header + 0x50 record "directory" layer, this
    /// decodes the 6 body sections (Vec3Data / Vec2Data / LookupData / MeshData / U32Data / ModelData)
    /// and walks the ModelData/pool command stream one CMeshSystem at a time. The crucial difference
    /// from the naive corner-triple builder: a section's vertices take their position from
    /// <c>Vec3Data[LookupData[i]]</c> and its triangles from a dedicated u16 index buffer sliced out
    /// of MeshData — NOT from raw consecutive corner indices.
    ///
    /// Provenance (per the C++ header): outer format + pool model + vertex-assembly dispatch are all
    /// binary-confirmed (A4.verify.body.assembly, 2026-05-28). The gated subsystems (Portal / Skeleton
    /// / Collision / AAB) are ported verbatim so their stream bytes are consumed in the exact binary
    /// read order, keeping the ModelData cursor aligned for the following mesh sections.
    /// </summary>
    public sealed class UberModel
    {
        // ----- vertex types (confirmed against the dispatch switch @ 0x009d9440) -------------------
        public enum VertexType : uint
        {
            None = 0,
            Lit = 1,
            Unlit = 2,
            Thin = 3,
            Raw = 4,
            FatDeform = 5,
            Fat = 6,
            Deform = 14,
        }

        // ----- CMeshSystem flag bits (gating bits confirmed against FUN_009d8ad0) ------------------
        private const uint FlagPortalSystem = 0x00001;
        private const uint FlagVec3Array = 0x00004;
        private const uint FlagSkeletons = 0x00008;
        private const uint FlagUserData = 0x00020;
        private const uint FlagCollision = 0x00080;
        private const uint FlagNoAab = 0x02000;
        private const uint FlagHasEf = 0x08000;

        // SAAB struct sizes (Pack=1).
        private const int SizeAabFace = 10;   // SAABFace
        private const int SizeAabNode = 0x28; // SAABBNode

        // ===========================================================================================
        // Stream reader — LE cursor over a byte slice. Mirrors UberStreamReader.
        // ===========================================================================================
        public sealed class StreamReader
        {
            private readonly byte[] _buf;
            private readonly int _start;
            private readonly int _size;
            private int _off;

            public StreamReader(byte[] buf, int start, int size)
            {
                _buf = buf;
                _start = start;
                _size = size;
                _off = 0;
            }

            public int Remaining => _off < _size ? _size - _off : 0;

            public bool ReadU8(out byte v)
            {
                v = 0;
                if (Remaining < 1) return false;
                v = _buf[_start + _off];
                _off += 1;
                return true;
            }

            public bool ReadU16(out ushort v)
            {
                v = 0;
                if (Remaining < 2) return false;
                v = (ushort)(_buf[_start + _off] | (_buf[_start + _off + 1] << 8));
                _off += 2;
                return true;
            }

            public bool ReadU32(out uint v)
            {
                v = 0;
                if (Remaining < 4) return false;
                int p = _start + _off;
                v = (uint)(_buf[p] | (_buf[p + 1] << 8) | (_buf[p + 2] << 16) | (_buf[p + 3] << 24));
                _off += 4;
                return true;
            }

            public bool ReadF32(out float v)
            {
                v = 0;
                if (!ReadU32(out uint bits)) return false;
                v = BitConverter.UInt32BitsToSingle(bits);
                return true;
            }

            public bool ReadBytes(int n, out byte[] outBytes)
            {
                outBytes = Array.Empty<byte>();
                if (n < 0 || Remaining < n) return false;
                outBytes = new byte[n];
                Array.Copy(_buf, _start + _off, outBytes, 0, n);
                _off += n;
                return true;
            }

            /// <summary>Pascal string: [u8 len][len bytes][1 pad byte]. Empty == failure (binary semantics).</summary>
            public string ReadPascalStr()
            {
                if (!ReadU8(out byte len)) return string.Empty;
                if (Remaining < len + 1) return string.Empty;
                string s = System.Text.Encoding.Latin1.GetString(_buf, _start + _off, len);
                _off += len;
                _off += 1; // trailing pad byte
                return s;
            }

            public bool ReadVec3(out Vector3 v)
            {
                v = default;
                if (!ReadF32(out float x) || !ReadF32(out float y) || !ReadF32(out float z)) return false;
                v = new Vector3(x, y, z);
                return true;
            }

            public bool ReadVec2(out Vector2 v)
            {
                v = default;
                if (!ReadF32(out float x) || !ReadF32(out float y)) return false;
                v = new Vector2(x, y);
                return true;
            }

            public bool SkipF32(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    if (!ReadF32(out _)) return false;
                }
                return true;
            }
        }

        // ===========================================================================================
        // Built vertex + section/mesh/system records.
        // ===========================================================================================
        public struct UberVert
        {
            public Vector3 Position;
            public Vector3 Normal;
            public Vector2 Uv0;
            public Vector2 Uv1;
            public uint Diffuse;
            // Skinning (Deform / FatDeform only): 2-bone blend. boneA/boneB index the CMeshSystem
            // skeleton; weight is the explicit blend factor for the stored byte (the other bone's
            // weight is implied). Irrelevant to bind-pose rendering — positions are already posed.
            public byte BoneA;
            public byte BoneB;
            public float Weight;
        }

        public sealed class MeshSection
        {
            public string MaterialName = "";
            public uint Flags;
            public uint Id;
            public uint MeshId;
            public uint Lod;
            public VertexType Type = VertexType.None;
            public Vector3 BbMin, BbMax;
            public uint IndexCount;
            public ushort[] Indices = Array.Empty<ushort>();
            public uint VertexCount;
            public UberVert[] Verts = Array.Empty<UberVert>();
            public bool HasNormal, HasUv0, HasUv1, HasColor, HasSkin;

            // Topology bit — binary-derived from the shipped .ubr files (selftest flag/IndexCount
            // correlation): section flags are exactly 0x1 (triangle LIST) or 0x2 (triangle STRIP),
            // mutually exclusive. Every section whose IndexCount is NOT a multiple of 3 (provably a
            // strip) has flags==0x2; the flat list quads (water/terrain sheets) have flags==0x1.
            // (The uber_model.h C++ note guessed bit 0x4 for tristrip — that is unverified and does
            // NOT match the data; the real strip bit is 0x2.)
            public bool IsTriStrip => (Flags & 0x2) != 0;

            public bool Load(StreamReader r, UberModel data)
            {
                MaterialName = r.ReadPascalStr();
                if (MaterialName.Length == 0) return false;

                if (!r.ReadU32(out Flags)) return false;
                if (!r.ReadU32(out Id)) return false;
                if (!r.ReadU32(out MeshId)) return false;
                if (!r.ReadU32(out Lod)) return false;

                if (!r.ReadU32(out uint vtRaw)) return false;
                Type = (VertexType)vtRaw;

                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out IndexCount)) return false;

                // Index buffer lives in MeshData (not ModelData). ReadMeshData advances a dedicated
                // MeshData cursor; the ModelData stream reader is unaffected.
                if (!data.ReadMeshData((int)IndexCount * sizeof(ushort), out byte[] idxBytes))
                {
                    return false;
                }
                Indices = new ushort[IndexCount];
                for (int i = 0; i < IndexCount; i++)
                {
                    Indices[i] = (ushort)(idxBytes[i * 2] | (idxBytes[i * 2 + 1] << 8));
                }

                // Only known-supported types reserve cursors + build verts. Unsupported types still
                // read vertex_count (stream field) but refuse the load (cursor reservation would
                // corrupt the following section).
                switch (Type)
                {
                    case VertexType.Lit:
                    case VertexType.Unlit:
                    case VertexType.Thin:
                    case VertexType.Raw:
                    case VertexType.Fat:
                    case VertexType.Deform:
                    case VertexType.FatDeform:
                        if (!r.ReadU32(out VertexCount)) return false;
                        break;
                    case VertexType.None:
                    default:
                        data.RefuseReason = "vtype:unknown(" + vtRaw + ")";
                        return false;
                }

                uint lookupOff = data.GetLookupCursor(VertexCount);
                uint npv = U32PerVert(Type);
                uint u32Off = npv != 0 ? data.GetU32Cursor(VertexCount * npv) : 0;

                Verts = new UberVert[VertexCount];
                switch (Type)
                {
                    case VertexType.Lit:
                    {
                        HasColor = HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Diffuse = data.U32At(o++);
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Unlit:
                    {
                        HasNormal = HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Thin:
                    {
                        HasUv0 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Raw:
                    {
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                        }
                        break;
                    }
                    case VertexType.Fat:
                    {
                        // 3 u32: packed normal + uv0(index) + uv1(index). (asm: assembler 0x009dab80)
                        HasNormal = HasUv0 = HasUv1 = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            Verts[i].Uv1 = data.Vec2(data.U32At(o++));
                        }
                        break;
                    }
                    case VertexType.Deform:
                    {
                        // 3 u32: packed normal + uv0(index) + skin. (asm: assembler 0x009dd320)
                        HasNormal = HasUv0 = HasSkin = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            DecodeSkin(data.U32At(o++), ref Verts[i]);
                        }
                        break;
                    }
                    case VertexType.FatDeform:
                    {
                        // 4 u32: packed normal + uv0(index) + uv1(index) + skin. (asm: 0x009dc990)
                        HasNormal = HasUv0 = HasUv1 = HasSkin = true;
                        uint o = u32Off;
                        for (uint i = 0; i < VertexCount; i++)
                        {
                            Verts[i].Position = data.GetVec3Via(lookupOff + i);
                            Verts[i].Normal = DecodeNormal(data.U32At(o++));
                            Verts[i].Uv0 = data.Vec2(data.U32At(o++));
                            Verts[i].Uv1 = data.Vec2(data.U32At(o++));
                            DecodeSkin(data.U32At(o++), ref Verts[i]);
                        }
                        break;
                    }
                }

                return true;
            }
        }

        public sealed class Mesh
        {
            public string Name = "";
            public string ModelName = "";
            public uint Lod;
            public Vector3 BbMin, BbMax;
            public uint MeshSectionCount2;
            public uint Id;
            public uint MeshSectionCount;
            public readonly List<MeshSection> Sections = new();

            public bool LoadHeader(StreamReader r)
            {
                Name = r.ReadPascalStr();
                if (Name.Length == 0) return false;
                ModelName = r.ReadPascalStr();
                if (ModelName.Length == 0) return false;
                if (!r.ReadU32(out Lod)) return false;
                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out MeshSectionCount2)) return false;
                if (!r.ReadU32(out Id)) return false;
                if (!r.ReadU32(out MeshSectionCount)) return false;
                return true;
            }

            public bool LoadMeshSections(StreamReader r, UberModel data)
            {
                Sections.Clear();
                for (uint i = 0; i < MeshSectionCount; i++)
                {
                    var s = new MeshSection();
                    if (!s.Load(r, data)) return false;
                    Sections.Add(s);
                }
                return true;
            }
        }

        // ----- skeleton (retained for skinning/animation; gated by MESHSYS_FLAG_SKELETONS) ---------
        public sealed class Bone
        {
            public string Name = "";
            public int Parent = -1;
            public Vector3 Position;                          // bind local position (Z-up, parent-relative)
            public Quaternion Rotation = Quaternion.Identity; // bind local rotation
        }

        public sealed class Skeleton
        {
            public string Name = "";
            public readonly List<Bone> Bones = new();
        }

        public sealed class MeshSystem
        {
            public string Name = "";
            public uint Flags;
            public Vector3 BbMin, BbMax;
            public uint A, B;   // raw bits; A/B are IEEE-754 floats = the system's (X,Y) world placement
            public readonly List<Mesh> Meshes = new();
            public readonly List<Skeleton> Skeletons = new();

            /// <summary>World placement offset (X,Y) of this system's local geometry. Map tiles use this
            /// to grid out across the continent (multiples of 256); objects have it = 0.</summary>
            public Vector3 WorldOffset =>
                new Vector3(BitConverter.UInt32BitsToSingle(A), BitConverter.UInt32BitsToSingle(B), 0f);

            public bool Load(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out Flags)) return false;
                if (!r.ReadVec3(out BbMin)) return false;
                if (!r.ReadVec3(out BbMax)) return false;
                if (!r.ReadU32(out A)) return false;
                if (!r.ReadU32(out B)) return false;

                // Mesh array — phase 1: read every mesh header.
                if (!r.ReadU32(out uint meshCount)) return false;
                Meshes.Clear();
                for (uint i = 0; i < meshCount; i++)
                {
                    var m = new Mesh();
                    if (!m.LoadHeader(r)) return false;
                    Meshes.Add(m);
                }

                // Optional sub-systems — exact binary read order. These consume stream bytes that
                // sit BETWEEN the mesh headers and the mesh sections; skipping desyncs the reader,
                // so we parse (consume) them faithfully even though the editor doesn't use them.
                if ((Flags & FlagPortalSystem) != 0)
                {
                    if (!PortalSystem.Consume(r, data)) return false;
                }
                if ((Flags & FlagVec3Array) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    for (uint i = 0; i < n; i++)
                    {
                        if (!r.ReadU32(out uint m)) return false;
                        if (!r.SkipF32((int)m * 3)) return false;
                    }
                }
                if ((Flags & FlagUserData) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    if (!r.ReadBytes((int)n, out _)) return false;
                }
                if ((Flags & FlagSkeletons) != 0)
                {
                    if (!r.ReadU32(out uint n)) return false;
                    for (uint i = 0; i < n; i++)
                    {
                        Skeleton? sk = SkeletonReader.Read(r);
                        if (sk == null) return false;
                        Skeletons.Add(sk);
                    }
                }

                // Phase 2: each mesh reads its sections (after ALL headers + optional subs).
                for (int i = 0; i < Meshes.Count; i++)
                {
                    if (!Meshes[i].LoadMeshSections(r, data)) { data.RefuseReason ??= "sections"; return false; }
                }

                // AAB: always read unless NO_AAB. Collision: gated by COLLISION.
                if ((Flags & FlagNoAab) == 0)
                {
                    if (!Aab.Consume(r, data)) { data.RefuseReason ??= "aab"; return false; }
                }
                if ((Flags & FlagCollision) != 0)
                {
                    // Collision is the LAST step, read from this system's own ModelData slice and UNUSED
                    // by the editor. A `COLL_Mesh` (type 3) part carries a body the reference (and the
                    // binary) leave unconsumed, which desyncs the collision reader — but the renderable
                    // geometry (sections) is already fully parsed, so a collision desync is NON-FATAL.
                    if (!Collision.Consume(r, data))
                    {
                        data.RefuseReason = null; // not a refusal; the system's geometry is intact
                    }
                }
                return true;
            }
        }

        // ===========================================================================================
        // Gated subsystems — ported to CONSUME the stream in the exact binary read order. The editor
        // doesn't use their contents, so they parse-and-discard, but byte-for-byte to keep the cursor
        // aligned for the mesh sections that follow.
        // ===========================================================================================
        private static class Aab
        {
            public static bool Consume(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out uint faceCount)) return false;
                if (faceCount != 0)
                {
                    if (!data.ReadMeshData((int)faceCount * SizeAabFace, out _)) return false;
                }
                if (!r.ReadU32(out uint nodeCount)) return false;
                if (!data.ReadMeshData(SizeAabNode, out _)) return false; // root node, always read
                if (nodeCount != 0)
                {
                    if (!data.ReadMeshData((int)nodeCount * SizeAabNode, out _)) return false;
                }
                if (!r.ReadU32(out uint mapCount)) return false;
                if (mapCount != 0)
                {
                    if (!data.ReadMeshData((int)mapCount * 4, out _)) return false;
                }
                return true;
            }
        }

        private static class SkeletonReader
        {
            public static Skeleton? Read(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0) return null;
                var sk = new Skeleton { Name = name };
                if (!r.ReadU32(out uint nbones)) return null;
                for (uint i = 0; i < nbones; i++)
                {
                    Bone? b = ReadBone(r);
                    if (b == null) return null;
                    sk.Bones.Add(b);
                }
                if (!r.ReadU32(out uint n)) return null;
                if (n != 0)
                {
                    if (!r.ReadU32(out uint nx)) return null;
                    for (uint i = 0; i < nx; i++)
                    {
                        if (r.ReadPascalStr().Length == 0) return null; // external instance name
                        if (!r.ReadU32(out _)) return null;             // bone index
                    }
                }
                return sk;
            }

            private static Bone? ReadBone(StreamReader r)
            {
                string name = r.ReadPascalStr();
                if (name.Length == 0) return null;
                if (!r.ReadU32(out uint parent)) return null;
                // CBoneTransform: flags(u32) + position(vec3) + quat(W,X,Y,Z) + rotation matrix(9 f32).
                if (!r.ReadU32(out _)) return null;                          // flags
                if (!r.ReadVec3(out Vector3 pos)) return null;              // bind position
                // Bind quaternion is stored X,Y,Z,W (verified geometrically: each bone's composed
                // bind-world position lands on the centroid of the vertices skinned to it only with
                // this order — for both soldiers and rigid props. The earlier "W,X,Y,Z" reading was
                // wrong; it left bind rotations subtly off, which exploded vertex-skinned models on
                // animation while leaving small-rotation rigid props looking acceptable).
                if (!r.ReadF32(out float qx)) return null;                 // quat X
                if (!r.ReadF32(out float qy)) return null;                 // quat Y
                if (!r.ReadF32(out float qz)) return null;                 // quat Z
                if (!r.ReadF32(out float qw)) return null;                 // quat W
                if (!r.SkipF32(9)) return null;                            // 3x3 rotation matrix (derived; unused)
                return new Bone
                {
                    Name = name,
                    Parent = unchecked((int)parent),
                    Position = pos,
                    Rotation = new Quaternion(qx, qy, qz, qw), // System.Numerics is (X,Y,Z,W)
                };
            }
        }

        private static class Collision
        {
            public static bool Consume(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out uint n)) { data.RefuseReason = "collision:count"; return false; }
                if (n == 0) return true;
                if (r.ReadPascalStr().Length == 0) { data.RefuseReason = "collision:name"; return false; }
                if (!r.ReadU32(out uint nparts)) { data.RefuseReason = "collision:nparts"; return false; }
                for (uint i = 0; i < nparts; i++)
                {
                    // A COLL_Mesh (type 3) part leaves its body unconsumed (matching the reference),
                    // which desyncs subsequent parts. The caller treats this as non-fatal.
                    if (!ConsumePart(r, out uint t))
                    {
                        data.RefuseReason = $"collision:part{i}/{nparts}:type={t}";
                        return false;
                    }
                }
                return true;
            }

            private static bool ConsumePart(StreamReader r, out uint t)
            {
                t = 0xFFFFFFFF;
                if (r.ReadPascalStr().Length == 0) return false; // part name
                if (!r.ReadU32(out t)) return false;             // type
                if (!r.SkipF32(3)) return false;                 // bbox min
                if (!r.SkipF32(3)) return false;                 // bbox max
                switch ((int)t)
                {
                    case 0: return r.SkipF32(3) && r.SkipF32(1);              // Sphere: center + radius
                    case 1: return r.SkipF32(3) && r.SkipF32(3);              // Box: min + max
                    case 2: return r.SkipF32(3) && r.SkipF32(1) && r.SkipF32(1); // Cylinder: center + length + radius
                    case 6: return r.SkipF32(16) && r.SkipF32(3) && r.SkipF32(3) && r.SkipF32(1) && r.SkipF32(1); // OOBB
                    case 3: return true; // Mesh: body unconsumed (preserved binary quirk)
                    default: return true;
                }
            }
        }

        private static class PortalSystem
        {
            // Mirrors CPortalSystem::Load — consumes the full indoor portal/region graph.
            public static bool Consume(StreamReader r, UberModel data)
            {
                if (!r.ReadU32(out uint n)) return false;
                if (n == 0) return true; // "no portal system" early-out
                if (!r.SkipF32(3) || !r.SkipF32(3)) return false; // bbox
                for (int i = 0; i < 5; i++)
                {
                    if (!ConsumePortalMesh(r)) return false; // portal_mesh0..4
                }
                if (!r.ReadU32(out n)) return false;
                for (uint i = 0; i < n; i++)
                {
                    if (!ConsumePortal(r)) return false; // exterior portals
                }
                if (!r.ReadU32(out n)) return false;
                uint regionCount = n;
                for (uint i = 0; i < n; i++)
                {
                    if (!ConsumeRegion(r)) return false;
                }
                if (!r.ReadU32(out n)) return false;
                for (uint i = 0; i < n; i++)
                {
                    if (r.ReadPascalStr().Length == 0) return false; // strings
                }
                if (!r.ReadU32(out n)) return false;
                if (n != 0)
                {
                    for (uint i = 0; i < n; i++)
                    {
                        if (!ConsumeMeshItem(r)) return false;
                    }
                    if (!r.ReadU32(out n)) return false;
                    if (n != 0)
                    {
                        if (!ConsumeAabv(r)) return false;
                    }
                    for (uint i = 0; i < regionCount; i++)
                    {
                        if (!r.ReadU32(out _)) return false;          // portal id
                        if (!r.ReadU32(out uint hasAabv)) return false;
                        if (hasAabv != 0 && !ConsumeAabv(r)) return false;
                    }
                }
                if (!r.ReadU32(out n)) return false;
                uint u32Count = n;
                for (uint i = 0; i < n; i++)
                {
                    if (!r.ReadU32(out _)) return false;
                }
                if (u32Count != 0)
                {
                    if (!r.ReadU32(out n)) return false;
                    if (n != 0 && !ConsumeAabv(r)) return false;
                }
                // Trailing sub-structs (CPortalRegionSubStructure0 array). The binary has a preserved
                // brace-less-if quirk on failure; on success it simply consumes the array.
                if (!r.ReadU32(out uint nss)) return false;
                for (uint i = 0; i < nss; i++)
                {
                    if (!ConsumeRegionSubStruct0(r)) return false;
                }
                return true;
            }

            private static bool ConsumePortalMesh(StreamReader r)
            {
                if (r.ReadPascalStr().Length == 0) return false;
                return r.SkipF32(2); // a, b
            }

            private static bool ConsumePlane(StreamReader r) => r.SkipF32(3) && r.SkipF32(2); // normal + distance + e

            private static bool ConsumePortal(StreamReader r)
            {
                if (!r.ReadU32(out _)) return false;          // a
                if (!r.ReadU32(out _)) return false;          // region_a
                if (!r.ReadU32(out _)) return false;          // region_b
                if (!r.SkipF32(4 * 3)) return false;          // 4 vec3 points
                if (!ConsumePlane(r)) return false;
                return r.ReadU32(out _);                       // mesh_item_id
            }

            private static bool ConsumeConvexHull(StreamReader r)
            {
                if (!r.ReadU32(out _)) return false;          // a
                if (!r.SkipF32(3) || !r.SkipF32(3)) return false; // bbox
                if (!r.ReadU32(out uint nplanes)) return false;
                for (uint i = 0; i < nplanes; i++)
                {
                    if (!ConsumePlane(r)) return false;
                }
                if (!r.ReadU32(out uint nverts)) return false;
                return r.SkipF32((int)nverts * 3);
            }

            private static bool ConsumeRegionSubStruct0(StreamReader r)
            {
                if (!r.SkipF32(1)) return false;     // a
                return r.SkipF32(4 * 3);             // 4 vec3
            }

            private static bool ConsumeRegion(StreamReader r)
            {
                if (r.ReadPascalStr().Length == 0) return false;  // name
                if (!r.ReadU32(out _)) return false;              // id
                if (!r.SkipF32(3) || !r.SkipF32(3)) return false; // bbox
                if (!r.ReadU32(out uint nportals)) return false;
                for (uint i = 0; i < nportals; i++)
                {
                    if (!ConsumePortal(r)) return false;
                }
                if (!r.ReadU32(out uint nhulls)) return false;
                for (uint i = 0; i < nhulls; i++)
                {
                    if (!ConsumeConvexHull(r)) return false;
                }
                if (!r.ReadU32(out uint nu32)) return false;
                for (uint i = 0; i < nu32; i++)
                {
                    if (!r.ReadU32(out _)) return false;
                }
                if (!r.ReadU32(out uint nss)) return false;
                for (uint i = 0; i < nss; i++)
                {
                    if (!ConsumeRegionSubStruct0(r)) return false;
                }
                return true;
            }

            private static bool ConsumeMeshItem(StreamReader r)
            {
                for (int i = 0; i < 6; i++)
                {
                    if (!r.ReadU32(out _)) return false; // a, index, id, flags, region_a, region_b
                }
                if (r.ReadPascalStr().Length == 0) return false; // instance name
                if (r.ReadPascalStr().Length == 0) return false; // asset name
                if (!r.ReadU32(out uint nm)) return false;
                for (uint i = 0; i < nm; i++)
                {
                    if (!r.ReadU32(out _)) return false;
                }
                return r.SkipF32(16); // transform 4x4
            }

            private static bool ConsumeAabv(StreamReader r)
            {
                if (!r.ReadU32(out uint n)) return false;
                if (n == 0) return true;
                // Recursive node walk; `n` is the total node count (the binary pre-sizes a slab and
                // walks via a running index, but byte-consumption only needs the recursion).
                return ConsumeAabvNode(r);
            }

            private static bool ConsumeAabvNode(StreamReader r)
            {
                if (!r.ReadU8(out byte flags)) return false;
                if (!r.SkipF32(3) || !r.SkipF32(3)) return false; // bbox
                if ((flags & 0x01) != 0)
                {
                    if (!r.ReadU16(out ushort nleaf)) return false;
                    for (uint i = 0; i < nleaf; i++)
                    {
                        if (!r.ReadU16(out _)) return false;
                    }
                }
                if ((flags & 0x10) != 0)
                {
                    if (!ConsumeAabvNode(r)) return false;
                }
                if ((flags & 0x20) != 0)
                {
                    if (!ConsumeAabvNode(r)) return false;
                }
                return true;
            }
        }

        // ===========================================================================================
        // UberModel container.
        // ===========================================================================================
        private bool _opened;
        private UberHeader _header = new();
        private readonly List<UberRecord> _records = new();

        private Vector3[] _vec3 = Array.Empty<Vector3>();
        private Vector2[] _vec2 = Array.Empty<Vector2>();
        private uint[] _lookup = Array.Empty<uint>();
        private uint[] _u32 = Array.Empty<uint>();
        private byte[] _meshData = Array.Empty<byte>();
        private byte[] _modelData = Array.Empty<byte>();

        // Per-FetchMeshSystem cursors.
        private int _meshDataCur;
        private int _meshDataEnd;
        private uint _lookupOffset;
        private uint _u32Offset;

        public bool Opened => _opened;
        public UberHeader Header => _header;
        public IReadOnlyList<UberRecord> Records => _records;

        /// <summary>Diagnostic: reason the most recent <see cref="FetchMeshSystemAt"/> refused (or null).</summary>
        public string? RefuseReason { get; internal set; }

        public static UberModel Load(byte[] data)
        {
            var m = new UberModel();
            if (!m.Open(data))
            {
                throw new InvalidOperationException("UberModel.Open failed (bad header / size invariant)");
            }
            return m;
        }

        public bool Open(byte[] data)
        {
            _opened = false;
            _records.Clear();
            _meshDataCur = _meshDataEnd = 0;
            _lookupOffset = _u32Offset = 0;

            // Header + size invariant (reuses the UberMesh parse via a throwaway instance).
            UberMesh um;
            try
            {
                um = UberMesh.Load(data);
            }
            catch
            {
                return false;
            }

            _header = new UberHeader
            {
                Version1 = um.Header.Version1,
                Version2 = um.Header.Version2,
                RecordCount = um.Header.RecordCount,
                VertexCount = um.Header.VertexCount,
                UvCount = um.Header.UvCount,
                IndexCount = um.Header.IndexCount,
                ConnCount = um.Header.ConnCount,
                BlobBytes = um.Header.BlobBytes,
                PoolBytes = um.Header.PoolBytes,
                SectionOffset = um.Header.SectionOffset,
            };

            if (_header.Version1 != 1 || _header.Version2 != 1) return false;

            uint computedSection =
                  (uint)UberMesh.HeaderSize
                + _header.RecordCount * (uint)UberMesh.RecordSize
                + _header.VertexCount * 0x0C
                + _header.UvCount * 0x08
                + _header.IndexCount * 0x04
                + _header.BlobBytes;
            if (_header.SectionOffset != computedSection) return false;

            uint computedSize = _header.SectionOffset + _header.ConnCount * 0x04 + _header.PoolBytes;
            if (computedSize != (uint)data.Length) return false;

            foreach (UberRecord rec in um.Records)
            {
                _records.Add(rec);
            }

            // Body sections — content-formats §2.2 layout:
            //   verts[vertex_count]  × 0x0C   -> Vec3Data
            //   uvs[uv_count]        × 0x08   -> Vec2Data
            //   indices[index_count] × 0x04   -> LookupData (binary's Lookup is a 32-bit corner stream)
            //   blob[blob_bytes]              -> MeshData
            //   conn[conn_count]     × 0x04   -> U32Data
            //   pool[pool_bytes]              -> ModelData (per-mesh-system command stream)
            int p = UberMesh.HeaderSize + (int)_header.RecordCount * UberMesh.RecordSize;

            _vec3 = new Vector3[_header.VertexCount];
            for (int i = 0; i < _header.VertexCount; i++)
            {
                _vec3[i] = new Vector3(ReadF(data, p), ReadF(data, p + 4), ReadF(data, p + 8));
                p += 0x0C;
            }
            _vec2 = new Vector2[_header.UvCount];
            for (int i = 0; i < _header.UvCount; i++)
            {
                _vec2[i] = new Vector2(ReadF(data, p), ReadF(data, p + 4));
                p += 0x08;
            }
            _lookup = new uint[_header.IndexCount];
            for (int i = 0; i < _header.IndexCount; i++)
            {
                _lookup[i] = ReadU(data, p);
                p += 4;
            }
            _meshData = new byte[_header.BlobBytes];
            if (_header.BlobBytes > 0)
            {
                Array.Copy(data, p, _meshData, 0, (int)_header.BlobBytes);
                p += (int)_header.BlobBytes;
            }
            _u32 = new uint[_header.ConnCount];
            for (int i = 0; i < _header.ConnCount; i++)
            {
                _u32[i] = ReadU(data, p);
                p += 4;
            }
            _modelData = new byte[_header.PoolBytes];
            if (_header.PoolBytes > 0)
            {
                Array.Copy(data, p, _modelData, 0, (int)_header.PoolBytes);
            }

            _opened = true;
            return true;
        }

        /// <summary>Locate a CMeshSystem by record name and decode it. Returns null on miss/refuse.</summary>
        public MeshSystem? FetchMeshSystem(string name)
        {
            if (!_opened || name == null) return null;

            int found = -1;
            for (int i = 0; i < _records.Count; i++)
            {
                if (_records[i].Name == name) { found = i; break; }
            }
            if (found < 0) return null;
            return FetchMeshSystemAt(found);
        }

        /// <summary>Decode the CMeshSystem at record index. Returns null on refuse/overrun.</summary>
        public MeshSystem? FetchMeshSystemAt(int index)
        {
            if (!_opened || index < 0 || index >= _records.Count) return null;
            RefuseReason = null;

            UberRecord e0 = _records[index];
            uint nextModel;
            if (index + 1 < _records.Count)
            {
                nextModel = _records[index + 1].FirstVertex; // record +0x4C = ModelData/pool offset
            }
            else
            {
                nextModel = (uint)_modelData.Length;
            }

            uint modelOff = e0.FirstVertex;
            uint modelEnd = nextModel;
            if (modelOff > _modelData.Length || modelEnd > _modelData.Length || modelOff > modelEnd)
            {
                return null;
            }

            // Per-section cursors for this fetch.
            _meshDataCur = (int)e0.FirstBlobByte;
            _meshDataEnd = index + 1 < _records.Count ? (int)_records[index + 1].FirstBlobByte : (int)_header.BlobBytes;
            _lookupOffset = e0.FirstIndex;
            _u32Offset = e0.FirstConn;

            var r = new StreamReader(_modelData, (int)modelOff, (int)(modelEnd - modelOff));
            var sys = new MeshSystem { Name = e0.Name };
            if (sys.Load(r, this))
            {
                return sys;
            }
            RefuseReason ??= "decode-failed(flags=0x" + sys.Flags.ToString("X") + ")";
            return null;
        }

        // ----- cursor + reservation helpers (binary: data.ReadMeshData / GetLookup / GetU32 / GetVec3) -----
        internal bool ReadMeshData(int nbytes, out byte[] outBytes)
        {
            outBytes = Array.Empty<byte>();
            if (nbytes < 0) return false;
            if (_meshDataCur + nbytes > _meshDataEnd) return false;
            if (_meshDataCur + nbytes > _meshData.Length) return false;
            outBytes = new byte[nbytes];
            Array.Copy(_meshData, _meshDataCur, outBytes, 0, nbytes);
            _meshDataCur += nbytes;
            return true;
        }

        internal uint GetLookupCursor(uint vertexCount)
        {
            uint ret = _lookupOffset;
            _lookupOffset += vertexCount;
            return ret;
        }

        internal uint GetU32Cursor(uint nu32)
        {
            if (nu32 == 0) return 0;
            uint ret = _u32Offset;
            _u32Offset += nu32;
            return ret;
        }

        internal uint U32At(uint i) => i < _u32.Length ? _u32[i] : 0u;

        internal Vector2 Vec2(uint i) => i < _vec2.Length ? _vec2[i] : default;

        /// <summary>position = Vec3Data[ LookupData[lookupIndex] ]; clamps to zero on overflow.</summary>
        internal Vector3 GetVec3Via(uint lookupIndex)
        {
            if (lookupIndex >= _lookup.Length) return default;
            uint vi = _lookup[lookupIndex];
            if (vi >= _vec3.Length) return default;
            return _vec3[vi];
        }

        // ----- static helpers ----------------------------------------------------------------------
        private static uint U32PerVert(VertexType t) => t switch
        {
            VertexType.Thin => 1,
            VertexType.Lit => 2,
            VertexType.Unlit => 2,
            VertexType.Fat => 3,
            VertexType.Deform => 3,
            VertexType.FatDeform => 4,
            _ => 0, // Raw + unsupported
        };

        // Skin u32 (asm: skin decoder 0x009dcba0): boneA = low byte, boneB = next byte, weight =
        // signed int16 in the high half scaled by 1/16384 (2^-14). 2-bone blend. Bind-pose rendering
        // ignores this; stored for completeness / future skinning.
        private static void DecodeSkin(uint packed, ref UberVert v)
        {
            v.BoneA = (byte)(packed & 0xFF);
            v.BoneB = (byte)((packed >> 8) & 0xFF);
            v.Weight = (short)(packed >> 16) / 16384.0f;
        }

        private static Vector3 DecodeNormal(uint packed)
        {
            // x = bits 20..29 (signed arithmetic shift, no mask — binary fidelity), y = 10..19,
            // z = 0..9; each biased by -0x200 and scaled by 1/512.
            int ix = ((int)packed >> 20) - 0x200;
            int iy = (int)((packed >> 10) & 0x3FF) - 0x200;
            int iz = (int)(packed & 0x3FF) - 0x200;
            return new Vector3(ix / 512.0f, iy / 512.0f, iz / 512.0f);
        }

        private static float ReadF(byte[] d, int p) =>
            BitConverter.UInt32BitsToSingle(ReadU(d, p));

        private static uint ReadU(byte[] d, int p) =>
            (uint)(d[p] | (d[p + 1] << 8) | (d[p + 2] << 16) | (d[p + 3] << 24));
    }
}
