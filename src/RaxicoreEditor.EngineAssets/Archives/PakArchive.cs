using System;
using System.Collections.Generic;
using System.Text;
using RaxicoreEditor.EngineAssets.Compression;
using RaxicoreEditor.EngineAssets.IO;

namespace RaxicoreEditor.EngineAssets.Archives
{
    /// <summary>One directory entry in a PACK archive.</summary>
    public sealed class PakEntry
    {
        public string Name { get; init; } = "";
        public uint Offset { get; init; }            // relative to the payload region start
        public uint StoredSize { get; init; }        // on-disk record size (12-byte frame + payload)
        public uint UncompressedSize { get; init; }
        public uint Hash { get; init; }              // per-entry hash (algorithm unidentified)
    }

    /// <summary>
    /// Engine-derived PACK container (magic "PACK", v2), ported from the reference
    /// implementation's <c>pak_archive.cpp</c>. 28-byte header, then a directory record
    /// at 0x1C listing every entry, then the payload records (each a 12-byte frame
    /// {u32 uncompressedSize, u32 crc, "LZO1"} + LZO1X data). The repack writer emits store-mode
    /// LZO1X payloads and the matching PakCrc per record.
    /// </summary>
    public sealed class PakArchive
    {
        public const int HeaderSize = 0x1C;
        private static readonly byte[] Tag = { (byte)'L', (byte)'Z', (byte)'O', (byte)'1' };

        private byte[] _image = Array.Empty<byte>();
        private readonly List<PakEntry> _entries = new();

        public uint Version { get; private set; }
        public uint Field08 { get; private set; }
        public uint DirUncompressedSize { get; private set; }
        public uint HeaderHash0 { get; private set; }
        public uint HeaderHash1 { get; private set; }
        public IReadOnlyList<PakEntry> Entries => _entries;
        public uint PayloadRegionStart => (uint)HeaderSize + Field08;

        public static bool HasMagic(ReadOnlySpan<byte> b) =>
            b.Length >= 4 && b[0] == 'P' && b[1] == 'A' && b[2] == 'C' && b[3] == 'K';

        public static PakArchive Load(byte[] image)
        {
            var a = new PakArchive { _image = image };
            a.Open();
            return a;
        }

        private void Open()
        {
            if (_image.Length < HeaderSize || !HasMagic(_image))
            {
                throw new InvalidOperationException("Not a PACK archive");
            }

            var r = new ByteReader(_image);
            r.Skip(4); // "PACK"
            Version = r.ReadUInt32();
            Field08 = r.ReadUInt32();
            DirUncompressedSize = r.ReadUInt32();
            uint fileCount = r.ReadUInt32();
            HeaderHash0 = r.ReadUInt32();
            HeaderHash1 = r.ReadUInt32();

            if (DirUncompressedSize == 0)
            {
                throw new InvalidOperationException("PACK directory size is zero");
            }

            // The first record (at 0x1C) is the directory itself; its on-disk size is Field08.
            byte[] dir = DecompressRecord((uint)HeaderSize, DirUncompressedSize, Field08);
            ParseDirectory(dir, fileCount);
        }

