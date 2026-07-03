using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using RaxicoreEditor.EngineAssets.Surfaces;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// Surface tile (.srf) editor. Renders the 128×128 surface-type grid as a colour-coded bitmap and
    /// lets you paint surface types onto cells (pick a type from the legend palette, drag on the grid).
    /// The .srf is fixed-layout so edits re-encode directly; Export returns the edited bytes.
    /// </summary>
    public sealed class SurfaceDocument : DocumentBase
    {
        private readonly byte[] _data;
        private readonly SurfaceTile? _tile;
        private byte _paintType;

        public SurfaceDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Surface)
        {
            _data = data;
            try
            {
                _tile = SurfaceTile.Parse(data);
                if (_tile.IsFull)
                {
                    var counts = new int[256];
                    Surface = BuildBitmap(_tile, counts);
                    Legend = BuildLegend(counts);
                    int distinct = 0;
                    foreach (int c in counts)
                    {
                        if (c > 0) distinct++;
                    }
                    Info = $"128×128 surface tile · {distinct} surface types · header {HeaderHex(_tile)}";
                    // Default paint = the most common non-zero type, else 0.
                    _paintType = Legend.Count > 0 ? Legend[0].Type : (byte)0;
                }
                else
                {
                    Info = $"sparse/short surface tile ({data.Length} bytes) — no full grid to edit";
                }
            }
            catch (Exception ex)
            {
                Info = "parse failed: " + ex.Message;
            }
            SelectTypeCommand = new RelayCommand<LegendEntry>(le => { if (le != null) PaintType = le.Type; });
        }

        /// <summary>The editable 128×128 grid bitmap (one pixel per cell).</summary>
        public WriteableBitmap? Surface { get; }
        public string Info { get; } = "";
        public IReadOnlyList<LegendEntry> Legend { get; } = Array.Empty<LegendEntry>();
        public ICommand SelectTypeCommand { get; }
        public bool CanEdit => _tile is { IsFull: true };
        public int GridDim => SurfaceTile.GridDim;

        public byte PaintType
        {
            get => _paintType;
            set
            {
                if (SetProperty(ref _paintType, value))
                {
                    RaisePropertyChanged(nameof(CurrentPaintBrush));
                    RaisePropertyChanged(nameof(PaintInfo));
                }
            }
        }

        public IBrush CurrentPaintBrush
        {
            get { (byte r, byte g, byte b) = TypeColor(_paintType); return new SolidColorBrush(Color.FromRgb(r, g, b)); }
        }

        public string PaintInfo => $"painting type {_paintType}";

        /// <summary>Paint the selected type onto a cell (and update the bitmap in place).</summary>
        public void Paint(int row, int col)
        {
            if (_tile is not { IsFull: true } || Surface == null)
            {
                return;
            }
            if ((uint)row >= SurfaceTile.GridDim || (uint)col >= SurfaceTile.GridDim)
            {
                return;
            }
            SurfaceTile.Cell old = _tile.GetCell(row, col);
            if (old.Type == _paintType)
            {
                return; // no change
            }
            _tile.SetCell(row, col, new SurfaceTile.Cell(_paintType, old.Flags, old.Blend));
            (byte r, byte g, byte b) = TypeColor(_paintType);
            using (ILockedFramebuffer fb = Surface.Lock())
            {
                IntPtr p = IntPtr.Add(fb.Address, row * fb.RowBytes + col * 4);
                Marshal.WriteByte(p, 0, b);
                Marshal.WriteByte(p, 1, g);
                Marshal.WriteByte(p, 2, r);
                Marshal.WriteByte(p, 3, 255);
            }
            IsDirty = true;
        }

        public override byte[] Export() => _tile != null ? _tile.ToBytes() : _data;

        public sealed class LegendEntry
        {
            public required byte Type { get; init; }
            public required IBrush Swatch { get; init; }
            public required string Label { get; init; }
        }

        private static string HeaderHex(SurfaceTile tile)
        {
            var sb = new StringBuilder();
            foreach (byte b in tile.Header)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        private static WriteableBitmap BuildBitmap(SurfaceTile tile, int[] counts)
        {
            const int dim = SurfaceTile.GridDim; // 128
            var bgra = new byte[dim * dim * 4];
            for (int row = 0; row < dim; row++)
            {
                for (int col = 0; col < dim; col++)
                {
                    byte type = tile.GetCell(row, col).Type;
                    counts[type]++;
                    (byte r, byte g, byte b) = TypeColor(type);
                    int o = (row * dim + col) * 4;
                    bgra[o + 0] = b;
                    bgra[o + 1] = g;
                    bgra[o + 2] = r;
                    bgra[o + 3] = 255;
                }
            }

            var bmp = new WriteableBitmap(new PixelSize(dim, dim), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
            using ILockedFramebuffer fb = bmp.Lock();
            int srcRow = dim * 4;
            for (int y = 0; y < dim; y++)
            {
                Marshal.Copy(bgra, y * srcRow, IntPtr.Add(fb.Address, y * fb.RowBytes), srcRow);
            }
            return bmp;
        }

        private static List<LegendEntry> BuildLegend(int[] counts)
        {
            var present = new List<(int type, int count)>();
            for (int t = 0; t < 256; t++)
            {
                if (counts[t] > 0)
                {
                    present.Add((t, counts[t]));
                }
            }
            present.Sort((a, b) => b.count.CompareTo(a.count));

            var legend = new List<LegendEntry>();
            int limit = Math.Min(present.Count, 24);
            for (int i = 0; i < limit; i++)
            {
                (int type, int count) = present[i];
                (byte r, byte g, byte b) = TypeColor((byte)type);
                legend.Add(new LegendEntry
                {
                    Type = (byte)type,
                    Swatch = new SolidColorBrush(Color.FromRgb(r, g, b)),
                    Label = $"type {type}  ·  {count} cells",
                });
            }
            return legend;
        }

        /// <summary>Stable, well-separated colour per surface type (golden-ratio hue spread).</summary>
        private static (byte, byte, byte) TypeColor(byte type)
        {
            if (type == 0)
            {
                return (46, 49, 56); // type 0 = empty/default — neutral so real surfaces stand out
            }
            double hue = (type * 0.61803398875) % 1.0 * 360.0;
            return HsvToRgb(hue, 0.58, 0.96);
        }

        private static (byte, byte, byte) HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
            double m = v - c;
            double r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return ((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }
    }
}
