using System;
using System.Collections.Generic;
using System.Numerics;

namespace RaxicoreEditor.EngineAssets.Meshes
{
    /// <summary>Pure, skeleton-independent analysis of an <see cref="AnimRecord"/> clip, for the animation
    /// evaluation views (metadata/health, dope sheet, export). Everything here works from the clip's own
    /// track keyframes — no posing/forward-kinematics required.</summary>
    public static class AnimClipAnalysis
    {
        /// <summary>Estimate a clip's frame rate from the spacing of its distinct keyframe times: the
        /// reciprocal of the median gap. Returns 0 if there aren't enough keys to tell.</summary>
        public static float InferredFps(IReadOnlyList<float> keyTimes)
        {
            if (keyTimes.Count < 2)
            {
                return 0f;
            }
            var gaps = new List<float>(keyTimes.Count - 1);
            for (int i = 1; i < keyTimes.Count; i++)
            {
                float g = keyTimes[i] - keyTimes[i - 1];
                if (g > 1e-5f) gaps.Add(g);
            }
            if (gaps.Count == 0)
            {
                return 0f;
            }
            gaps.Sort();
            float median = gaps[gaps.Count / 2];
            return median > 1e-5f ? 1f / median : 0f;
        }

        /// <summary>How many of the clip's tracks actually move (carry position or rotation keys) versus
        /// being a single static transform.</summary>
        public static int AnimatedBoneCount(AnimRecord clip)
        {
            int n = 0;
            foreach (AnimTrack tk in clip.Tracks)
            {
                if (tk.PosKeys.Count > 0 || tk.RotKeys.Count > 0) n++;
            }
            return n;
        }

        /// <summary>How closely the clip loops: the worst-case difference between each track's start pose and
        /// its end pose. <paramref name="posDelta"/> is the largest positional gap (world units) and
        /// <paramref name="rotDegrees"/> the largest rotational gap (degrees). Both near zero means the clip
        /// returns to its starting pose and loops seamlessly.</summary>
        public static void LoopSeam(AnimRecord clip, out float posDelta, out float rotDegrees)
        {
            posDelta = 0f;
            rotDegrees = 0f;
            float end = clip.Duration;
            foreach (AnimTrack tk in clip.Tracks)
            {
                float p = (tk.SamplePosition(0f) - tk.SamplePosition(end)).Length();
                if (p > posDelta) posDelta = p;

                Quaternion a = Quaternion.Normalize(tk.SampleRotation(0f));
                Quaternion b = Quaternion.Normalize(tk.SampleRotation(end));
                float dot = Math.Clamp(MathF.Abs(Quaternion.Dot(a, b)), 0f, 1f);
                float deg = 2f * MathF.Acos(dot) * (180f / MathF.PI);
                if (deg > rotDegrees) rotDegrees = deg;
            }
        }
    }
}
