using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using RaxicoreEditor.Editor.Documents;

namespace RaxicoreEditor.Editor.Controls
{
    /// <summary>
    /// Renders a <see cref="SurfaceDocument"/>'s 128×128 grid bitmap (crisp, square-letterboxed) and
    /// paints the document's selected surface type onto cells under the pointer (click or drag).
    /// </summary>
    public sealed class SurfaceEditor : Control
    {
        private bool _painting;

        public SurfaceEditor()
        {
            ClipToBounds = true;
            RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        }

        private SurfaceDocument? Doc => DataContext as SurfaceDocument;

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            WriteableBitmap? bmp = Doc?.Surface;
            if (bmp == null)
            {
                return;
            }
            Rect dest = SquareRect();
            context.DrawImage(bmp, new Rect(bmp.Size), dest);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _painting = true;
                PaintAt(e.GetPosition(this));
                e.Pointer.Capture(this);
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_painting)
            {
                PaintAt(e.GetPosition(this));
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _painting = false;
            e.Pointer.Capture(null);
        }

        private void PaintAt(Point pos)
        {
            SurfaceDocument? doc = Doc;
            if (doc is not { CanEdit: true })
            {
                return;
            }
            Rect dest = SquareRect();
            if (dest.Width < 1)
            {
                return;
            }
            double nx = (pos.X - dest.X) / dest.Width;
            double ny = (pos.Y - dest.Y) / dest.Height;
            if (nx < 0 || nx >= 1 || ny < 0 || ny >= 1)
            {
                return;
            }
            int col = (int)(nx * doc.GridDim);
            int row = (int)(ny * doc.GridDim);
            doc.Paint(row, col);
            InvalidateVisual();
        }

        private Rect SquareRect()
        {
            double side = Math.Min(Bounds.Width, Bounds.Height);
            double x0 = (Bounds.Width - side) / 2;
            double y0 = (Bounds.Height - side) / 2;
            return new Rect(x0, y0, side, side);
        }
    }
}
