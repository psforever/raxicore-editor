using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RaxicoreEditor.Editor.Documents;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Editor.Controls
{
    /// <summary>
    /// A dope sheet for the active animation clip: one row per bone (track), a keyframe tick at each of that
    /// bone's key times along the time axis, and a draggable playhead at the current time. Reads its data
    /// from the <see cref="MeshDocument"/> in its DataContext; clicking/dragging seeks (and pauses playback).
    /// Height grows with the bone count so it scrolls inside a ScrollViewer.
    /// </summary>
    public sealed class AnimDopeSheet : Control
    {
        private const double RowH = 13;
        private const double LabelW = 66;

        // Colours chosen to read on both the light and dark themes.
        private static readonly IBrush SepBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80));
        private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x90, 0x90, 0x90));
        private static readonly IBrush TickBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x90, 0xD9));
        private static readonly IBrush SelBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x40, 0x9C, 0xFF));
        private static readonly Pen PlayheadPen = new(new SolidColorBrush(Color.FromArgb(0xFF, 0xE0, 0x40, 0x40)), 1.5);
        private static readonly Pen SepPen = new(SepBrush, 1);

        private MeshDocument? _doc;

        public AnimDopeSheet()
        {
            ClipToBounds = true;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_doc != null) _doc.PropertyChanged -= OnDocChanged;
            _doc = DataContext as MeshDocument;
            if (_doc != null) _doc.PropertyChanged += OnDocChanged;
            InvalidateMeasure();
            InvalidateVisual();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_doc != null) _doc.PropertyChanged -= OnDocChanged;
        }

        private void OnDocChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(MeshDocument.AnimTime):
                case nameof(MeshDocument.SelectedTrack):
                    InvalidateVisual(); // just the playhead / row highlight moved
                    break;
                case nameof(MeshDocument.ClipStats):     // fired when the clip changes
                case nameof(MeshDocument.HasActiveClip):
                    InvalidateMeasure();                 // track count (height) may have changed
                    InvalidateVisual();
                    break;
            }
        }

        private IReadOnlyList<AnimTrack> Tracks => _doc?.ClipTracks ?? Array.Empty<AnimTrack>();

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsFinite(availableSize.Width) ? availableSize.Width : 260;
            return new Size(w, Math.Max(RowH, Tracks.Count * RowH));
        }

        public override void Render(DrawingContext ctx)
        {
            base.Render(ctx);
            IReadOnlyList<AnimTrack> tracks = Tracks;
            float dur = _doc?.ActiveClip?.Duration ?? 0f;
            if (tracks.Count == 0 || dur <= 0f)
            {
                return;
            }

            double width = Bounds.Width;
            double timeW = Math.Max(1, width - LabelW);
            AnimTrack? selected = _doc?.SelectedTrack;
            var typeface = new Typeface(FontFamily.Default);

            for (int i = 0; i < tracks.Count; i++)
            {
                AnimTrack tk = tracks[i];
                double y = i * RowH;
                if (ReferenceEquals(tk, selected))
                {
                    ctx.FillRectangle(SelBrush, new Rect(0, y, width, RowH));
                }
                // Bone name (truncated to the label column).
                var ft = new FormattedText(Truncate(tk.Name, 11), CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, typeface, 9.5, LabelBrush);
                ctx.DrawText(ft, new Point(2, y + (RowH - ft.Height) / 2));
                // Row baseline.
                ctx.DrawLine(SepPen, new Point(LabelW, y + RowH - 0.5), new Point(width, y + RowH - 0.5));
                // Keyframe ticks for this bone (its own pos+rot key times).
                DrawTicks(ctx, TickBrush, tk, y, dur, timeW);
            }

            // Playhead across all rows.
            double px = LabelW + (_doc!.AnimTime / dur) * timeW;
            px = Math.Clamp(px, LabelW, width);
            ctx.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, tracks.Count * RowH));
        }

        private static void DrawTicks(DrawingContext ctx, IBrush tick, AnimTrack tk, double y, float dur, double timeW)
        {
            void Tick(float t)
            {
                double x = LabelW + (t / dur) * timeW;
                ctx.FillRectangle(tick, new Rect(x - 0.75, y + 2, 1.5, RowH - 4));
            }
            if (tk.PosKeys.Count == 0 && tk.RotKeys.Count == 0)
            {
                return; // static bone: no ticks
            }
            foreach (AnimKey<System.Numerics.Vector3> k in tk.PosKeys) Tick(k.Time);
            foreach (AnimKey<System.Numerics.Quaternion> k in tk.RotKeys) Tick(k.Time);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            SeekTo(e.GetCurrentPoint(this).Position.X);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                SeekTo(e.GetCurrentPoint(this).Position.X);
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void SeekTo(double x)
        {
            float dur = _doc?.ActiveClip?.Duration ?? 0f;
            if (_doc == null || dur <= 0f)
            {
                return;
            }
            double timeW = Math.Max(1, Bounds.Width - LabelW);
            float t = (float)Math.Clamp((x - LabelW) / timeW, 0, 1) * dur;
            _doc.IsPlaying = false;
            _doc.AnimTime = t; // re-poses the model + moves the playhead
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";
    }
}