        private void ParseDirectory(byte[] dir, uint fileCount)
        {
            _entries.Clear();
            int p = 0;
            for (uint i = 0; i < fileCount; i++)
            {
                int nameStart = p;
                while (p < dir.Length && dir[p] != 0) p++;
                if (p >= dir.Length) throw new InvalidOperationException("PACK directory: unterminated name");
                string name = Encoding.Latin1.GetString(dir, nameStart, p - nameStart);
                p++; // NUL
                if (p + 24 > dir.Length) throw new InvalidOperationException("PACK directory: truncated entry");

                // {reserved0, offset, reserved2, storedSize, uncompressedSize, hash}
                uint offset = ReadU32(dir, p + 4);
                uint storedSize = ReadU32(dir, p + 12);
                uint uncompressedSize = ReadU32(dir, p + 16);
                uint hash = ReadU32(dir, p + 20);
                p += 24;

                _entries.Add(new PakEntry
                {
                    Name = name,
                    Offset = offset,
                    StoredSize = storedSize,
                    UncompressedSize = uncompressedSize,
                    Hash = hash,
                });
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
            if (index < 0 || index >= _entries.Count) throw new ArgumentOutOfRangeException(nameof(index));
            PakEntry e = _entries[index];
            uint abs = PayloadRegionStart + e.Offset;
            return DecompressRecord(abs, e.UncompressedSize & 0x7FFFFFFFu, e.StoredSize);
        }

        public byte[] Extract(string name)
        {
            int i = IndexOf(name);
            if (i < 0) throw new KeyNotFoundException("PACK entry not found: " + name);
            return Extract(i);
        }

        /// <summary>Decompress (or copy, for the "stored" bit-31 case) the record framed at <paramref name="offset"/>.</summary>
        private byte[] DecompressRecord(uint offset, uint expectedSize, uint storedSize)
        {
            if (offset + 12 > _image.Length) throw new InvalidOperationException("PACK record out of range");
            uint frameUncompressed = ReadU32(_image, (int)offset);
            // tag at offset+8
            if (_image[offset + 8] != 'L' || _image[offset + 9] != 'Z' ||
                _image[offset + 10] != 'O' || _image[offset + 11] != '1')
            {
                throw new InvalidOperationException("PACK record missing LZO1 tag");
            }

            int dataOff = (int)offset + 12;

            if ((frameUncompressed & 0x80000000u) != 0)
            {
                int storedLen = (int)(frameUncompressed & 0x7FFFFFFFu);
                var stored = new byte[storedLen];
                Array.Copy(_image, dataOff, stored, 0, storedLen);
                return stored;
            }

            int inLen = storedSize >= 12 ? (int)(storedSize - 12) : (_image.Length - dataOff);
            var output = new byte[expectedSize];
            LzoResult rc = Lzo1x.Decompress(
                new ReadOnlySpan<byte>(_image, dataOff, inLen), output, out int produced);
            // Padded records legitimately leave bytes unconsumed; the size check is authoritative.
            if (rc == LzoResult.InputOverrun || produced != expectedSize)
            {
                throw new InvalidOperationException(
                    $"PACK record decode failed (rc={rc}, produced={produced}, expected={expectedSize})");
            }
            return output;
        }

        // ---- repack ----------------------------------------------------------------------------

        public sealed class InputFile
        {
            public string Name { get; init; } = "";
            public byte[] Data { get; init; } = Array.Empty<byte>();
            public uint Hash { get; init; }
        }

        /// <summary>Build a PACK v2 image from (name, bytes) files using store-mode LZO1X payloads.</summary>
        public static byte[] Build(IReadOnlyList<InputFile> files, uint version = 2,
                                   uint headerHash0 = 0, uint headerHash1 = 0)
        {
            var records = new List<byte[]>(files.Count);
            var offsets = new uint[files.Count];
            uint running = 0;

            for (int i = 0; i < files.Count; i++)
            {
                byte[] comp = Lzo1x.StoreCompress(files[i].Data);
                var w = new ByteWriter(comp.Length + 12);
                w.WriteUInt32((uint)files[i].Data.Length);
                w.WriteUInt32(AssetCrc.PakCrc(comp));
                w.WriteBytes(Tag);
                w.WriteBytes(comp);
                byte[] rec = w.ToArray();
                offsets[i] = running;
                running += (uint)rec.Length;
                records.Add(rec);
            }

            // Directory blob.
            var dir = new ByteWriter(256);
            for (int i = 0; i < files.Count; i++)
            {
                dir.WriteCString(files[i].Name);
                dir.WriteUInt32(0);                                  // reserved0
                dir.WriteUInt32(offsets[i]);                         // offset
                dir.WriteUInt32(0);                                  // reserved2
                dir.WriteUInt32((uint)records[i].Length);           // storedSize
                dir.WriteUInt32((uint)files[i].Data.Length);        // uncompressedSize
                dir.WriteUInt32(files[i].Hash);                     // hash
            }
            byte[] dirBlob = dir.ToArray();

            byte[] dirComp = Lzo1x.StoreCompress(dirBlob);
            var dirRec = new ByteWriter(dirComp.Length + 12);
            dirRec.WriteUInt32((uint)dirBlob.Length);
            dirRec.WriteUInt32(AssetCrc.PakCrc(dirComp));
            dirRec.WriteBytes(Tag);
            dirRec.WriteBytes(dirComp);
            byte[] dirRecord = dirRec.ToArray();
            uint field08 = (uint)dirRecord.Length;

            var outw = new ByteWriter(HeaderSize + dirRecord.Length + (int)running);
            outw.WriteAscii("PACK");
            outw.WriteUInt32(version);
            outw.WriteUInt32(field08);
            outw.WriteUInt32((uint)dirBlob.Length);
            outw.WriteUInt32((uint)files.Count);
            outw.WriteUInt32(headerHash0);
            outw.WriteUInt32(headerHash1);
            outw.WriteBytes(dirRecord);
            foreach (byte[] rec in records)
            {
                outw.WriteBytes(rec);
            }
            return outw.ToArray();
        }

        public byte[] RebuildWithReplacement(int index, byte[] newData)
        {
            var files = new List<InputFile>(_entries.Count);
            for (int i = 0; i < _entries.Count; i++)
            {
                files.Add(new InputFile
                {
                    Name = _entries[i].Name,
                    Hash = _entries[i].Hash,
                    Data = i == index ? newData : Extract(i),
                });
            }
            return Build(files, Version == 0 ? 2 : Version, HeaderHash0, HeaderHash1);
        }

        private static uint ReadU32(byte[] b, int o) =>
            (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
    }
}
