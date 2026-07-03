using System;
using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using RaxicoreEditor.Editor.Documents;

namespace RaxicoreEditor.Editor.Rendering
{
    /// <summary>
    /// Avalonia control that hosts the offscreen Silk.NET Vulkan mesh renderer. It renders to a
    /// WriteableBitmap (no on-screen Vulkan surface) and orbits via pointer input.
    /// </summary>
    public sealed class VulkanViewport : Control
    {
        private MeshViewportRenderer? _renderer;
        private readonly OrbitCamera _camera = new();
        private WriteableBitmap? _bitmap;
        private byte[] _pixels = Array.Empty<byte>();
        private DispatcherTimer? _timer;
        private MeshDocument? _doc;
        private bool _needsRender = true;
        private int _pw, _ph;
        private Point _lastPos;
        private bool _orbit, _pan;

        // Animation / skinning.
        private SkeletalAnimator? _animator;
        private MeshPart? _part;
        private float[]?[] _skinBuffers = Array.Empty<float[]?>();
        private int[] _skinnedIndices = Array.Empty<int>();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private double _lastSec;

        public VulkanViewport()
        {
            ClipToBounds = true;
        }

        // A bare Control reports a desired size of (0,0); under a themed content presenter that
        // sizes-to-content this collapses the viewport to nothing (the ~1-inch render). Claim all
        // the space the parent offers so the viewport fills the tab.
        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsFinite(availableSize.Width) ? availableSize.Width : 0;
            double h = double.IsFinite(availableSize.Height) ? availableSize.Height : 0;
            return new Size(w, h);
        }

        // The render only happens when _needsRender is set; nothing re-triggered it after a layout
        // resize, so a first render at a transient small size would stick. Re-render on every arrange.
        protected override Size ArrangeOverride(Size finalSize)
        {
            Size s = base.ArrangeOverride(finalSize);
            _needsRender = true;
            return s;
        }

        protected override void OnDataContextChanged(EventArgs e)
        {
            base.OnDataContextChanged(e);
            if (_doc != null)
            {
                _doc.GeometryChanged -= OnGeometryChanged;
                _doc.AnimChanged -= OnAnimChanged;
                _doc.TexturesChanged -= OnTexturesChanged;
            }
            _doc = DataContext as MeshDocument;
            if (_doc != null)
            {
                _doc.GeometryChanged += OnGeometryChanged;
                _doc.AnimChanged += OnAnimChanged;
                _doc.TexturesChanged += OnTexturesChanged;
            }
            UploadMesh();
        }

        private void OnGeometryChanged()
        {
            // Selected part changed in the inspector — re-upload and re-frame.
            UploadMesh();
        }

        private void OnTexturesChanged()
        {
            // A texture override was applied — re-upload the SAME geometry (new textures) without
            // re-framing the camera or rebuilding the animator.
            if (_renderer == null || _doc == null || !_doc.HasMesh)
            {
                return;
            }
            _renderer.SetMesh(_doc.Submeshes);
            if (_doc is { IsPlaying: false } && _doc.ActiveClip != null)
            {
                SkinAndUpload(_doc.AnimTime); // keep the posed frame after a re-upload
            }
            _needsRender = true;
        }

