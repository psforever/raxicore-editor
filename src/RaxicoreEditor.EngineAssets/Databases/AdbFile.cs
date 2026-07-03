using System;
using System.Collections.Generic;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Databases
{
    /// <summary>
    /// General reader for the "chunky" AsciiDatabase (.adb) container, based on the wrapper in
    /// the engine-derived reference implementation's <c>ascii_database.cpp</c>. Strips the chunky/asciidatabase
    /// framing and exposes the section keyword, the symbol count, and the NUL-delimited token
    /// stream — enough to view/inspect every .adb section type (waves, physmaterial, light3d,
    /// game_objects, …). The full game_objects command-stream decode is a higher layer.
    /// </summary>
    public sealed class AdbFile
    {
        public string SectionKeyword { get; private set; } = "";

        /// <summary>Byte length of the leading NUL-delimited string pool (the database's symbol table).</summary>
        public uint SymbolCount { get; private set; }

        /// <summary>Byte length of the binary record/command stream that follows the string pool.</summary>
        public int CommandStreamLength { get; private set; }

        public IReadOnlyList<string> Tokens => _tokens;
        private readonly List<string> _tokens = new();

        public static bool IsChunky(ReadOnlySpan<byte> d) =>
            d.Length >= 6 && d[0] == 'c' && d[1] == 'h' && d[2] == 'u' && d[3] == 'n' && d[4] == 'k' && d[5] == 'y';

        public static AdbFile Parse(byte[] data)
        {
            if (!IsChunky(data))
            {
                throw new InvalidOperationException("Not a chunky/adb container");
            }

            var adb = new AdbFile();
            int root = FindBytes(data, "asciidatabase", 0);
            if (root < 0)
            {
                throw new InvalidOperationException("adb: missing asciidatabase chunk");
            }

            // body = root tag (13) + NUL (1) + 4 header bytes + u32 payloadSize
            int o = root + 13 + 1 + 4 + 4;
            if (o + 4 > data.Length)
            {
                throw new InvalidOperationException("adb: truncated");
            }

            adb.SectionKeyword = ReadCString(data, ref o);
            adb.SymbolCount = ReadU32(data, o);
            o += 4;

            // The string pool is exactly SymbolCount bytes of NUL-delimited symbols; a binary
            // record/command stream follows it (decoded by higher layers, e.g. game_objects — NOT
            // text). Bounding to the pool keeps the token view clean instead of bleeding binary
            // bytes into garbage rows.
            int poolEnd = (int)Math.Min((long)o + adb.SymbolCount, data.Length);
            while (o < poolEnd)
            {
                string tok = ReadCString(data, ref o, poolEnd);
                if (tok.Length == 0)
                {
                    continue; // NUL padding inside / at the end of the pool
                }
                adb._tokens.Add(tok);
            }
            adb.CommandStreamLength = Math.Max(0, data.Length - poolEnd);
            return adb;
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.Append("section: ").Append(SectionKeyword).Append('\n');
            sb.Append("string pool: ").Append(SymbolCount).Append(" bytes\n");
            sb.Append("strings: ").Append(_tokens.Count).Append('\n');
            sb.Append("binary records: ").Append(CommandStreamLength).Append(" bytes\n\n");
            foreach (string t in _tokens)
            {
                sb.Append(t).Append('\n');
            }
            return sb.ToString();
        }

        private static string ReadCString(byte[] d, ref int p) => ReadCString(d, ref p, d.Length);

        private static string ReadCString(byte[] d, ref int p, int end)
        {
            if (end > d.Length) end = d.Length;
            int start = p;
            while (p < end && d[p] != 0) p++;
            string s = Encoding.Latin1.GetString(d, start, p - start);
            if (p < end) p++; // NUL
            return s;
        }

        private static uint ReadU32(byte[] b, int o) =>
            (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);

        private static int FindBytes(byte[] hay, string needle, int from)
        {
            byte[] n = Encoding.ASCII.GetBytes(needle);
            for (int i = from; i + n.Length <= hay.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < n.Length; k++)
                {
                    if (hay[i + k] != n[k]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
