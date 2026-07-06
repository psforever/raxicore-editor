using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Maps
{
    /// <summary>
    /// One absolutely-placed scene object from a <c>pse_exactobject</c> line: a record name plus a world
    /// position, per-axis scale and yaw. Unlike <see cref="RelativeObject"/> (relative to a parent), these
    /// coordinates are already in the continent's own world frame — the same frame as the
    /// <c>map_objects</c> records.
    /// </summary>
    public readonly record struct ExactObject(string Name, Vector3 Position, Vector3 Scale, float Yaw);

    /// <summary>
    /// A <c>groundcover_mapNN.lst</c> — the continent's flora-and-scatter manifest (packed beside
    /// <c>contents_mapNN.mpo</c>). Its <c>pse_exactobject</c> lines place trees, rocks and a handful of
    /// built structures (e.g. the battle-island <c>warpgate_small</c>/<c>hst</c> gates and the sanctuary
    /// BFR buildings and repair silos) that are <b>not</b> in <c>map_objects</c>.
    /// <para>
    /// Line grammar (whitespace-separated):
    /// <c>pse_exactobject &lt;name&gt; &lt;id&gt; posX posY posZ scaleX scaleY scaleZ pitch roll yaw ["label"]</c>.
    /// <c>pitch</c>/<c>roll</c> are 0 in every shipped file; <c>yaw</c> uses the same 16384-units-per-turn
    /// integer heading as the <c>map_objects</c> records; the trailing quoted label (present on gates) is
    /// ignored.
    /// </para>
    /// </summary>
    public sealed class ExactObjectList
    {
        private const float TurnUnits = 16384f; // heading units per full turn (2^14), matching map_objects

        public IReadOnlyList<ExactObject> Objects => _objects;
        private readonly List<ExactObject> _objects = new();

        public static ExactObjectList Parse(byte[] data)
        {
            var list = new ExactObjectList();
            string text = Encoding.ASCII.GetString(data);
            foreach (string raw in text.Split('\n'))
            {
                string[] t = raw.Split(new[] { ' ', '\t', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                // keyword name id X Y Z sx sy sz pitch roll yaw  → 12 leading tokens (label optional)
                if (t.Length < 12 || !t[0].Equals("pse_exactobject", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!TryF(t[3], out float px) || !TryF(t[4], out float py) || !TryF(t[5], out float pz) ||
                    !TryF(t[6], out float sx) || !TryF(t[7], out float sy) || !TryF(t[8], out float sz) ||
                    !TryF(t[11], out float yawRaw))
                {
                    continue;
                }
                float yaw = yawRaw / TurnUnits * MathF.Tau;
                list._objects.Add(new ExactObject(t[1], new Vector3(px, py, pz), new Vector3(sx, sy, sz), yaw));
            }
            return list;
        }

        private static bool TryF(string s, out float v) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }
}
