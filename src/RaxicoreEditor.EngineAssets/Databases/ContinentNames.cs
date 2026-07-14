using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using RaxicoreEditor.EngineAssets.Archives;

namespace RaxicoreEditor.EngineAssets.Databases
{
    /// <summary>
    /// Maps a continent file stem (<c>map03</c>) to its display name (<c>Cyssor</c>), read straight from
    /// the client's own gamestring table — <c>english.str</c> inside <c>startup.pak</c>. The relevant keys
    /// are <c>@xp_&lt;stem&gt;_label = "&lt;Category&gt;: &lt;Name&gt;"</c> (e.g. <c>@xp_map03_label=Map: Cyssor</c>,
    /// <c>@xp_map14_label=Training: VR Shooting Range</c>); the name is the text after the "<c>: </c>".
    /// </summary>
    public sealed class ContinentNames
    {
        private readonly Dictionary<string, string> _byStem = new(StringComparer.OrdinalIgnoreCase);

        public int Count => _byStem.Count;

        /// <summary>The continent name for a file stem (<c>map03</c> → <c>Cyssor</c>), or null if unknown.</summary>
        public string? Name(string stem) => _byStem.TryGetValue(stem, out string? n) ? n : null;

        /// <summary>Load names from <c>startup.pak → english.str</c> under <paramref name="assetDir"/>.
        /// Returns null if the archive/entry isn't present or can't be read (callers fall back to raw names).</summary>
        public static ContinentNames? TryLoad(string? assetDir)
        {
            if (string.IsNullOrEmpty(assetDir))
            {
                return null;
            }
            try
            {
                string pakPath = Path.Combine(assetDir, "startup.pak");
                if (!File.Exists(pakPath))
                {
                    return null;
                }
                PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                byte[]? strData = null;
                foreach (PakEntry e in pak.Entries)
                {
                    if (e.Name.EndsWith("english.str", StringComparison.OrdinalIgnoreCase))
                    {
                        strData = pak.Extract(e.Name);
                        break;
                    }
                }
                if (strData == null)
                {
                    return null;
                }
                var cn = new ContinentNames();
                cn.Parse(strData);
                return cn.Count > 0 ? cn : null;
            }
            catch
            {
                return null;
            }
        }

        private void Parse(byte[] data)
        {
            // "@xp_<stem>_label=<Category>: <Name>" per line; plus "@cN=<Name>" for the Core Combat caverns.
            string text = Encoding.UTF8.GetString(data);
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                // Cavern zones: "@cN=<name>" (c1=Supai … c6=Drugaskan) names the terrain file ugd0N.
                if (line.Length > 4 && line[0] == '@' && line[1] == 'c' && char.IsDigit(line[2]) && line[3] == '=')
                {
                    string cname = line.Substring(4).Trim();
                    if (cname.Length > 0)
                    {
                        _byStem["ugd0" + line[2]] = cname;
                    }
                    continue;
                }
                if (!line.StartsWith("@xp_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0)
                {
                    continue;
                }
                string key = line.Substring(0, eq).Trim();          // @xp_map03_label
                if (!key.EndsWith("_label", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                // stem = the text between "@xp_" and "_label"
                int stemLen = key.Length - 4 - "_label".Length;
                if (stemLen <= 0)
                {
                    continue;
                }
                string stem = key.Substring(4, stemLen);            // map03
                string value = line.Substring(eq + 1).Trim();       // "Map: Cyssor"
                int colon = value.IndexOf(": ", StringComparison.Ordinal);
                string name = colon >= 0 ? value.Substring(colon + 2).Trim() : value;
                if (name.Length > 0)
                {
                    _byStem[stem] = name;
                }
            }
        }
    }
}
