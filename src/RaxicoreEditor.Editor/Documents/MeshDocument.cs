using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using RaxicoreEditor.EngineAssets.Archives;
using RaxicoreEditor.EngineAssets.Maps;
using RaxicoreEditor.EngineAssets.Meshes;
using RaxicoreEditor.EngineAssets.Textures;
using RaxicoreEditor.Editor.Mvvm;

namespace RaxicoreEditor.Editor.Documents
{
    /// <summary>
    /// One material of the selected mesh part in the inspector's texture picker: the material name, the
    /// texture currently bound to it, and which empire variants (NC/TR/VS) it can be swapped to.
    /// </summary>
    public sealed class MaterialSlot : ObservableObject
    {
        public required string Material { get; init; }

        private string? _texture;
        public string? Texture
        {
            get => _texture;
            set { if (SetProperty(ref _texture, value)) RaisePropertyChanged(nameof(Display)); }
        }

        public string Display => string.IsNullOrEmpty(_texture)
            ? $"{Material}  —  (no texture)"
            : $"{Material}  —  {_texture}";

        private bool _nc, _tr, _vs;
        public bool NcEnabled { get => _nc; set => SetProperty(ref _nc, value); }
        public bool TrEnabled { get => _tr; set => SetProperty(ref _tr, value); }
        public bool VsEnabled { get => _vs; set => SetProperty(ref _vs, value); }
    }

    /// <summary>
    /// A per-material draw batch: interleaved vertices (position[3] + normal[3] + uv[2], stride 8 floats),
    /// a triangle index buffer, the source material name, and its resolved texture (BGRA, or null →
    /// rendered untextured/white).
    /// </summary>
    public sealed class MeshSubmesh
    {
        public required float[] Vertices { get; init; } // stride 8: px,py,pz, nx,ny,nz, u,v
        public required uint[] Indices { get; init; }
        public required string Material { get; init; }
        public byte[]? TextureBgra { get; internal set; }
        public int TextureWidth { get; internal set; }
        public int TextureHeight { get; internal set; }
        /// <summary>The resolved texture key (base name, no extension) currently applied, or null.</summary>
        public string? TextureName { get; internal set; }

        /// <summary>True when the resolved texture is a translucent overlay (shield domes, energy
        /// beams, shoreline foam — the engine-derived "_mask_" naming convention) rather than an
        /// opaque/cutout material, so the viewport draws it with real alpha blending instead of a
        /// hard alpha-test discard.</summary>
        public bool IsTranslucent { get; internal set; }

        /// <summary>Swap this submesh's texture (used by the per-material picker / empire swap).</summary>
        public void ApplyTexture(DdsImage? dds, string? name)
        {
            TextureBgra = dds?.Bgra;
            TextureWidth = dds?.Width ?? 0;
            TextureHeight = dds?.Height ?? 0;
            TextureName = dds != null ? name : null;
            IsTranslucent = IsMaskTextureName(dds != null ? name : null);
        }

        /// <summary>Shared "is this a translucent-overlay texture" rule (see <see cref="IsTranslucent"/>),
        /// so every construction path — auto-apply, the picker's manual swap, and instanced continent
        /// scene objects — agrees on it.</summary>
        internal static bool IsMaskTextureName(string? textureKey) =>
            textureKey != null && textureKey.Contains("mask", StringComparison.OrdinalIgnoreCase);

        // Per-vertex skin data (Deform/FatDeform only; null otherwise). Bone indices reference the
        // owning part's skeleton; Weight is the 2-bone blend factor.
        public byte[]? BoneA { get; init; }
        public byte[]? BoneB { get; init; }
        public float[]? Weight { get; init; }

        public int VertexCount => Vertices.Length / 8;
        public int TriangleCount => Indices.Length / 3;
        public bool HasTexture => TextureBgra != null && TextureWidth > 0 && TextureHeight > 0;
        public bool IsSkinned => BoneA != null && BoneB != null && Weight != null;
    }

    /// <summary>
    /// One selectable mesh in a .ubr — a single decoded CMeshSystem (record) or the "All meshes"
    /// aggregate. Holds per-material submeshes so the viewport can render it textured.
    /// </summary>
    public sealed class MeshPart
    {
        public required string Name { get; init; }
        public required IReadOnlyList<MeshSubmesh> Submeshes { get; init; }
        public Vector3 BoundsMin { get; init; }
        public Vector3 BoundsMax { get; init; }
        public bool IsAggregate { get; init; }
        public UberModel.Skeleton? Skeleton { get; init; }

        public bool IsSkinned
        {
            get { foreach (MeshSubmesh s in Submeshes) if (s.IsSkinned) return true; return false; }
        }

        public int TriangleCount
        {
            get { int t = 0; foreach (MeshSubmesh s in Submeshes) t += s.TriangleCount; return t; }
        }

        public int TexturedSubmeshCount
        {
            get { int n = 0; foreach (MeshSubmesh s in Submeshes) if (s.HasTexture) n++; return n; }
        }

        public bool HasGeometry => TriangleCount > 0;
        public Vector3 Center => (BoundsMin + BoundsMax) * 0.5f;
        public string Display => $"{Name}  ·  {TriangleCount} tris";
        public override string ToString() => Display;
    }

    /// <summary>
    /// 3D document (UberMesh). Decodes every CMeshSystem via the faithful <see cref="UberModel"/> pool
    /// parser into selectable <see cref="MeshPart"/>s, each split into per-material <see cref="MeshSubmesh"/>es
    /// with UVs and a resolved DDS texture (looked up from the sibling dds_*.fat archives). Export returns
    /// the original bytes.
    /// </summary>
    public sealed class MeshDocument : DocumentBase
    {
        private readonly byte[] _data;
        private MeshPart? _selectedPart;
        private TextureProvider? _textures;
        private List<MeshSubmesh> _allSubmeshes = new();

