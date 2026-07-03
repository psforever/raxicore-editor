using System;
using System.Text;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>Read-only hex view over a raw byte payload. Export returns the original bytes.</summary>
    public sealed class HexDocument : DocumentBase
    {
        private const int PreviewLimit = 256 * 1024; // cap the rendered dump

        private readonly byte[] _data;

        public HexDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Hex)
        {
            _data = data;
            HexText = BuildHexDump(data, PreviewLimit);
        }

        public string HexText { get; }

        public int ByteCount => _data.Length;

        public override byte[] Export() => _data;

        private static string BuildHexDump(byte[] data, int limit)
        {
            int n = Math.Min(data.Length, limit);
            var sb = new StringBuilder(n * 4);
            var ascii = new char[16];

            for (int off = 0; off < n; off += 16)
            {
                sb.Append(off.ToString("x8")).Append("  ");
                int row = Math.Min(16, n - off);
                for (int i = 0; i < 16; i++)
                {
                    if (i < row)
                    {
                        byte b = data[off + i];
                        sb.Append(b.ToString("x2")).Append(' ');
                        ascii[i] = b >= 32 && b < 127 ? (char)b : '.';
                    }
                    else
                    {
                        sb.Append("   ");
                        ascii[i] = ' ';
                    }
                    if (i == 7)
                    {
                        sb.Append(' ');
                    }
                }
                sb.Append(' ').Append(ascii, 0, 16).Append('\n');
            }

            if (data.Length > limit)
            {
                sb.Append("… ").Append(data.Length - limit).Append(" more bytes (preview truncated)\n");
            }
            return sb.ToString();
        }
    }
}
