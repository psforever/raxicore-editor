using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Maps;

namespace RaxicoreEditor.Generation.Continents
{
    /// <summary>
    /// Extracts bridge decks from a continent's <c>contents_mapNN.mpo</c> object list.
    ///
    /// Bridges ship as long runs of small placed pieces (<c>bridge_start</c> / <c>bridge_mid</c> /
    /// <c>bridge_end</c> / <c>bridge_extend</c>, ~16 world units apart; <c>bridge_support_foot</c>
    /// pillars are ignored). Each piece carries its own position AND heading, so every piece is emitted
    /// as its own short segment oriented along that heading.
    ///
    /// This deliberately does NOT chain piece centres into polylines. Chaining has to assume the object
    /// list is in spatial order, and where it isn't the polyline doubles back — segments end up drawn at
    /// angles the bridge never had. Using each piece's recorded yaw is correct by construction and
    /// independent of list order; consecutive pieces are close enough that the segments still read as one
    /// continuous deck.
    /// </summary>
    internal static class ContinentBridges
    {
        private static readonly HashSet<string> Deck = new(StringComparer.OrdinalIgnoreCase)
        {
            "bridge_start", "bridge_mid", "bridge_end", "bridge_extend"
        };

        /// <summary>
        /// Half-length of each drawn deck segment, in world units. Pieces sit ~16 units apart, so ~10
        /// (a 20-unit segment) leaves them slightly overlapping and the run reads as unbroken.
        /// </summary>
        private const float HalfSegment = 10f;

        /// <summary>Returns one 2-point segment per deck piece: [[x1,y1],[x2,y2]] in world coords.</summary>
        public static List<List<float[]>> Build(string resourcesPak, string baseName)
        {
            var segments = new List<List<float[]>>();
            MpoFile mpo;
            try
            {
                var pak = PakArchive.Load(File.ReadAllBytes(resourcesPak));
                int idx = pak.IndexOf("contents_" + baseName + ".mpo");
                if (idx < 0) return segments;
                mpo = MpoFile.Parse(pak.Extract(idx));
            }
            catch { return segments; }

            foreach (var o in mpo.Objects)
            {
                if (!Deck.Contains(o.Name)) continue;
                // Yaw is radians about +Z; the deck runs along (cos, sin) -- verified against the shipped
                // data (Cyssor's yaw 1.52 pieces step in +Y, and cos/sin of 1.52 is ~(0.05, 1.00)).
                float dx = MathF.Cos(o.Yaw) * HalfSegment;
                float dy = MathF.Sin(o.Yaw) * HalfSegment;
                segments.Add(new List<float[]>
                {
                    new[] { o.Position.X - dx, o.Position.Y - dy },
                    new[] { o.Position.X + dx, o.Position.Y + dy }
                });
            }
            return segments;
        }
    }
}