        /// <summary>Raised when <see cref="SelectedPart"/> changes so the viewport can re-upload/re-frame.</summary>
        public event Action? GeometryChanged;

        /// <summary>Raised when a texture override is applied so the viewport re-uploads the same geometry.</summary>
        public event Action? TexturesChanged;

        public MeshDocument(string title, string source, byte[] data)
            : base(title, source, DocumentKind.Mesh)
        {
            _data = data;
            string? assetDir = AssetDirOf(source);
            try
            {
                Assemble(data, assetDir);
            }
            catch (Exception ex)
            {
                Summary = "parse failed: " + ex.Message;
            }

            // Continents: after the terrain, place the static scene objects (bases/towers/trees) from
            // the .mpo manifest + uber.ubr so the map looks like the in-game continent, not bare terrain.
            bool isContinent = IsContinentSource(source) && !string.IsNullOrEmpty(assetDir);
            if (isContinent)
            {
                try { AppendContinentObjects(assetDir!, Path.GetFileNameWithoutExtension(BeforeBang(source))); }
                catch (Exception ex) { Summary += " | scene objects failed: " + ex.Message; }
            }

            if (Parts.Count > 0)
            {
                // Continents default to the whole-map aggregate; models default to an animatable part
                // so the animation panel shows immediately on open.
                _selectedPart = isContinent ? Parts[0] : (FirstAnimatablePart() ?? Parts[0]);
                RebuildMaterials();
            }

            // The clip database usually sits beside the model; the viewport auto-loads it (cached).
            if (!string.IsNullOrEmpty(assetDir))
            {
                string cand = Path.Combine(assetDir, "anims.ubr");
                if (File.Exists(cand))
                {
                    SiblingAnimsPath = cand;
                }
            }
        }

        private MeshPart? FirstAnimatablePart()
        {
            foreach (MeshPart p in Parts)
            {
                if (!p.IsAggregate && p.Skeleton != null && p.IsSkinned)
                {
                    return p;
                }
            }
            return null;
        }

        public ObservableCollection<MeshPart> Parts { get; } = new();

        public MeshPart? SelectedPart
        {
            get => _selectedPart;
            set
            {
                if (SetProperty(ref _selectedPart, value))
                {
                    RaisePropertyChanged(nameof(BoundsMin));
                    RaisePropertyChanged(nameof(BoundsMax));
                    RaisePropertyChanged(nameof(HasMesh));
                    RaisePropertyChanged(nameof(PartInfo));
                    RaisePropertyChanged(nameof(CanAnimate));
                    RaisePropertyChanged(nameof(HasSkeleton));
                    RebuildMaterials();
                    if (AnimSource != null) SetAnimSource(AnimSource); // re-filter clips for the new skeleton
                    GeometryChanged?.Invoke();
                }
            }
        }

        public IReadOnlyList<MeshSubmesh> Submeshes => _selectedPart?.Submeshes ?? Array.Empty<MeshSubmesh>();
        public Vector3 BoundsMin => _selectedPart?.BoundsMin ?? Vector3.Zero;
        public Vector3 BoundsMax => _selectedPart?.BoundsMax ?? Vector3.Zero;
        public bool HasMesh => _selectedPart?.HasGeometry ?? false;

        public string Summary { get; private set; } = "";
        public IReadOnlyDictionary<string, int> RefuseBreakdown { get; private set; } =
            new Dictionary<string, int>();

        public string PartInfo
        {
            get
            {
                if (_selectedPart == null)
                {
                    return "";
                }
                MeshPart p = _selectedPart;
                Vector3 c = p.Center;
                Vector3 size = p.BoundsMax - p.BoundsMin;
                return $"{p.TriangleCount} tris · {p.Submeshes.Count} materials ({p.TexturedSubmeshCount} textured)\n" +
                       $"center ({c.X:0.#}, {c.Y:0.#}, {c.Z:0.#})\n" +
                       $"size ({size.X:0.#} × {size.Y:0.#} × {size.Z:0.#})";
            }
        }

        public override byte[] Export() => _data;

        // ---- materials / texture override ------------------------------------------------------

        /// <summary>Distinct materials of the selected part, each with its currently-applied texture and
        /// which empire variants it can swap to. Drives the inspector's texture picker.</summary>
        public ObservableCollection<MaterialSlot> Materials { get; } = new();

        private string[]? _availableTextures;

        /// <summary>All texture keys indexed from the sibling <c>.fat</c> archives (sorted), for the picker.</summary>
        public IReadOnlyList<string> AvailableTextures
        {
            get
            {
                if (_availableTextures != null) return _availableTextures;
                if (_textures == null) return Array.Empty<string>();
                var list = new List<string>(_textures.TextureNames);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                _availableTextures = list.ToArray();
                return _availableTextures;
            }
        }

        /// <summary>True when the selected part has any materials (gates the inspector's texture panel).</summary>
        public bool HasMaterials => Materials.Count > 0;

        private MaterialSlot? _selectedMaterial;
        /// <summary>The material the texture picker targets (bound to the materials list selection).</summary>
        public MaterialSlot? SelectedMaterial
        {
            get => _selectedMaterial;
            set
            {
                if (SetProperty(ref _selectedMaterial, value) && value?.Texture is string tex)
                {
                    TextureFilter = LetterPrefix(tex); // seed the search with the texture's family
                }
            }
        }

        private string _textureFilter = "";
        /// <summary>Filters <see cref="FilteredTextures"/> by substring.</summary>
        public string TextureFilter
        {
            get => _textureFilter;
            set { if (SetProperty(ref _textureFilter, value)) RefreshFilteredTextures(); }
        }

