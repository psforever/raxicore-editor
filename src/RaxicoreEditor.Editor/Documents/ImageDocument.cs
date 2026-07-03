using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RaxicoreEditor.EngineAssets.Textures;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Texture document (DDS). Decodes the top mip to a preview bitmap. Export returns the bytes.
    /// </summary>
    public sealed class ImageDocument : DocumentBase
    {
        private readonly byte[] _data;

        public ImageDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Image)
        {
            _data = data;
            try
            {
                DdsImage dds = DdsImage.Decode(data);
                Preview = BuildBitmap(dds);
                Info = $"{dds.Format}  {dds.Width}×{dds.Height}";
            }
            catch (Exception ex)
            {
                Info = "decode failed: " + ex.Message;
            }
        }

        public Bitmap? Preview { get; }
        public string Info { get; } = "";

        public override byte[] Export() => _data;

        private static WriteableBitmap BuildBitmap(DdsImage dds)
        {
            var bmp = new WriteableBitmap(new PixelSize(dds.Width, dds.Height), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer fb = bmp.Lock();
            int srcRow = dds.Width * 4;
            for (int y = 0; y < dds.Height; y++)
            {
                Marshal.Copy(dds.Bgra, y * srcRow, IntPtr.Add(fb.Address, y * fb.RowBytes), srcRow);
            }
            return bmp;
        }
    }
}
