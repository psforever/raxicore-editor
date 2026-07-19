using System;
using System.Collections.Generic;
using System.Numerics;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Editor.Rendering
{
    /// <summary>
    /// Computes per-bone skinning matrices for a skeleton driven by an ANIM clip. Works in the viewport's
    /// view-space: bones/clips are engine-derived Z-up, so the native skinning matrix is conjugated by the
    /// Z-up→Y-up rotation V (skinViewSpace = V⁻¹ · skinNative · V), letting it be applied directly to the
    /// view-space mesh vertices.
    ///
    /// All matrices are System.Numerics row-vector form (p' = p · M). A bone's local transform is
    /// R·T (rotate then translate); world = local · parentWorld; skin = invBindWorld · animWorld.
    /// </summary>
    public sealed class SkeletalAnimator
    {
        private static readonly Matrix4x4 V = Matrix4x4.CreateRotationX(-MathF.PI / 2f);    // (x,y,z)→(x,z,-y)
        private static readonly Matrix4x4 Vinv = Matrix4x4.CreateRotationX(MathF.PI / 2f);

        private readonly UberModel.Skeleton _skel;
        private readonly Matrix4x4[] _bindLocal;
        private readonly Matrix4x4[] _invBindWorld;
        private readonly Matrix4x4[] _skinView;       // reused output buffer
        private readonly Dictionary<string, int> _boneByName;

        public SkeletalAnimator(UberModel.Skeleton skel)
        {
            _skel = skel;
            int n = skel.Bones.Count;
            _bindLocal = new Matrix4x4[n];
            _invBindWorld = new Matrix4x4[n];
            _skinView = new Matrix4x4[n];
            _animWorldScratch = new Matrix4x4[n];
            _boneWorldView = new Matrix4x4[n];
            _boneByName = new Dictionary<string, int>(n, StringComparer.OrdinalIgnoreCase);

            var bindWorld = new Matrix4x4[n];
            for (int i = 0; i < n; i++)
            {
                UberModel.Bone b = skel.Bones[i];
                _bindLocal[i] = Local(b.Position, b.Rotation);
                if (!_boneByName.ContainsKey(b.Name)) _boneByName[b.Name] = i;
            }
            for (int i = 0; i < n; i++)
            {
                int p = skel.Bones[i].Parent;
                bindWorld[i] = (p >= 0 && p < n) ? _bindLocal[i] * bindWorld[p] : _bindLocal[i];
                if (!Matrix4x4.Invert(bindWorld[i], out _invBindWorld[i]))
                {
                    _invBindWorld[i] = Matrix4x4.Identity;
                }
            }
        }

        public int BoneCount => _skel.Bones.Count;

        /// <summary>Index of a bone by name, or -1 if this skeleton has no such bone.</summary>
        public int BoneIndex(string name) => _boneByName.TryGetValue(name, out int i) ? i : -1;

        private readonly Matrix4x4[] _animWorldScratch;
        private readonly Matrix4x4[] _boneWorldView; // reused output buffer for SampleBoneWorldsViewSpace

        /// <summary>
        /// Sample the clip at <paramref name="t"/> seconds and return view-space skinning matrices (one
        /// per bone). When <paramref name="clip"/> is null, returns bind-pose (all identity). The returned
        /// array is reused between calls — copy if you need to retain it.
        /// </summary>
        public Matrix4x4[] Sample(AnimRecord? clip, float t)
        {
            Matrix4x4[] animWorld = ComputeAnimWorld(clip, t);
            int n = animWorld.Length;
            for (int i = 0; i < n; i++)
            {
                Matrix4x4 skinNative = _invBindWorld[i] * animWorld[i];
                _skinView[i] = Vinv * skinNative * V;
            }
            return _skinView;
        }

        /// <summary>
        /// Sample the clip and return each bone's own view-space WORLD transform (not a skin delta) — the
        /// bone's actual posed position/orientation, for drawing a skeleton overlay. <c>.Translation</c> on
        /// the result is the joint position. Reused array between calls, same rules as <see cref="Sample"/>.
        /// </summary>
        public Matrix4x4[] SampleBoneWorldsViewSpace(AnimRecord? clip, float t)
        {
            Matrix4x4[] animWorld = ComputeAnimWorld(clip, t);
            int n = animWorld.Length;
            for (int i = 0; i < n; i++)
            {
                _boneWorldView[i] = Vinv * animWorld[i] * V;
            }
            return _boneWorldView;
        }

        // Forward-kinematics compose: per-bone local transform (animated if a track exists, else bind)
        // composed with the parent's already-computed world transform. Native (Z-up) space.
        private Matrix4x4[] ComputeAnimWorld(AnimRecord? clip, float t)
        {
            int n = _skel.Bones.Count;
            for (int i = 0; i < n; i++)
            {
                UberModel.Bone bone = _skel.Bones[i];
                Matrix4x4 local = _bindLocal[i];
                AnimTrack? tr = clip != null ? FindTrack(clip, bone.Name) : null;
                if (tr != null)
                {
                    local = Local(tr.SamplePosition(t), tr.SampleRotation(t));
                }
                int p = bone.Parent;
                _animWorldScratch[i] = (p >= 0 && p < n) ? local * _animWorldScratch[p] : local;
            }
            return _animWorldScratch;
        }

        private static AnimTrack? FindTrack(AnimRecord clip, string boneName)
        {
            foreach (AnimTrack tk in clip.Tracks)
            {
                if (string.Equals(tk.Name, boneName, StringComparison.OrdinalIgnoreCase))
                {
                    return tk;
                }
            }
            return null;
        }

        private static Matrix4x4 Local(Vector3 pos, Quaternion rot)
        {
            // Row-vector: rotate then translate → R · T.
            return Matrix4x4.CreateFromQuaternion(rot) * Matrix4x4.CreateTranslation(pos);
        }
    }
}
