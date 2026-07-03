using System;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Surfaces
{
    /// <summary>
    /// '.srf' surface tile (inside mapNN_srf.pak), ported from the engine-derived reference
    /// implementation's <c>surface_tile.h</c>. A full tile is an 8-byte header + a
    /// 128x128 grid of 4-byte cells {type, flags, u16 blend} = 65,544 bytes. Operates on the
    /// decompressed payload (caller pulls via PakArchive.Extract).
    /// </summary>
    public sealed class SurfaceTile
    {
        public const int HeaderSize = 8;
        public const int GridDim = 128;
        public const int CellBytes = 4;
        public const int GridBytes = GridDim * GridDim * CellBytes;
        public const int TileBytes = HeaderSize + GridBytes; // 0x10008

        private readonly byte[] _data;

        public bool IsFull { get; }
        public byte[] Header { get; } = new byte[HeaderSize];

        public readonly record struct Cell(byte Type, byte Flags, ushort Blend);

        private SurfaceTile(byte[] data, bool full)
        {
            _data = data;
            IsFull = full;
            Array.Copy(data, Header, Math.Min(HeaderSize, data.Length));
        }

        public static bool IsFullTile(int len) => len == TileBytes;

        public static SurfaceTile Parse(byte[] data) => new(data, data.Length >= TileBytes);

        public Cell GetCell(int row, int col)
        {
            if (!IsFull || (uint)row >= GridDim || (uint)col >= GridDim)
            {
                return default;
            }
            int o = HeaderSize + (row * GridDim + col) * CellBytes;
            return new Cell(_data[o], _data[o + 1], (ushort)(_data[o + 2] | (_data[o + 3] << 8)));
        }

        /// <summary>Overwrite a cell in place. The .srf is fixed-layout, so this directly re-encodes.</summary>
        public void SetCell(int row, int col, Cell c)
        {
            if (!IsFull || (uint)row >= GridDim || (uint)col >= GridDim)
            {
                return;
            }
            int o = HeaderSize + (row * GridDim + col) * CellBytes;
            _data[o] = c.Type;
            _data[o + 1] = c.Flags;
            _data[o + 2] = (byte)(c.Blend & 0xFF);
            _data[o + 3] = (byte)(c.Blend >> 8);
        }

        /// <summary>The (possibly edited) raw bytes — a valid .srf, byte-for-byte round-trippable.</summary>
        public byte[] ToBytes() => _data;

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("surface tile: ").Append(IsFull ? "full 128x128" : $"short/sparse ({_data.Length} bytes)").Append('\n');
            sb.Append("header:");
            foreach (byte b in Header)
            {
                sb.Append(' ').Append(b.ToString("x2"));
            }
            sb.Append("\n\n");

            if (IsFull)
            {
                var hist = new int[256];
                for (int row = 0; row < GridDim; row++)
                {
                    for (int col = 0; col < GridDim; col++)
                    {
                        hist[GetCell(row, col).Type]++;
                    }
                }
                sb.Append("surface-type usage (type: cells):\n");
                for (int t = 0; t < 256; t++)
                {
                    if (hist[t] > 0)
                    {
                        sb.Append("  ").Append(t).Append(": ").Append(hist[t]).Append('\n');
                    }
                }
            }
            return sb.ToString();
        }
    }
}