        /// <summary>The picker's current shortlist (filtered + capped). Bound to the texture list.</summary>
        public ObservableCollection<string> FilteredTextures { get; } = new();

        private string? _pickedTexture;
        /// <summary>Selecting a texture here applies it to <see cref="SelectedMaterial"/>.</summary>
        public string? PickedTexture
        {
            get => _pickedTexture;
            set
            {
                if (SetProperty(ref _pickedTexture, value) && value != null && _selectedMaterial != null)
                {
                    ApplyTextureToMaterial(_selectedMaterial.Material, value);
                }
            }
        }

        private void RefreshFilteredTextures()
        {
            FilteredTextures.Clear();
            const int cap = 400; // keep the list responsive; refine via the search box
            foreach (string t in AvailableTextures)
            {
                if (_textureFilter.Length == 0 ||
                    t.Contains(_textureFilter, StringComparison.OrdinalIgnoreCase))
                {
                    FilteredTextures.Add(t);
                    if (FilteredTextures.Count >= cap) break;
                }
            }
        }

        private static string LetterPrefix(string s)
        {
            int i = 0;
            while (i < s.Length && char.IsLetter(s[i])) i++;
            return i > 0 ? s[..i] : s;
        }

        /// <summary>True once at least one empire-coded texture is present, so the whole-model swap is useful.</summary>
        public bool HasEmpireTextures
        {
            get
            {
                foreach (MaterialSlot s in Materials)
                {
                    if (s.NcEnabled || s.TrEnabled || s.VsEnabled) return true;
                }
                return false;
            }
        }

        private void RebuildMaterials()
        {
            Materials.Clear();
            MeshPart? p = _selectedPart;
            if (p != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (MeshSubmesh s in p.Submeshes)
                {
                    if (!seen.Add(s.Material)) continue;
                    var slot = new MaterialSlot { Material = s.Material, Texture = s.TextureName };
                    UpdateEmpireFlags(slot);
                    Materials.Add(slot);
                }
            }
            _selectedMaterial = null;
            RaisePropertyChanged(nameof(SelectedMaterial));
            if (FilteredTextures.Count == 0) RefreshFilteredTextures();
            RaisePropertyChanged(nameof(HasMaterials));
            RaisePropertyChanged(nameof(HasEmpireTextures));
        }

        private void UpdateEmpireFlags(MaterialSlot slot)
        {
            string? cur = slot.Texture;
            slot.NcEnabled = cur != null && _textures?.EmpireVariant(cur, "nc") != null;
            slot.TrEnabled = cur != null && _textures?.EmpireVariant(cur, "tr") != null;
            slot.VsEnabled = cur != null && _textures?.EmpireVariant(cur, "vs") != null;
        }

        // Re-texture every submesh that uses this material (so the change shows in every part), and
        // refresh the matching slot. Returns true if anything changed. Does NOT raise TexturesChanged —
        // callers batch and raise once.
        private bool SetMaterialTextureCore(string material, string textureKey)
        {
            if (_textures == null) return false;
            DdsImage? dds = _textures.Get(textureKey);
            bool any = false;
            foreach (MeshSubmesh s in _allSubmeshes)
            {
                if (string.Equals(s.Material, material, StringComparison.OrdinalIgnoreCase))
                {
                    s.ApplyTexture(dds, textureKey);
                    any = true;
                }
            }
            foreach (MaterialSlot slot in Materials)
            {
                if (string.Equals(slot.Material, material, StringComparison.OrdinalIgnoreCase))
                {
                    slot.Texture = dds != null ? textureKey : null;
                    UpdateEmpireFlags(slot);
                }
            }
            return any;
        }

        /// <summary>Apply a chosen texture (by index key) to one material across the whole model.</summary>
        public void ApplyTextureToMaterial(string material, string textureKey)
        {
            if (SetMaterialTextureCore(material, textureKey))
            {
                RaisePropertyChanged(nameof(HasEmpireTextures));
                TexturesChanged?.Invoke();
            }
        }

        /// <summary>Swap one material to its NC/TR/VS variant (no-op if that empire's texture isn't indexed).</summary>
        public void ApplyEmpireToMaterial(MaterialSlot slot, string empire)
        {
            if (_textures == null || slot.Texture == null) return;
            string? variant = _textures.EmpireVariant(slot.Texture, empire);
            if (variant != null && SetMaterialTextureCore(slot.Material, variant))
            {
                RaisePropertyChanged(nameof(HasEmpireTextures));
                TexturesChanged?.Invoke();
            }
        }

        /// <summary>Swap the WHOLE model to one empire — the headline "stop showing NC on every model" action.</summary>
        public void ApplyEmpireToModel(string empire)
        {
            if (_textures == null) return;
            bool any = false;
            foreach (MaterialSlot slot in Materials)
            {
                if (slot.Texture == null) continue;
                string? variant = _textures.EmpireVariant(slot.Texture, empire);
                if (variant != null && SetMaterialTextureCore(slot.Material, variant)) any = true;
            }
            if (any)
            {
                RaisePropertyChanged(nameof(HasEmpireTextures));
                TexturesChanged?.Invoke();
            }
        }

        // ---- animation state -------------------------------------------------------------------
        private AnimRecord? _activeClip;
        private bool _isPlaying;

        /// <summary>The loaded anims.ubr (set via the inspector). Clips come from a separate file.</summary>
        public AnimDb? AnimSource { get; private set; }

        /// <summary>Path to the <c>anims.ubr</c> sitting beside this model, if any (for auto-load).</summary>
        public string? SiblingAnimsPath { get; private set; }

        // anims.ubr is ~62 MB; parse once per path and share across documents/this session.
        private static readonly Dictionary<string, AnimDb> AnimCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AnimCacheLock = new();

