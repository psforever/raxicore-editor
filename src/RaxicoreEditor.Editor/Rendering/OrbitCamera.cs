using System;
using System.Numerics;

namespace RaxicoreEditor.Editor.Rendering
{
    /// <summary>Orbit camera producing Vulkan-correct view/projection matrices (System.Numerics).</summary>
    public sealed class OrbitCamera
    {
        private Vector3 _target = Vector3.Zero;
        private float _yaw = 0.7f;
        private float _pitch = 0.5f;
        private float _distance = 10f;
        private float _fovY = MathF.PI / 180f * 50f;
        private float _radius = 10f; // scene radius from the last Frame(); drives adaptive near/far

        public void Orbit(float dYaw, float dPitch)
        {
            _yaw += dYaw;
            _pitch = Math.Clamp(_pitch + dPitch, -1.55f, 1.55f);
        }

        public void Dolly(float delta)
        {
            _distance = Math.Clamp(_distance * MathF.Exp(-delta * 0.15f), 0.01f, 50000f);
        }

        public void Pan(float dx, float dy)
        {
            Vector3 forward = Vector3.Normalize(_target - Position);
            Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
            Vector3 up = Vector3.Normalize(Vector3.Cross(right, forward));
            float scale = _distance * 0.0015f;
            _target += (-dx * right + dy * up) * scale;
        }

        public void Frame(Vector3 min, Vector3 max)
        {
            _target = (min + max) * 0.5f;
            Vector3 ext = max - min;
            float radius = 0.5f * ext.Length();
            if (radius < 1e-4f)
            {
                radius = 1f;
            }
            _radius = radius;
            _distance = radius / MathF.Tan(_fovY * 0.5f) * 1.4f;
        }

        public Vector3 Position
        {
            get
            {
                var dir = new Vector3(
                    MathF.Cos(_pitch) * MathF.Sin(_yaw),
                    MathF.Sin(_pitch),
                    MathF.Cos(_pitch) * MathF.Cos(_yaw));
                return _target + dir * _distance;
            }
        }

        public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, _target, Vector3.UnitY);

        /// <summary>Right-handed perspective for Vulkan clip space (y-down, depth [0,1]).</summary>
        public Matrix4x4 Projection(float aspect)
        {
            if (aspect < 0.001f)
            {
                aspect = 1f;
            }
            // Adaptive near/far hugging the framed scene, rather than a fixed 0.05 near with a
            // distance×10 far. The old wide range left almost no depth precision at continent distances,
            // so the coplanar water sheet and seabed z-fought badly. Keeping near a healthy fraction of
            // the view distance (and far just past the scene) restores precision at every zoom level.
            float far = _distance + _radius * 1.5f + 1f;
            float near = MathF.Max(0.02f, (_distance - _radius * 1.5f) * 0.5f);
            if (near >= far) near = far * 0.001f;

            float f = 1f / MathF.Tan(_fovY * 0.5f);
            // Reversed-Z depth (near→1, far→0): pairs with a 0.0 depth clear and a Greater compare in
            // the pipelines. Float depth then keeps its fine precision out at the far plane instead of
            // wasting it near the camera, which is what lets the coplanar continent water/seabed and
            // distant terrain resolve cleanly. Row-vector convention (point * matrix), uploaded directly
            // to a column-major GLSL mat4.
            var p = new Matrix4x4(
                f / aspect, 0, 0, 0,
                0, -f, 0, 0,
                0, 0, near / (far - near), -1,
                0, 0, (near * far) / (far - near), 0);
            return p;
        }
    }
}
