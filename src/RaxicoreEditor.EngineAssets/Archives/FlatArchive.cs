using System;
using System.Collections.Generic;
using System.Text;
using RaxicoreEditor.EngineAssets.IO;

namespace RaxicoreEditor.EngineAssets.Archives
{
    public sealed class FlatEntry
    {
        public string Name { get; init; } = "";
        public uint DataOffset { get; init; } // absolute byte offset of the payload inside the .fat
        public uint DataLen { get; init; }
    }

    /// <summary>
    /// FLAT flat-file archive (.fat data store + .fdx index), ported from the engine-derived
    /// reference implementation's <c>asset_archive.h</c>. The .fat is self-describing
    /// (names + lengths), so it is parsed directly; the writer emits a matching .fat + .fdx pair.
    /// Payloads are stored raw (typically pre-converted DDS textures).
    /// </summary>
    public sealed class FlatArchive
    {
        private byte[] _fat = Array.Empty<byte>();
        private readonly List<FlatEntry> _entries = new();

        public uint Version { get; private set; }
        public IReadOnlyList<FlatEntry> Entries => _entries;

        public static bool HasMagic(ReadOnlySpan<byte> b) =>
            b.Length >= 4 && b[0] == 'F' && b[1] == 'L' && b[2] == 'A' && b[3] == 'T';

        public static FlatArchive Load(byte[] fat)
        {
            var a = new FlatArchive { _fat = fat };
            a.Open();
            return a;
        }

        private void Open()
        {
            if (!HasMagic(_fat))
            {
                throw new InvalidOperationException("Not a FLAT archive");
            }
            var r = new ByteReader(_fat);
            r.Skip(4); // "FLAT"
            Version = r.ReadUInt32();
            r.ReadUInt32();                  // reserved (0)
            uint entryCount = r.ReadUInt32();
            r.ReadUInt32();                  // totalFileSize

            for (uint i = 0; i < entryCount; i++)
            {
                uint nameLen = r.ReadUInt32();
                string name = Encoding.Latin1.GetString(r.ReadSpan((int)nameLen));
                r.ReadByte();                // NUL
                uint dataLen = r.ReadUInt32();
                uint dataOffset = (uint)r.Position;
                r.Skip((int)dataLen);
                _entries.Add(new FlatEntry { Name = name, DataOffset = dataOffset, DataLen = dataLen });
            }
        }

        public int IndexOf(string name)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                if (string.Equals(_entries[i].Name, name, StringComparison.Ordinal)) return i;
            }
            return -1;
        }

        public byte[] Extract(int index)
        {
            FlatEntry e = _entries[index];
            var result = new byte[e.DataLen];
            Array.Copy(_fat, e.DataOffset, result, 0, e.DataLen);
            return result;
        }

        public byte[] Extract(string name)
        {
            int i = IndexOf(name);
            if (i < 0) throw new KeyNotFoundException("FLAT entry not found: " + name);
            return Extract(i);
        }

        // ---- repack ----------------------------------------------------------------------------

        public sealed class InputFile
        {
            public string Name { get; init; } = "";
            public byte[] Data { get; init; } = Array.Empty<byte>();
        }

        public sealed class Built
        {
            public byte[] Fat { get; init; } = Array.Empty<byte>();
            public byte[] Fdx { get; init; } = Array.Empty<byte>();
        }

        public static Built Build(IReadOnlyList<InputFile> files, uint version = 1)
        {
            var fat = new ByteWriter(1024);
            fat.WriteAscii("FLAT");
            fat.WriteUInt32(version);
            fat.WriteUInt32(0);                          // reserved
            fat.WriteUInt32((uint)files.Count);
            int sizeFieldPos = fat.Reserve(4);          // totalFileSize (back-patched)

            var offsets = new uint[files.Count];
            var lens = new uint[files.Count];
            for (int i = 0; i < files.Count; i++)
            {
                fat.WriteUInt32((uint)files[i].Name.Length);
                fat.WriteAscii(files[i].Name);
                fat.WriteByte(0);                       // NUL
                fat.WriteUInt32((uint)files[i].Data.Length);
                offsets[i] = (uint)fat.Length;
                lens[i] = (uint)files[i].Data.Length;
                fat.WriteBytes(files[i].Data);
            }
            fat.PatchUInt32(sizeFieldPos, (uint)fat.Length);

            var fdx = new ByteWriter(256);
            fdx.WriteUInt32((uint)files.Count);
            for (int i = 0; i < files.Count; i++)
            {
                fdx.WriteUInt32((uint)files[i].Name.Length);
                fdx.WriteAscii(files[i].Name);
                fdx.WriteByte(0);
                fdx.WriteUInt32(offsets[i]);
                fdx.WriteUInt32(lens[i]);
            }

            return new Built { Fat = fat.ToArray(), Fdx = fdx.ToArray() };
        }
    }
}
