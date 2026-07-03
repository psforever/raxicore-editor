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
        private float _near = 0.05f;
        private float _far = 5000f;

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
            _distance = radius / MathF.Tan(_fovY * 0.5f) * 1.4f;
            _far = MathF.Max(5000f, _distance * 10f);
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
            float f = 1f / MathF.Tan(_fovY * 0.5f);
            // Row-vector convention (point * matrix), uploaded directly to a column-major GLSL mat4.
            var p = new Matrix4x4(
                f / aspect, 0, 0, 0,
                0, -f, 0, 0,
                0, 0, _far / (_near - _far), -1,
                0, 0, (_near * _far) / (_near - _far), 0);
            return p;
        }
    }
}