        /// <summary>Load (and cache) an anims.ubr. Safe to call off the UI thread.</summary>
        public static AnimDb LoadAnimsCached(string path)
        {
            lock (AnimCacheLock)
            {
                if (AnimCache.TryGetValue(path, out AnimDb? cached))
                {
                    return cached;
                }
            }
            AnimDb db = AnimDb.Load(File.ReadAllBytes(path));
            lock (AnimCacheLock)
            {
                AnimCache[path] = db;
            }
            return db;
        }

        /// <summary>Clips from <see cref="AnimSource"/> filtered to those targeting this part's skeleton.</summary>
        public ObservableCollection<AnimRecord> AnimClips { get; } = new();

        /// <summary>Raised when the active clip changes so the viewport can (re)build its animator.</summary>
        public event Action? AnimChanged;

        public bool CanAnimate => _selectedPart?.IsSkinned ?? false;

        /// <summary>
        /// True when the selected part carries a skeleton at all — a broader gate than
        /// <see cref="CanAnimate"/>: most parts with bones are RIGID mesh-on-bone (doors, turrets,
        /// bridges), not vertex-skinned, but still have real bone data worth visualizing.
        /// </summary>
        public bool HasSkeleton => _selectedPart?.Skeleton != null;

        private bool _showSkeleton;
        /// <summary>Toggle the skeleton-overlay line render in the viewport.</summary>
        public bool ShowSkeleton
        {
            get => _showSkeleton;
            set => SetProperty(ref _showSkeleton, value);
        }

        /// <summary>Playback cursor in seconds (advanced by the viewport while playing).</summary>
        public float AnimTime { get; set; }

        public AnimRecord? ActiveClip
        {
            get => _activeClip;
            set
            {
                if (SetProperty(ref _activeClip, value))
                {
                    AnimTime = 0f;
                    AnimChanged?.Invoke();
                }
            }
        }

        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        /// <summary>
        /// Attach an animation source and pick the clips that actually drive this model, ranked best-first.
        /// Engine-derived clip names are model-prefixed (<c>nchev_base_fire</c>, <c>ncflite_base_crouch</c>);
        /// the biped bone names (<c>bip01_*</c>) are SHARED across every soldier, so a naive bone-overlap
        /// filter would offer thousands of clips — including <c>fp_*</c> first-person clips that only pose
        /// the arms and visibly distort a third-person body. So: keep clips that share enough bones, rank
        /// the model's OWN clips first, and exclude first-person clips for third-person models (and vice
        /// versa).
        /// </summary>
        public void SetAnimSource(AnimDb db)
        {
            AnimSource = db;
            AnimClips.Clear();

            var boneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UberModel.Skeleton? skel = _selectedPart?.Skeleton;
            if (skel != null)
            {
                foreach (UberModel.Bone b in skel.Bones) boneNames.Add(b.Name);
            }

            string model = (_selectedPart?.Name ?? "").ToLowerInvariant();
            bool fpModel = model.StartsWith("fp_", StringComparison.Ordinal);
            int minOverlap = Math.Max(1, (skel?.Bones.Count ?? 1) / 4); // ~25% of the skeleton

            var scored = new List<(AnimRecord rec, int score)>();
            foreach (AnimRecord rec in db.Records)
            {
                int overlap = 0;
                foreach (AnimTrack tk in rec.Tracks)
                {
                    if (boneNames.Contains(tk.Name)) overlap++;
                }
                if (boneNames.Count > 0 && overlap < minOverlap) continue;

                bool isFp = rec.Name.StartsWith("fp_", StringComparison.OrdinalIgnoreCase);
                // First-person clips pose only the arms; on a 3rd-person body they wreck it. Match the
                // clip's "person" to the model's.
                if (boneNames.Count > 0 && isFp != fpModel) continue;

                bool own = model.Length > 0 && rec.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase);
                scored.Add((rec, own ? overlap + 1_000_000 : overlap));
            }

            scored.Sort((a, b) =>
            {
                int c = b.score.CompareTo(a.score);
                return c != 0 ? c : string.Compare(a.rec.Name, b.rec.Name, StringComparison.OrdinalIgnoreCase);
            });

            // If the model has its OWN clips (name-prefixed — these target its exact skeleton and look
            // perfect), offer only those. Otherwise fall back to the best-overlapping shared clips
            // (capped so the dropdown stays usable) — these are authored for a sibling armor's skeleton,
            // so they animate the body well with minor proportion differences at the extremities.
            var ownClips = scored.FindAll(s => s.score >= 1_000_000);
            List<(AnimRecord rec, int score)> chosen = ownClips.Count > 0
                ? ownClips
                : scored.GetRange(0, Math.Min(scored.Count, 250));
            foreach ((AnimRecord rec, int _) in chosen) AnimClips.Add(rec);

            // Drop a now-irrelevant clip selection.
            if (_activeClip != null && !AnimClips.Contains(_activeClip))
            {
                ActiveClip = null;
            }
        }

        private static string? AssetDirOf(string source)
        {
            int bang = source.IndexOf('!');
            string file = bang >= 0 ? source.Substring(0, bang) : source;
            try { return FindAssetRoot(Path.GetDirectoryName(file)); }
            catch { return null; }
        }

