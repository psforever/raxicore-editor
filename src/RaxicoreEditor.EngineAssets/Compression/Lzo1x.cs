using System;

namespace RaxicoreEditor.EngineAssets.Compression
{
    public enum LzoResult
    {
        Ok = 0,
        InputNotConsumed = 1,
        InputOverrun = 2,
    }

    /// <summary>
    /// LZO1X-1 codec used by every engine-derived <c>.pak</c> record (4-byte record tag "LZO1").
    /// Decoder is a faithful flat port of the reference implementation's grammar in
    /// <c>lzo1.cpp</c> (leaf sub_850700): literal runs of (token+3),
    /// matches of (length+2), distance biases of -1 / -(1+0x800) / -0x4000 with the (n&amp;8)&lt;&lt;11
    /// high bit, and the M4 m_pos==op end-of-stream marker. The encoder emits a valid store-mode
    /// stream (single literal run + EOS) for round-trippable repacking.
    /// </summary>
    public static class Lzo1x
    {
        /// <summary>
        /// Decompress <paramref name="input"/> into <paramref name="output"/> (sized to the expected
        /// uncompressed length). Returns the LZO result; <paramref name="produced"/> is the number of
        /// bytes written. Padded <c>.pak</c> records legitimately yield <see cref="LzoResult.InputNotConsumed"/>.
        /// </summary>
        public static LzoResult Decompress(ReadOnlySpan<byte> input, Span<byte> output, out int produced)
        {
            int ip = 0;
            int op = 0;
            int n;

            // A leading control byte > 0x11 opens the stream with a literal run of (value - 0x11).
            if (input[ip] > 17)
            {
                n = input[ip++] - 17;
                if (n < 4)
                {
                    goto carry_literals;
                }
                do { output[op++] = input[ip++]; } while (--n > 0);
                goto after_run;
            }

        loop_top:
            n = input[ip++];
            if (n >= 16)
            {
                goto decode_match;
            }
            if (n == 0)
            {
                while (input[ip] == 0) { n += 255; ip++; }
                n += 15 + input[ip++];
            }
            // Literal run of (n + 3) bytes.
            {
                int count = n + 3;
                do { output[op++] = input[ip++]; } while (--count > 0);
            }

        after_run:
            n = input[ip++];
            if (n >= 16)
            {
                goto decode_match;
            }
            {
                int rf = op - (1 + 0x0800);
                rf -= n >> 2;
                rf -= input[ip++] << 2;
                output[op++] = output[rf++];
                output[op++] = output[rf++];
                output[op++] = output[rf];
            }
            goto end_match;

        decode_match:
            {
                int rf;
                if (n >= 64) // M2
                {
                    rf = op - 1 - ((n >> 2) & 7) - (input[ip++] << 3);
                    n = (n >> 5) - 1;
                }
                else if (n >= 32) // M3
                {
                    n &= 31;
                    if (n == 0)
                    {
                        while (input[ip] == 0) { n += 255; ip++; }
                        n += 31 + input[ip++];
                    }
                    rf = op - 1 - (ReadU16(input, ip) >> 2);
                    ip += 2;
                }
                else if (n >= 16) // M4
                {
                    rf = op - ((n & 8) << 11);
                    n &= 7;
                    if (n == 0)
                    {
                        while (input[ip] == 0) { n += 255; ip++; }
                        n += 7 + input[ip++];
                    }
                    rf -= ReadU16(input, ip) >> 2;
                    ip += 2;
                    if (rf == op)
                    {
                        goto stream_end; // end-of-stream marker
                    }
                    rf -= 0x4000;
                }
                else // M1
                {
                    rf = op - 1 - (n >> 2) - (input[ip++] << 2);
                    output[op++] = output[rf++];
                    output[op++] = output[rf];
                    goto end_match;
                }

                // Match copy of (n + 2) bytes from rf.
                output[op++] = output[rf++];
                output[op++] = output[rf++];
                do { output[op++] = output[rf++]; } while (--n > 0);
            }

        end_match:
            n = input[ip - 2] & 3;
            if (n == 0)
            {
                goto loop_top;
            }
        carry_literals:
            do { output[op++] = input[ip++]; } while (--n > 0);
            n = input[ip++];
            goto decode_match;

        stream_end:
            produced = op;
            if (ip == input.Length) return LzoResult.Ok;
            return ip < input.Length ? LzoResult.InputNotConsumed : LzoResult.InputOverrun;
        }

        /// <summary>Convenience overload that allocates the output buffer.</summary>
        public static byte[] Decompress(ReadOnlySpan<byte> input, int uncompressedSize)
        {
            var output = new byte[uncompressedSize];
            Decompress(input, output, out int produced);
            if (produced != uncompressedSize)
            {
                throw new InvalidOperationException(
                    $"LZO1X produced {produced} bytes, expected {uncompressedSize}");
            }
            return output;
        }

        private static int ReadU16(ReadOnlySpan<byte> b, int o) => b[o] | (b[o + 1] << 8);

        // ---- store-mode encoder (valid, decodable; not ratio-optimised) -----------------------

        private static readonly byte[] Eos = { 0x11, 0x00, 0x00 }; // M4 marker, distance 0

        /// <summary>
        /// Encode <paramref name="raw"/> as a store-mode LZO1X stream (one literal run + EOS marker)
        /// that decodes back to exactly <paramref name="raw"/>.
        /// </summary>
        public static byte[] StoreCompress(ReadOnlySpan<byte> raw)
        {
            int len = raw.Length;
            var io = new System.IO.MemoryStream();

            if (len == 0)
            {
                io.Write(Eos, 0, Eos.Length);
                return io.ToArray();
            }

            if (len <= 3)
            {
                // First-byte fast path: token = len + 17 copies len literals, then a match (EOS).
                io.WriteByte((byte)(len + 17));
                WriteSpan(io, raw);
            }
            else if (len <= 18)
            {
                // Short literal run: token (len - 3) in [1..15].
                io.WriteByte((byte)(len - 3));
                WriteSpan(io, raw);
            }
            else
            {
                // Long literal run: token 0, then (len - 18) as N*255 zero bytes + final remainder.
                io.WriteByte(0);
                int r = len - 18;
                while (r > 255) { io.WriteByte(0); r -= 255; }
                io.WriteByte((byte)r); // r in [1..255]
                WriteSpan(io, raw);
            }

            io.Write(Eos, 0, Eos.Length);
            return io.ToArray();
        }

        private static void WriteSpan(System.IO.MemoryStream io, ReadOnlySpan<byte> data)
        {
            foreach (byte b in data)
            {
                io.WriteByte(b);
            }
        }
    }
}
