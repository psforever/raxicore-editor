using System;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Textures
{
    /// <summary>
    /// Minimal DDS decoder for texture preview. Decodes the top mip of BC1 (DXT1), BC2 (DXT3),
    /// BC3 (DXT5), and uncompressed 32-bpp images to tightly-packed BGRA (premultiplied-agnostic,
    /// opaque alpha). The engine ships pre-converted DDS textures in the FLAT archives.
    /// </summary>
    public sealed class DdsImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public byte[] Bgra { get; private set; } = Array.Empty<byte>();
        public string Format { get; private set; } = "";

        public static bool HasMagic(ReadOnlySpan<byte> b) =>
            b.Length >= 4 && b[0] == 'D' && b[1] == 'D' && b[2] == 'S' && b[3] == ' ';

        public static DdsImage Decode(byte[] data)
        {
            if (!HasMagic(data) || data.Length < 128)
            {
                throw new InvalidOperationException("Not a DDS file");
            }

            int height = (int)ReadU32(data, 12);
            int width = (int)ReadU32(data, 16);
            uint pfFlags = ReadU32(data, 80);
            string fourCc = Encoding.ASCII.GetString(data, 84, 4);
            uint rgbBitCount = ReadU32(data, 88);

            var img = new DdsImage { Width = width, Height = height };
            var bgra = new byte[width * height * 4];
            int dataOffset = 128;

            const uint DdpfFourCc = 0x4;
            const uint DdpfRgb = 0x40;

            if ((pfFlags & DdpfFourCc) != 0)
            {
                switch (fourCc)
                {
                    case "DXT1":
                        DecodeBc1(data, dataOffset, width, height, bgra);
                        img.Format = "DXT1/BC1";
                        break;
                    case "DXT3":
                        DecodeBc2Bc3(data, dataOffset, width, height, bgra, explicitAlpha: true);
                        img.Format = "DXT3/BC2";
                        break;
                    case "DXT5":
                        DecodeBc2Bc3(data, dataOffset, width, height, bgra, explicitAlpha: false);
                        img.Format = "DXT5/BC3";
                        break;
                    default:
                        throw new NotSupportedException("Unsupported DDS fourCC: " + fourCc);
                }
            }
            else if ((pfFlags & DdpfRgb) != 0 && rgbBitCount == 32)
            {
                // Assume A8R8G8B8 / X8R8G8B8 (BGRA byte order in memory) — copy directly.
                int n = Math.Min(bgra.Length, data.Length - dataOffset);
                Array.Copy(data, dataOffset, bgra, 0, n);
                // X8R8G8B8 (no DDPF_ALPHAPIXELS) carries garbage in the X byte; force it opaque so the
                // renderer's alpha test doesn't wrongly discard it.
                const uint DdpfAlphaPixels = 0x1;
                if ((pfFlags & DdpfAlphaPixels) == 0)
                {
                    for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
                }
                img.Format = "RGB32";
            }
            else
            {
                throw new NotSupportedException("Unsupported DDS pixel format");
            }

            img.Bgra = bgra;
            return img;
        }

        private static void DecodeBc1(byte[] d, int off, int w, int h, byte[] bgra)
        {
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<byte> colors = stackalloc byte[16]; // 4 colors × BGRA
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int p = off + (by * blocksX + bx) * 8;
                    DecodeBc1Colors(d, p, colors, out _);
                    uint idx = ReadU32(d, p + 4);
                    EmitBlock(bgra, w, h, bx, by, colors, idx);
                }
            }
        }

        private static void DecodeBc2Bc3(byte[] d, int off, int w, int h, byte[] bgra, bool explicitAlpha)
        {
            int blocksX = (w + 3) / 4;
            int blocksY = (h + 3) / 4;
            Span<byte> colors = stackalloc byte[16];
            Span<byte> alpha = stackalloc byte[16];
            for (int by = 0; by < blocksY; by++)
            {
                for (int bx = 0; bx < blocksX; bx++)
                {
                    int block = off + (by * blocksX + bx) * 16;
                    if (explicitAlpha)
                    {
                        for (int i = 0; i < 16; i++)
                        {
                            int nib = (d[block + (i >> 1)] >> ((i & 1) * 4)) & 0xF;
                            alpha[i] = (byte)(nib * 17);
                        }
                    }
                    else
                    {
                        DecodeBc3Alpha(d, block, alpha);
                    }
                    int colorBlock = block + 8;
                    DecodeBc1Colors(d, colorBlock, colors, out _);
                    uint idx = ReadU32(d, colorBlock + 4);
                    EmitBlock(bgra, w, h, bx, by, colors, idx, alpha);
                }
            }
        }

        private static void DecodeBc1Colors(byte[] d, int p, Span<byte> colors, out bool hasAlpha)
        {
            ushort c0 = (ushort)(d[p] | (d[p + 1] << 8));
            ushort c1 = (ushort)(d[p + 2] | (d[p + 3] << 8));
            Span<byte> e0 = stackalloc byte[3];
            Span<byte> e1 = stackalloc byte[3];
            Rgb565(c0, e0);
            Rgb565(c1, e1);

            // colors stored BGRA.
            SetColor(colors, 0, e0[2], e0[1], e0[0]);
            SetColor(colors, 1, e1[2], e1[1], e1[0]);
            if (c0 > c1)
            {
                for (int k = 0; k < 3; k++)
                {
                    colors[8 + k] = (byte)((2 * e_(colors, 0, k) + e_(colors, 1, k)) / 3);
                    colors[12 + k] = (byte)((e_(colors, 0, k) + 2 * e_(colors, 1, k)) / 3);
                }
                colors[11] = colors[15] = 255;
                hasAlpha = false;
            }
            else
            {
                for (int k = 0; k < 3; k++)
                {
                    colors[8 + k] = (byte)((e_(colors, 0, k) + e_(colors, 1, k)) / 2);
                    colors[12 + k] = 0;
                }
                colors[11] = 255;
                colors[15] = 0; // transparent black
                hasAlpha = true;
            }
        }

        private static void DecodeBc3Alpha(byte[] d, int block, Span<byte> alpha)
        {
            byte a0 = d[block];
            byte a1 = d[block + 1];
            Span<byte> a = stackalloc byte[8];
            a[0] = a0;
            a[1] = a1;
            if (a0 > a1)
            {
                for (int i = 1; i < 7; i++) a[i + 1] = (byte)(((7 - i) * a0 + i * a1) / 7);
            }
            else
            {
                for (int i = 1; i < 5; i++) a[i + 1] = (byte)(((5 - i) * a0 + i * a1) / 5);
                a[6] = 0;
                a[7] = 255;
            }
            ulong bits = 0;
            for (int i = 0; i < 6; i++) bits |= (ulong)d[block + 2 + i] << (8 * i);
            for (int i = 0; i < 16; i++)
            {
                alpha[i] = a[(int)((bits >> (3 * i)) & 7)];
            }
        }

        private static void EmitBlock(byte[] bgra, int w, int h, int bx, int by, Span<byte> colors,
            uint idx, Span<byte> alpha = default)
        {
            for (int i = 0; i < 16; i++)
            {
                int px = bx * 4 + (i & 3);
                int py = by * 4 + (i >> 2);
                if (px >= w || py >= h)
                {
                    continue;
                }
                int ci = (int)((idx >> (2 * i)) & 3);
                int dst = (py * w + px) * 4;
                bgra[dst + 0] = colors[ci * 4 + 0];
                bgra[dst + 1] = colors[ci * 4 + 1];
                bgra[dst + 2] = colors[ci * 4 + 2];
                bgra[dst + 3] = alpha.Length == 16 ? alpha[i] : colors[ci * 4 + 3];
            }
        }

        private static void SetColor(Span<byte> colors, int index, byte b, byte g, byte r)
        {
            colors[index * 4 + 0] = b;
            colors[index * 4 + 1] = g;
            colors[index * 4 + 2] = r;
            colors[index * 4 + 3] = 255;
        }

        private static byte e_(Span<byte> colors, int index, int k) => colors[index * 4 + k];

        private static void Rgb565(ushort c, Span<byte> rgb)
        {
            int r = (c >> 11) & 31;
            int g = (c >> 5) & 63;
            int b = c & 31;
            rgb[0] = (byte)((r << 3) | (r >> 2));
            rgb[1] = (byte)((g << 2) | (g >> 4));
            rgb[2] = (byte)((b << 3) | (b >> 2));
        }

        private static uint ReadU32(byte[] b, int o) =>
            (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
    }
}
