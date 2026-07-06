using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Maps
{
    /// <summary>
    /// One sub-object of a composite model, read from a <c>pse_relativeobject</c> line: a record name
    /// plus a placement (translation, per-axis scale, yaw about the up axis) expressed in the parent
    /// object's own local frame.
    /// </summary>
    public readonly record struct RelativeObject(string Name, Vector3 Position, Vector3 Scale, float Yaw);

    /// <summary>
    /// A composite-object definition — a plain-text <c>.lst</c> (packed in <c>maps/map_resources.pak</c>)
    /// whose <c>pse_relativeobject</c> lines list the child records that make up a larger structure and
    /// exactly where each one sits relative to the parent. The warpgate is the canonical case: its flat
    /// pad is placed by <c>map_objects</c>, and <c>warpgate_1.lst</c> assembles the three standing arches
    /// on top of it (each arch = seven <c>wg_arm_piece*</c> records, listed three times at 0° / +120° / −120°).
    /// <para>
    /// Line grammar (whitespace-separated):
    /// <c>pse_relativeobject &lt;name&gt; posX posY posZ scaleX scaleY scaleZ pitch roll yaw</c>.
    /// <c>pitch</c>/<c>roll</c> are 0 in every shipped file; <c>yaw</c> uses the same 16384-units-per-turn
    /// integer heading as the <c>map_objects</c> records.
    /// </para>
    /// </summary>
    public sealed class RelativeObjectList
    {
        private const float TurnUnits = 16384f; // heading units per full turn (2^14), matching map_objects

        public IReadOnlyList<RelativeObject> Objects => _objects;
        private readonly List<RelativeObject> _objects = new();

        public static RelativeObjectList Parse(byte[] data)
        {
            var list = new RelativeObjectList();
            string text = Encoding.ASCII.GetString(data);
            foreach (string raw in text.Split('\n'))
            {
                string[] t = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 11 || !t[0].Equals("pse_relativeobject", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!TryF(t[2], out float px) || !TryF(t[3], out float py) || !TryF(t[4], out float pz) ||
                    !TryF(t[5], out float sx) || !TryF(t[6], out float sy) || !TryF(t[7], out float sz) ||
                    !TryF(t[10], out float yawRaw))
                {
                    continue;
                }
                float yaw = yawRaw / TurnUnits * MathF.Tau;
                list._objects.Add(new RelativeObject(t[1], new Vector3(px, py, pz), new Vector3(sx, sy, sz), yaw));
            }
            return list;
        }

        private static bool TryF(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
