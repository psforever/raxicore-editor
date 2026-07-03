using System;
using System.Buffers.Binary;
using System.Text;

namespace RaxicoreEditor.EngineAssets.IO
{
    /// <summary>
    /// Bounds-checked little-endian reader over a byte buffer. The engine-derived format stores everything
    /// little-endian (x86). Reads past the end throw <see cref="System.IO.EndOfStreamException"/>.
    /// </summary>
    public sealed class ByteReader
    {
        private readonly byte[] _data;
        private int _pos;

        public ByteReader(byte[] data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        public int Position
        {
            get => _pos;
            set
            {
                if (value < 0 || value > _data.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _pos = value;
            }
        }

        public int Length => _data.Length;
        public int Remaining => _data.Length - _pos;
        public bool EndOfData => _pos >= _data.Length;
        public byte[] Buffer => _data;

        public void Skip(int count) => Position = _pos + count;

        public byte ReadByte()
        {
            Require(1);
            return _data[_pos++];
        }

        public ushort ReadUInt16()
        {
            Require(2);
            ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(_pos, 2));
            _pos += 2;
            return v;
        }

        public uint ReadUInt32()
        {
            Require(4);
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public int ReadInt32() => unchecked((int)ReadUInt32());

        public ulong ReadUInt64()
        {
            Require(8);
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(_pos, 8));
            _pos += 8;
            return v;
        }

        public float ReadSingle()
        {
            Require(4);
            float v = BinaryPrimitives.ReadSingleLittleEndian(_data.AsSpan(_pos, 4));
            _pos += 4;
            return v;
        }

        public byte PeekByte(int offset = 0)
        {
            int p = _pos + offset;
            if (p < 0 || p >= _data.Length)
            {
                throw new System.IO.EndOfStreamException("ByteReader.PeekByte past end");
            }
            return _data[p];
        }

        public byte[] ReadBytes(int count)
        {
            Require(count);
            var result = new byte[count];
            Array.Copy(_data, _pos, result, 0, count);
            _pos += count;
            return result;
        }

        public ReadOnlySpan<byte> ReadSpan(int count)
        {
            Require(count);
            ReadOnlySpan<byte> span = _data.AsSpan(_pos, count);
            _pos += count;
            return span;
        }

        /// <summary>Fixed-length field, trimmed at the first NUL.</summary>
        public string ReadFixedString(int length)
        {
            Require(length);
            int n = 0;
            while (n < length && _data[_pos + n] != 0)
            {
                n++;
            }
            string s = Encoding.Latin1.GetString(_data, _pos, n);
            _pos += length;
            return s;
        }

        /// <summary>NUL-terminated string (consumes the terminator).</summary>
        public string ReadCString()
        {
            int start = _pos;
            while (_pos < _data.Length && _data[_pos] != 0)
            {
                _pos++;
            }
            string s = Encoding.Latin1.GetString(_data, start, _pos - start);
            if (_pos < _data.Length)
            {
                _pos++; // skip NUL
            }
            return s;
        }

        private void Require(int count)
        {
            if (count < 0 || _pos + count > _data.Length)
            {
                throw new System.IO.EndOfStreamException(
                    $"ByteReader: read of {count} at {_pos} exceeds length {_data.Length}");
            }
        }
    }
}
