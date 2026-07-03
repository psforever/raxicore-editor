using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace RaxicoreEditor.EngineAssets.IO
{
    /// <summary>
    /// Growable little-endian writer. Used by the repack/re-export paths. Supports back-patching
    /// earlier 4-byte fields (for sizes/offsets resolved after the fact).
    /// </summary>
    public sealed class ByteWriter
    {
        private byte[] _buffer;
        private int _length;

        public ByteWriter(int capacity = 256)
        {
            _buffer = new byte[Math.Max(16, capacity)];
            _length = 0;
        }

        public int Length => _length;

        public void WriteByte(byte v)
        {
            EnsureCapacity(_length + 1);
            _buffer[_length++] = v;
        }

        public void WriteUInt16(ushort v)
        {
            EnsureCapacity(_length + 2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length, 2), v);
            _length += 2;
        }

        public void WriteUInt32(uint v)
        {
            EnsureCapacity(_length + 4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length, 4), v);
            _length += 4;
        }

        public void WriteInt32(int v) => WriteUInt32(unchecked((uint)v));

        public void WriteSingle(float v)
        {
            EnsureCapacity(_length + 4);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length, 4), v);
            _length += 4;
        }

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            EnsureCapacity(_length + bytes.Length);
            bytes.CopyTo(_buffer.AsSpan(_length));
            _length += bytes.Length;
        }

        public void WriteAscii(string s)
        {
            int n = Encoding.Latin1.GetByteCount(s);
            EnsureCapacity(_length + n);
            Encoding.Latin1.GetBytes(s, _buffer.AsSpan(_length, n));
            _length += n;
        }

        /// <summary>Writes the string bytes followed by a NUL terminator.</summary>
        public void WriteCString(string s)
        {
            WriteAscii(s);
            WriteByte(0);
        }

        /// <summary>Writes the string into a fixed-length, NUL-padded field.</summary>
        public void WriteFixedString(string s, int length)
        {
            byte[] raw = Encoding.Latin1.GetBytes(s);
            int n = Math.Min(raw.Length, length);
            EnsureCapacity(_length + length);
            Array.Copy(raw, 0, _buffer, _length, n);
            for (int i = n; i < length; i++)
            {
                _buffer[_length + i] = 0;
            }
            _length += length;
        }

        public int Reserve(int count)
        {
            int at = _length;
            EnsureCapacity(_length + count);
            Array.Clear(_buffer, _length, count);
            _length += count;
            return at;
        }

        public void PatchUInt32(int offset, uint v)
        {
            if (offset < 0 || offset + 4 > _length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(offset, 4), v);
        }

        public byte[] ToArray()
        {
            var result = new byte[_length];
            Array.Copy(_buffer, result, _length);
            return result;
        }

        public void WriteTo(Stream stream) => stream.Write(_buffer, 0, _length);

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
            {
                return;
            }
            int newCap = _buffer.Length * 2;
            while (newCap < required)
            {
                newCap *= 2;
            }
            Array.Resize(ref _buffer, newCap);
        }
    }
}