        private void OnAnimChanged()
        {
            // A clip was selected — show its first frame even while paused.
            if (_renderer != null && _animator != null && _doc?.ActiveClip != null)
            {
                SkinAndUpload(0f);
                _needsRender = true;
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            VulkanContext? ctx = VulkanContext.TryGetShared();
            if (ctx == null)
            {
                return;
            }
            try
            {
                _renderer = new MeshViewportRenderer(ctx);
                UploadMesh();
            }
            catch
            {
                _renderer = null;
                return;
            }

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
            _needsRender = true;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (_doc != null)
            {
                _doc.GeometryChanged -= OnGeometryChanged;
                _doc.AnimChanged -= OnAnimChanged;
                _doc.TexturesChanged -= OnTexturesChanged;
            }
            _timer?.Stop();
            _timer = null;
            _renderer?.Dispose();
            _renderer = null;
            _bitmap?.Dispose();
            _bitmap = null;
            _pw = _ph = 0;
        }

        private void UploadMesh()
        {
            if (_renderer == null || _doc == null || !_doc.HasMesh)
            {
                return;
            }
            _renderer.SetMesh(_doc.Submeshes);
            _camera.Frame(_doc.BoundsMin, _doc.BoundsMax);
            BuildAnimator();
            _needsRender = true;
        }

        private void BuildAnimator()
        {
            _animator = null;
            _part = _doc?.SelectedPart;
            _skinBuffers = Array.Empty<float[]?>();
            _skinnedIndices = Array.Empty<int>();
            if (_part?.Skeleton == null)
            {
                return;
            }
            // Build the animator whenever a skeleton exists, not just for vertex-skinned parts — most
            // bones in this engine drive RIGID mesh-on-bone parts (doors, turrets), not vertex skinning,
            // and the skeleton overlay (below) should work for those too. The skin-buffer setup that
            // follows still only populates entries for truly vertex-skinned submeshes.
            _animator = new SkeletalAnimator(_part.Skeleton);
            var buffers = new float[]?[_part.Submeshes.Count];
            var idxs = new System.Collections.Generic.List<int>();
            for (int i = 0; i < _part.Submeshes.Count; i++)
            {
                if (_part.Submeshes[i].IsSkinned)
                {
                    buffers[i] = (float[])_part.Submeshes[i].Vertices.Clone();
                    idxs.Add(i);
                }
            }
            _skinBuffers = buffers;
            _skinnedIndices = idxs.ToArray();

            TryAutoLoadAnims();
        }

        private string? _animAutoLoadedFor;

        // Auto-load the sibling anims.ubr (on a background thread; ~62 MB, cached) so an animatable
        // model's clips appear without the manual "Load anims.ubr…" step.
        private void TryAutoLoadAnims()
        {
            MeshDocument? doc = _doc;
            if (doc == null || doc.AnimSource != null)
            {
                return;
            }
            string? path = doc.SiblingAnimsPath;
            if (string.IsNullOrEmpty(path) || _animAutoLoadedFor == path)
            {
                return;
            }
            _animAutoLoadedFor = path;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    var loaded = RaxicoreEditor.Editor.Documents.MeshDocument.LoadAnimsCached(path);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_doc, doc) && doc.AnimSource == null)
                        {
                            doc.SetAnimSource(loaded);
                        }
                    });
                }
                catch { /* missing/locked anims.ubr — leave the manual button */ }
            });
        }

        private void SkinAndUpload(float time)
        {
            if (_renderer == null || _animator == null || _part == null || _doc?.ActiveClip == null)
            {
                return;
            }
            Matrix4x4[] skins = _animator.Sample(_doc.ActiveClip, time);
            int boneCount = skins.Length;
            foreach (int i in _skinnedIndices)
            {
                MeshSubmesh sm = _part.Submeshes[i];
                float[] src = sm.Vertices;
                float[]? dst = _skinBuffers[i];
                if (dst == null || sm.BoneA == null || sm.BoneB == null || sm.Weight == null)
                {
                    continue;
                }
                int vcount = src.Length / 8;
                for (int v = 0; v < vcount; v++)
                {
                    int o = v * 8;
                    int ba = sm.BoneA[v];
                    int bb = sm.BoneB[v];
                    float w = sm.Weight[v];
                    // 2-bone LBS. The packed weight is BoneA's weight (verified against the shipped
                    // skinned models — e.g. flag verts encode boneA=<flag bone>, boneB=root, w=1, i.e.
                    // 100% the flag bone), so it lerps boneB→boneA by w. Out-of-range bone (255) = static.
                    Matrix4x4 m = (ba < boneCount && bb < boneCount)
                        ? Matrix4x4.Lerp(skins[bb], skins[ba], w)
                        : Matrix4x4.Identity;
                    var p = new Vector3(src[o], src[o + 1], src[o + 2]);
                    var n = new Vector3(src[o + 3], src[o + 4], src[o + 5]);
                    Vector3 p2 = Vector3.Transform(p, m);
                    Vector3 n2 = Vector3.TransformNormal(n, m);
                    float nl = n2.Length();
                    if (nl > 1e-6f) n2 /= nl;
                    dst[o] = p2.X; dst[o + 1] = p2.Y; dst[o + 2] = p2.Z;
                    dst[o + 3] = n2.X; dst[o + 4] = n2.Y; dst[o + 5] = n2.Z;
                    dst[o + 6] = src[o + 6]; dst[o + 7] = src[o + 7];
                }
                _renderer.UpdateSubmeshVertices(i, dst);
            }
        }

        // Bright, consistent color for every bone segment — high-contrast against most model textures.
        private static readonly Vector3 BoneLineColor = new(0.15f, 1f, 0.35f);

        /// <summary>
        /// Rebuild and upload the skeleton-overlay line list for the current frame: one segment per
        /// non-root bone, from its own joint position to its parent's — in whatever pose the mesh is
        /// currently showing (bind pose, or the active clip at the current playback time), so the
        /// overlay always matches what's actually posed on screen.
        /// </summary>
        private void UpdateSkeletonLines()
        {
            if (_renderer == null)
            {
                return;
            }
            if (_animator == null || _doc is not { ShowSkeleton: true } || _part?.Skeleton == null)
            {
                _renderer.ClearSkeletonLines();
                return;
            }

            var bones = _part.Skeleton.Bones;
            Matrix4x4[] boneWorld = _animator.SampleBoneWorldsViewSpace(_doc.ActiveClip, _doc.AnimTime);

            var verts = new float[bones.Count * 2 * 6]; // up to 2 vertices * 6 floats per non-root bone
            int o = 0;
            for (int i = 0; i < bones.Count; i++)
            {
                int p = bones[i].Parent;
                if (p < 0 || p >= bones.Count)
                {
                    continue; // root bone — no parent segment to draw
                }
                Vector3 childPos = boneWorld[i].Translation;
                Vector3 parentPos = boneWorld[p].Translation;
                o = WriteVertex(verts, o, parentPos, BoneLineColor);
                o = WriteVertex(verts, o, childPos, BoneLineColor);
            }

            if (o < verts.Length)
            {
                Array.Resize(ref verts, o);
            }
            _renderer.SetSkeletonLines(verts);
            _needsRender = true;
        }

        private static int WriteVertex(float[] dst, int o, Vector3 pos, Vector3 color)
        {
            dst[o++] = pos.X; dst[o++] = pos.Y; dst[o++] = pos.Z;
            dst[o++] = color.X; dst[o++] = color.Y; dst[o++] = color.Z;
            return o;
        }

        private void Tick()
        {
            if (_renderer == null)
            {
                return;
            }

            // Advance + skin while playing.
            bool animating = _doc is { IsPlaying: true } && _animator != null
                             && _doc.ActiveClip != null && _skinnedIndices.Length > 0;
            double now = _clock.Elapsed.TotalSeconds;
            double dt = Math.Clamp(now - _lastSec, 0, 0.1);
            _lastSec = now;
            if (animating)
            {
                float dur = _doc!.ActiveClip!.Duration > 0.01f ? _doc.ActiveClip.Duration : 1f;
                float t = _doc.AnimTime + (float)dt;
                if (t > dur) t %= dur;
                _doc.AnimTime = t;
                SkinAndUpload(t);
                _needsRender = true;
            }

            UpdateSkeletonLines();

            if (!_needsRender)
            {
                return;
            }
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            int pw = Math.Max(1, (int)(Bounds.Width * scaling));
            int ph = Math.Max(1, (int)(Bounds.Height * scaling));
            if (pw <= 1 || ph <= 1)
            {
                return;
            }

            if (pw != _pw || ph != _ph || _bitmap == null)
            {
                _pw = pw;
                _ph = ph;
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(pw, ph), new Avalonia.Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
                _pixels = new byte[pw * ph * 4];
                _renderer.Resize(pw, ph);
            }

            float aspect = (float)pw / ph;
            Matrix4x4 mvp = _camera.View * _camera.Projection(aspect); // model = identity
            if (_renderer.Render(mvp, Matrix4x4.Identity, _pixels))
            {
                WritePixels();
                _needsRender = false;
                InvalidateVisual();
            }
        }

        private unsafe void WritePixels()
        {
            if (_bitmap == null)
            {
                return;
            }
            using ILockedFramebuffer fb = _bitmap.Lock();
            int rowBytes = fb.RowBytes;
            int srcRow = _pw * 4;
            byte* dst = (byte*)fb.Address;
            fixed (byte* s = _pixels)
            {
                for (int y = 0; y < _ph; y++)
                {
                    System.Buffer.MemoryCopy(s + (long)y * srcRow, dst + (long)y * rowBytes, rowBytes, srcRow);
                }
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_bitmap != null)
            {
                context.DrawImage(_bitmap, new Rect(Bounds.Size));
            }
        }

        // ---- camera input ----------------------------------------------------------------------

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            PointerPoint p = e.GetCurrentPoint(this);
            _lastPos = p.Position;
            _orbit = p.Properties.IsLeftButtonPressed;
            _pan = p.Properties.IsRightButtonPressed || p.Properties.IsMiddleButtonPressed;
            e.Pointer.Capture(this);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (!_orbit && !_pan)
            {
                return;
            }
            Point pos = e.GetCurrentPoint(this).Position;
            float dx = (float)(pos.X - _lastPos.X);
            float dy = (float)(pos.Y - _lastPos.Y);
            _lastPos = pos;
            if (_orbit)
            {
                _camera.Orbit(-dx * 0.01f, -dy * 0.01f);
            }
            else
            {
                _camera.Pan(dx, dy);
            }
            _needsRender = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _orbit = false;
            _pan = false;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            _camera.Dolly((float)e.Delta.Y);
            _needsRender = true;
        }
    }
}
