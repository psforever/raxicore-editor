using System;
using System.Collections.Generic;
using System.Text;

namespace RaxicoreEditor.EngineAssets.Databases
{
    /// <summary>
    /// Parser for the structured <c>game_objects.adb</c> object/item registry — faithful port of
    /// the engine-derived reference implementation's <c>ascii_database.{h,cpp}</c>. Decodes the chunky/asciidatabase
    /// wrapper, then the <c>add_resource</c> string pool + name index + command stream into a list of
    /// object-class definitions. The object order (first appearance) is the wire <c>class_id</c>; a later
    /// redefinition of the same name overrides in place (last-wins).
    /// </summary>
    public sealed class GameObjectDb
    {
        public sealed class GameObject
        {
            public int ClassId { get; init; }
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public Dictionary<string, List<string>> Properties { get; init; } = new();
        }

        public IReadOnlyList<GameObject> Objects => _objects;
        public uint NameIndexCount { get; private set; }

        private readonly List<GameObject> _objects = new();

        public static bool IsChunky(ReadOnlySpan<byte> d) =>
            d.Length >= 6 && d[0] == 'c' && d[1] == 'h' && d[2] == 'u' && d[3] == 'n' && d[4] == 'k' && d[5] == 'y';

        /// <summary>True if this looks like a game_objects registry (chunky + an add_resource container).</summary>
        public static bool IsGameObjects(byte[] d)
        {
            if (!IsChunky(d)) return false;
            return FindBytes(d, "add_resource", 0) >= 0;
        }

        public static GameObjectDb Parse(byte[] data)
        {
            var db = new GameObjectDb();
            if (!db.ParseInternal(data))
            {
                throw new InvalidOperationException("Not a parseable game_objects.adb registry");
            }
            return db;
        }

        private bool ParseInternal(byte[] d)
        {
            int len = d.Length;
            if (!IsChunky(d)) return false;

            int adb = FindBytes(d, "asciidatabase", 0);
            if (adb < 0) return false;

            // o = root tag(13) + NUL(1) + 4 header bytes + u32 payloadSize(4)
            int o = adb + 13 + 1 + 4 + 4;

            const int addResLen = 12; // "add_resource"
            if ((long)o + addResLen > len) return false;
            if (FindBytes(d, "add_resource", o) != o) return false; // must be exactly here
            o += addResLen + 1; // tag + NUL
            if ((long)o + 4 > len) return false;
            uint poolSize = ReadU32(d, o); o += 4;
            int poolBase = o;
            long poolEnd = (long)poolBase + poolSize;
            if (poolEnd > len) return false;

            // (A) name index: [u32 count][u32 reserved][u32 version][count*(u32 nameOff, recOff)]
            int p = (int)poolEnd;
            if ((long)p + 12 > len) return false;
            NameIndexCount = ReadU32(d, p); p += 4;
            p += 8; // reserved + version
            if ((long)p + (long)NameIndexCount * 8 > len) return false;
            p += (int)NameIndexCount * 8;

            var indexOf = new Dictionary<string, int>(StringComparer.Ordinal);
            bool haveCur = false;
            string curName = "";
            var curProps = new Dictionary<string, List<string>>(StringComparer.Ordinal);

            // (B) command stream: flat nodes {u32 argc; argc*u32 poolOff}; argc==0 separates objects.
            while ((long)p + 4 <= len)
            {
                uint argc = ReadU32(d, p);
                if (argc == 0) { p += 4; continue; }
                if (argc > 64 || (long)p + 4 + (long)argc * 4 > len) break;

                var fields = new uint[argc];
                bool inPool = true;
                for (uint i = 0; i < argc; i++)
                {
                    uint f = ReadU32(d, p + 4 + (int)i * 4);
                    if (f >= poolSize) { inPool = false; break; }
                    fields[i] = f;
                }
                if (!inPool) break;

                string cmd = Str(d, poolBase, poolSize, fields[0]);
                if (cmd == "add_property")
                {
                    string name = Str(d, poolBase, poolSize, fields[1]);
                    if (!haveCur || name != curName)
                    {
                        if (haveCur) Flush(curName, curProps, indexOf);
                        haveCur = true;
                        curName = name;
                        curProps = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                    }
                    if (argc >= 3)
                    {
                        string key = Str(d, poolBase, poolSize, fields[2]);
                        var vals = new List<string>();
                        for (uint i = 3; i < argc; i++) vals.Add(Str(d, poolBase, poolSize, fields[i]));
                        curProps[key] = vals;
                    }
                }
                else if (cmd == "set_resource_parent")
                {
                    // child/parent link; introduces no new object.
                }
                else
                {
                    break; // unknown command → end of stream
                }
                p += 4 + (int)argc * 4;
            }
            if (haveCur) Flush(curName, curProps, indexOf);
            return true;
        }

        private void Flush(string name, Dictionary<string, List<string>> props, Dictionary<string, int> indexOf)
        {
            string type = props.TryGetValue("type", out List<string>? tv) && tv.Count > 0 ? tv[0] : "";
            if (indexOf.TryGetValue(name, out int slot))
            {
                _objects[slot] = new GameObject { ClassId = slot, Name = name, Type = type, Properties = props };
            }
            else
            {
                int id = _objects.Count;
                indexOf[name] = id;
                _objects.Add(new GameObject { ClassId = id, Name = name, Type = type, Properties = props });
            }
        }

        private static string Str(byte[] d, int poolBase, uint poolSize, uint rel)
        {
            int a = poolBase + (int)rel;
            int limit = poolBase + (int)poolSize;
            int end = a;
            while (end < limit && d[end] != 0) end++;
            return Encoding.Latin1.GetString(d, a, end - a);
        }

        private static uint ReadU32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static int FindBytes(byte[] hay, string needle, int from)
        {
            int n = needle.Length;
            for (int i = from; i + n <= hay.Length; i++)
            {
                bool ok = true;
                for (int k = 0; k < n; k++)
                {
                    if (hay[i + k] != (byte)needle[k]) { ok = false; break; }
                }
                if (ok) return i;
            }
            return -1;
        }
    }
}