        /// <summary>
        /// The true asset root, not just the folder a model happens to sit in. A model opened from
        /// <c>expansion1/</c>, <c>patch1-5/</c>, or <c>patchmap/mapNN/</c> would otherwise get a
        /// <see cref="TextureProvider"/> scoped to that one subfolder — missing the root's and
        /// <c>pack/</c>'s shared texture archives that most materials actually resolve against, and
        /// breaking the root-relative <c>maps/map_resources.pak</c> + <c>uber.ubr</c> lookups in
        /// <see cref="AppendContinentObjects"/>. <c>uber.ubr</c> only ever exists at the true root, so
        /// walk upward looking for it (bounded, so an unrelated folder layout just falls back to the
        /// immediate directory instead of misbehaving).
        /// </summary>
        private static string? FindAssetRoot(string? dir)
        {
            string? d = dir;
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(d); i++)
            {
                if (File.Exists(Path.Combine(d, "uber.ubr"))) return d;
                d = Path.GetDirectoryName(d);
            }
            return dir;
        }

        private void Assemble(byte[] data, string? assetDir)
        {
            var model = UberModel.Load(data);
            _textures = GetTextureProvider(assetDir);
            TextureProvider textures = _textures;

            var allSubmeshes = new List<MeshSubmesh>();
            var parts = new List<MeshPart>();

            int sectionCount = 0;
            int refused = 0;
            var refuseReasons = new Dictionary<string, int>();
            var seenOffsets = new HashSet<uint>();
            for (int i = 0; i < model.Records.Count; i++)
            {
                UberRecord rec = model.Records[i];
                if (!seenOffsets.Add(rec.FirstVertex))
                {
                    continue;
                }

                UberModel.MeshSystem? sys = model.FetchMeshSystemAt(i);
                if (sys == null)
                {
                    refused++;
                    string reason = model.RefuseReason ?? "unknown";
                    refuseReasons[reason] = refuseReasons.GetValueOrDefault(reason) + 1;
                    continue;
                }

                // Per-system world placement: map tiles grid out via this offset; objects have it = 0.
                Vector3 worldOffset = sys.WorldOffset;

                List<MeshSubmesh> sysSubmeshes = BuildSystemSubmeshes(sys, worldOffset, textures, allowRigid: true, out int sa);
                sectionCount += sa;
                if (sysSubmeshes.Count == 0)
                {
                    continue;
                }

                ComputeBounds(sysSubmeshes, out Vector3 bmin, out Vector3 bmax);
                string partName = string.IsNullOrEmpty(rec.Name) ? $"system#{i}" : rec.Name;
                parts.Add(new MeshPart
                {
                    Name = partName,
                    Submeshes = sysSubmeshes,
                    BoundsMin = bmin,
                    BoundsMax = bmax,
                    Skeleton = sys.Skeletons.Count > 0 ? sys.Skeletons[0] : null,
                });
                allSubmeshes.AddRange(sysSubmeshes);
            }

            if (allSubmeshes.Count > 0)
            {
                ComputeBounds(allSubmeshes, out Vector3 amin, out Vector3 amax);
                parts.Insert(0, new MeshPart
                {
                    Name = $"All meshes ({parts.Count})",
                    Submeshes = allSubmeshes,
                    BoundsMin = amin,
                    BoundsMax = amax,
                    IsAggregate = true,
                });
            }

            foreach (MeshPart p in parts)
            {
                Parts.Add(p);
            }
            _allSubmeshes = allSubmeshes;

            int textured = 0;
            int tris = 0;
            foreach (MeshSubmesh s in allSubmeshes)
            {
                if (s.HasTexture) textured++;
                tris += s.TriangleCount;
            }
            string suffix = refused > 0 ? $" ({refused} systems refused)" : "";
            Summary = $"{model.Records.Count} records · {sectionCount} sections · {tris} tris · " +
                      $"{textures.IndexedTextureCount} textures indexed{suffix}";
            RefuseBreakdown = refuseReasons;
        }

        /// <summary>
        /// Decode one CMeshSystem into per-material textured submeshes (view space). <paramref name="allowRigid"/>
        /// enables the mesh-name↔bone-name rigid-attachment skin encoding (for animation); pass false for
        /// static instances (continent scene objects).
        /// </summary>
        private List<MeshSubmesh> BuildSystemSubmeshes(UberModel.MeshSystem sys, Vector3 worldOffset,
            TextureProvider textures, bool allowRigid, out int sectionsAdded)
        {
            sectionsAdded = 0;
            UberModel.Skeleton? skel = allowRigid && sys.Skeletons.Count > 0 ? sys.Skeletons[0] : null;
            var boneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (skel != null)
            {
                for (int bi = 0; bi < skel.Bones.Count; bi++)
                {
                    boneByName.TryAdd(skel.Bones[bi].Name, bi);
                }
            }

            var byMaterial = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
            foreach (UberModel.Mesh mesh in sys.Meshes)
            {
                int meshBone = boneByName.TryGetValue(mesh.Name, out int mb) ? mb : -1;
                foreach (UberModel.MeshSection section in mesh.Sections)
                {
                    if (section.VertexCount == 0 || section.IndexCount == 0)
                    {
                        continue;
                    }
                    if (!byMaterial.TryGetValue(section.MaterialName, out Builder? b))
                    {
                        b = new Builder(section.MaterialName);
                        byMaterial[section.MaterialName] = b;
                    }
                    AppendSection(section, b, worldOffset, meshBone);
                    sectionsAdded++;
                }
            }

            var result = new List<MeshSubmesh>();
            foreach (Builder b in byMaterial.Values)
            {
                MeshSubmesh? sm = b.Build(textures);
                if (sm != null)
                {
                    result.Add(sm);
                }
            }
            return result;
        }

        // ---- continent scene objects -----------------------------------------------------------

        // Z-up (native) ↔ Y-up (view) basis, matching ToViewSpace (x,y,z)→(x,z,-y).
        private static readonly Matrix4x4 ViewBasis = Matrix4x4.CreateRotationX(-MathF.PI / 2f);
        private static readonly Matrix4x4 ViewBasisInv = Matrix4x4.CreateRotationX(MathF.PI / 2f);

        // uber.ubr is ~106 MB and is the shared object library every continent references; decode once.
        private static readonly Dictionary<string, UberModel> UberCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object UberCacheLock = new();

        // Indexing the sibling .fat archives (>1 GB of directories, 21k+ textures) is the same for every
        // model opened from one folder — index once per asset dir and share.
        private static readonly Dictionary<string, TextureProvider> TexProviderCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object TexProviderLock = new();

        private static TextureProvider GetTextureProvider(string? assetDir)
        {
            string key = assetDir ?? "";
            lock (TexProviderLock)
            {
                if (TexProviderCache.TryGetValue(key, out TextureProvider? cached)) return cached;
            }
            var tp = new TextureProvider(assetDir);
            lock (TexProviderLock)
            {
                TexProviderCache[key] = tp;
            }
            return tp;
        }

        private static UberModel LoadUberCached(string path)
        {
            lock (UberCacheLock)
            {
                if (UberCache.TryGetValue(path, out UberModel? cached)) return cached;
            }
            UberModel m = UberModel.Load(File.ReadAllBytes(path));
            lock (UberCacheLock)
            {
                UberCache[path] = m;
            }
            return m;
        }

        // A continent .ubr is "mapNN.ubr" (NN = digits).
        private static bool IsContinentSource(string source)
        {
            string f = Path.GetFileNameWithoutExtension(BeforeBang(source)).ToLowerInvariant();
            if (!f.StartsWith("map") || f.Length <= 3) return false;
            for (int i = 3; i < f.Length; i++)
            {
                if (!char.IsDigit(f[i])) return false;
            }
            return Path.GetExtension(BeforeBang(source)).Equals(".ubr", StringComparison.OrdinalIgnoreCase);
        }

        private static string BeforeBang(string source)
        {
            int bang = source.IndexOf('!');
            return bang >= 0 ? source.Substring(0, bang) : source;
        }

        /// <summary>
        /// Populate a continent with its static scene objects (bases, towers, trees, …) from
        /// <c>maps/map_resources.pak → contents_mapNN.mpo</c>, each resolved by name from <c>uber.ubr</c>
        /// and placed at its recorded position/scale/rotation. Terrain is already assembled by
        /// <see cref="Assemble"/>; this adds a "Scene objects" part and folds the instances into the aggregate.
        /// Capped (instance count + baked-vertex budget) so a large continent stays openable.
        /// </summary>
        private void AppendContinentObjects(string assetDir, string stem)
        {
            if (_textures == null) return;
            string pakPath = Path.Combine(assetDir, "maps", "map_resources.pak");
            string uberPath = Path.Combine(assetDir, "uber.ubr");
            if (!File.Exists(pakPath) || !File.Exists(uberPath)) return;

            MpoFile mpo;
            try
            {
                PakArchive pak = PakArchive.Load(File.ReadAllBytes(pakPath));
                string entry = "contents_" + stem + ".mpo";
                if (pak.IndexOf(entry) < 0) return;
                mpo = MpoFile.Parse(pak.Extract(entry));
            }
            catch { return; }
            if (mpo.Objects.Count == 0) return;

            UberModel lib;
            try { lib = LoadUberCached(uberPath); }
            catch { return; }

            const int maxInstances = 600;          // keep submesh/draw-call count bounded
            const long vertBudget = 4_000_000;     // ~128 MB of baked object geometry
            var baseCache = new Dictionary<string, List<MeshSubmesh>>(StringComparer.OrdinalIgnoreCase);
            var objSubs = new List<MeshSubmesh>();
            long vertCount = 0;
            int placed = 0, skipped = 0, missing = 0;

            foreach (MapObject obj in mpo.Objects)
            {
                if (string.IsNullOrEmpty(obj.Name)) continue;
                if (placed >= maxInstances) { skipped++; continue; }

                if (!baseCache.TryGetValue(obj.Name, out List<MeshSubmesh>? baseSubs))
                {
                    UberModel.MeshSystem? osys = lib.FetchMeshSystem(obj.Name);
                    baseSubs = osys != null
                        ? BuildSystemSubmeshes(osys, Vector3.Zero, _textures, allowRigid: false, out _)
                        : new List<MeshSubmesh>();
                    baseCache[obj.Name] = baseSubs;
                }
                if (baseSubs.Count == 0) { missing++; continue; }

                long instVerts = 0;
                foreach (MeshSubmesh bs in baseSubs) instVerts += bs.VertexCount;
                if (vertCount + instVerts > vertBudget) { skipped++; continue; }

                Matrix4x4 m = InstanceMatrix(obj);
                foreach (MeshSubmesh bs in baseSubs) objSubs.Add(TransformSubmesh(bs, m));
                vertCount += instVerts;
                placed++;
            }

            if (objSubs.Count == 0) return;

            ComputeBounds(objSubs, out Vector3 omin, out Vector3 omax);
            Parts.Add(new MeshPart
            {
                Name = $"Scene objects ({placed})",
                Submeshes = objSubs,
                BoundsMin = omin,
                BoundsMax = omax,
            });
            _allSubmeshes.AddRange(objSubs);
            RebuildAggregate();

            string extra = (skipped > 0 ? $", {skipped} skipped (cap)" : "") +
                           (missing > 0 ? $", {missing} unresolved" : "");
            Summary += $" · {placed} scene objects{extra}";
        }

        // Native instance transform (scale → yaw about Z → translate), conjugated into view space so it
        // applies to the view-space base geometry: M_view = V⁻¹ · N_native · V.
        private static Matrix4x4 InstanceMatrix(MapObject obj)
        {
            Vector3 s = obj.Scale;
            if (s.X == 0f && s.Y == 0f && s.Z == 0f) s = Vector3.One;
            // MapObject.Yaw is the object's heading in radians about the native up (Z) axis.
            Matrix4x4 n = Matrix4x4.CreateScale(s)
                          * Matrix4x4.CreateRotationZ(obj.Yaw)
                          * Matrix4x4.CreateTranslation(obj.Position);
            return ViewBasisInv * n * ViewBasis;
        }

        // Bake a base submesh through an instance matrix (positions + normals); indices/material/texture
        // are shared (immutable) to keep memory down. Static — no skinning carried.
        private static MeshSubmesh TransformSubmesh(MeshSubmesh s, Matrix4x4 m)
        {
            float[] src = s.Vertices;
            var dst = new float[src.Length];
            for (int o = 0; o < src.Length; o += 8)
            {
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
            return new MeshSubmesh
            {
                Material = s.Material,
                Vertices = dst,
                Indices = s.Indices,
                TextureBgra = s.TextureBgra,
                TextureWidth = s.TextureWidth,
                TextureHeight = s.TextureHeight,
                TextureName = s.TextureName,
                IsTranslucent = s.IsTranslucent,
            };
        }

        private void RebuildAggregate()
        {
            if (Parts.Count == 0 || !Parts[0].IsAggregate) return;
            ComputeBounds(_allSubmeshes, out Vector3 amin, out Vector3 amax);
            Parts[0] = new MeshPart
            {
                Name = $"All meshes ({Parts.Count - 1})",
                Submeshes = _allSubmeshes,
                BoundsMin = amin,
                BoundsMax = amax,
                IsAggregate = true,
            };
        }

        // materials.adb defines a few continent surfaces (notably water) as named render materials rather
        // than direct textures, so ResolveNamed can't find them and they fall back to the 1×1 white
        // texture — that's the flat white "water" plane. The editor has no materials.adb resolver, so map
        // the visually-important ones to the engine's own water-surface / seabed textures. "water" is
        // additionally drawn translucent (a fixed alpha) so it reads like the original's semi-transparent
        // water rather than an opaque sheet. Resolved copies are cached and shared by reference so the
        // renderer's texture dedup collapses the ~800 water tiles to a single GPU texture.
        private readonly record struct AliasTex(byte[] Bgra, int Width, int Height, bool Translucent);

        private static readonly (string Material, string Texture, byte Alpha)[] MaterialAliases =
        {
            // ~0.88 opacity: deep water reads as solid dark-teal (the bright rocky seabed a few units
            // below doesn't show through as noise), while the slight translucency still hints at the
            // bottom in the shallows near shore.
            ("water", "old_water", 224),
            ("ocean_floor", "underwater", 255), // opaque seabed beneath the water
        };

        // The water sheet is baked coplanar with the seabed terrain (both sit at essentially the same
        // height across the open ocean), so without separation they z-fight. Lift the water a few world
        // units — enough to win the depth test cleanly at any zoom now that the camera's near/far hug the
        // scene, yet far too small (vs. the terrain's tens-of-units relief) for islands to stop occluding
        // it or for the raised shoreline to be visible.
        private const float WaterLiftY = 6f;

        private static readonly Dictionary<string, AliasTex?> AliasCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AliasLock = new();

        private static AliasTex? ResolveMaterialAlias(TextureProvider textures, string material)
        {
            lock (AliasLock)
            {
                if (AliasCache.TryGetValue(material, out AliasTex? cached)) return cached;
            }
            AliasTex? result = null;
            foreach (var (mat, tex, alpha) in MaterialAliases)
            {
                if (!material.Equals(mat, StringComparison.OrdinalIgnoreCase)) continue;
                DdsImage? img = textures.Get(tex);
                if (img != null)
                {
                    byte[] bgra = img.Bgra;
                    bool translucent = alpha != 255;
                    if (translucent)
                    {
                        bgra = (byte[])bgra.Clone(); // don't mutate the cached decode
                        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = alpha;
                    }
                    result = new AliasTex(bgra, img.Width, img.Height, translucent);
                }
                break;
            }
            lock (AliasLock)
            {
                AliasCache[material] = result;
            }
            return result;
        }

        // Per-material accumulator: positions (view-space), provided normals (or zero), uvs, indices.
        private sealed class Builder
        {
            public Builder(string material) { Material = material; }
            public string Material { get; }
            public List<Vector3> Pos { get; } = new();
            public List<Vector3> Norm { get; } = new();
            public List<Vector2> Uv { get; } = new();
            public List<uint> Idx { get; } = new();
            public List<byte> SkinA { get; } = new();
            public List<byte> SkinB { get; } = new();
            public List<float> SkinW { get; } = new();
            public bool AnySkin { get; set; }

            public MeshSubmesh? Build(TextureProvider textures)
            {
                if (Pos.Count == 0 || Idx.Count < 3)
                {
                    return null;
                }
                var pos = Pos.ToArray();
                var idx = Idx.ToArray();
                Vector3[] normals = FinalizeNormals(pos, Norm, idx);

                var verts = new float[pos.Length * 8];
                for (int i = 0; i < pos.Length; i++)
                {
                    int o = i * 8;
                    verts[o + 0] = pos[i].X; verts[o + 1] = pos[i].Y; verts[o + 2] = pos[i].Z;
                    verts[o + 3] = normals[i].X; verts[o + 4] = normals[i].Y; verts[o + 5] = normals[i].Z;
                    Vector2 uv = i < Uv.Count ? Uv[i] : default;
                    verts[o + 6] = uv.X; verts[o + 7] = uv.Y;
                }

                (DdsImage? dds, string? key) = textures.ResolveNamed(Material);
                byte[]? texBgra = dds?.Bgra;
                int texW = dds?.Width ?? 0, texH = dds?.Height ?? 0;
                string? texName = dds != null ? key : null;
                bool translucent = MeshSubmesh.IsMaskTextureName(texName);
                bool water = false;

                // Fall back to the materials.adb material aliases (water/seabed) when there's no direct
                // texture, so continent water renders as translucent water instead of a white plane.
                if (dds == null && ResolveMaterialAlias(textures, Material) is AliasTex alias)
                {
                    texBgra = alias.Bgra;
                    texW = alias.Width;
                    texH = alias.Height;
                    texName = Material;
                    translucent = alias.Translucent;
                    water = Material.Equals("water", StringComparison.OrdinalIgnoreCase);
                }

                // Nudge the water sheet just above the coplanar seabed (view Y is up) so it stops
                // z-fighting the terrain beneath it. See WaterLiftY.
                if (water)
                {
                    for (int i = 1; i < verts.Length; i += 8) verts[i] += WaterLiftY;
                }

                return new MeshSubmesh
                {
                    Material = Material,
                    Vertices = verts,
                    Indices = idx,
                    TextureBgra = texBgra,
                    TextureWidth = texW,
                    TextureHeight = texH,
                    TextureName = texName,
                    IsTranslucent = translucent,
                    BoneA = AnySkin ? SkinA.ToArray() : null,
                    BoneB = AnySkin ? SkinB.ToArray() : null,
                    Weight = AnySkin ? SkinW.ToArray() : null,
                };
            }
        }

        private static void AppendSection(UberModel.MeshSection section, Builder b, Vector3 worldOffset, int rigidBone)
        {
            uint baseVertex = (uint)b.Pos.Count;
            for (uint v = 0; v < section.VertexCount; v++)
            {
                UberModel.UberVert vert = section.Verts[v];
                // Place the tile/system in the world (native Z-up) before the Y-up rotation.
                b.Pos.Add(ToViewSpace(vert.Position + worldOffset));
                b.Norm.Add(section.HasNormal ? ToViewSpace(vert.Normal) : Vector3.Zero);
                b.Uv.Add(vert.Uv0);
                if (section.HasSkin)
                {
                    // True per-vertex skinning (Deform/FatDeform — soldiers).
                    b.AnySkin = true;
                    b.SkinA.Add(vert.BoneA);
                    b.SkinB.Add(vert.BoneB);
                    b.SkinW.Add(vert.Weight);
                }
                else if (rigidBone is >= 0 and <= 254)
                {
                    // Rigid attachment: the whole mesh moves with one bone. Encode as 1-bone LBS
                    // (BoneA==BoneB, weight 0) so the existing CPU skinning path drives it unchanged.
                    b.AnySkin = true;
                    byte rb = (byte)rigidBone;
                    b.SkinA.Add(rb);
                    b.SkinB.Add(rb);
                    b.SkinW.Add(0f);
                }
                else
                {
                    // No matching bone: 255 is out of skeleton range, so the skinner yields identity.
                    b.SkinA.Add(255);
                    b.SkinB.Add(255);
                    b.SkinW.Add(0f);
                }
            }

            ushort[] idx = section.Indices;
            uint vcount = section.VertexCount;
            if (section.IsTriStrip)
            {
                for (int i = 0; i + 2 < idx.Length; i++)
                {
                    ushort a = idx[i], bb = idx[i + 1], c = idx[i + 2];
                    if (a == bb || bb == c || a == c) continue;
                    if (a >= vcount || bb >= vcount || c >= vcount) continue;
                    if ((i & 1) == 0)
                    {
                        b.Idx.Add(baseVertex + a); b.Idx.Add(baseVertex + bb); b.Idx.Add(baseVertex + c);
                    }
                    else
                    {
                        b.Idx.Add(baseVertex + bb); b.Idx.Add(baseVertex + a); b.Idx.Add(baseVertex + c);
                    }
                }
            }
            else
            {
                for (int i = 0; i + 2 < idx.Length; i += 3)
                {
                    ushort a = idx[i], bb = idx[i + 1], c = idx[i + 2];
                    if (a >= vcount || bb >= vcount || c >= vcount) continue;
                    b.Idx.Add(baseVertex + a); b.Idx.Add(baseVertex + bb); b.Idx.Add(baseVertex + c);
                }
            }
        }

        private static Vector3[] FinalizeNormals(Vector3[] positions, List<Vector3> provided, uint[] indices)
        {
            var normals = new Vector3[positions.Length];
            var needsFace = new bool[positions.Length];
            for (int i = 0; i < positions.Length; i++)
            {
                Vector3 n = i < provided.Count ? provided[i] : Vector3.Zero;
                if (n.LengthSquared() > 1e-10f)
                {
                    normals[i] = Vector3.Normalize(n);
                }
                else
                {
                    needsFace[i] = true;
                }
            }
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                uint a = indices[t], b = indices[t + 1], c = indices[t + 2];
                Vector3 fn = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (needsFace[a]) normals[a] += fn;
                if (needsFace[b]) normals[b] += fn;
                if (needsFace[c]) normals[c] += fn;
            }
            for (int i = 0; i < normals.Length; i++)
            {
                if (!needsFace[i]) continue;
                float len = normals[i].Length();
                normals[i] = len > 1e-6f ? normals[i] / len : Vector3.UnitY;
            }
            return normals;
        }

        /// <summary>
        /// The engine is Z-up right-handed; the camera is Y-up. Rotate −90° about X: (x,y,z)→(x,z,−y).
        /// Proper rotation (det +1) so winding/normals are preserved. Display-only.
        /// </summary>
        private static Vector3 ToViewSpace(Vector3 v) => new Vector3(v.X, v.Z, -v.Y);

        private static void ComputeBounds(List<MeshSubmesh> submeshes, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue);
            max = new Vector3(float.MinValue);
            bool any = false;
            foreach (MeshSubmesh s in submeshes)
            {
                float[] v = s.Vertices;
                for (int i = 0; i < v.Length; i += 8)
                {
                    var p = new Vector3(v[i], v[i + 1], v[i + 2]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    any = true;
                }
            }
            if (!any)
            {
                min = max = Vector3.Zero;
            }
        }
    }
}
