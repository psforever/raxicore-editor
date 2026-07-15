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
        /// <summary>Baked per-vertex colour (RGBA 0-1, 4 floats per vertex), from the engine's pre-lit
        /// (<c>Diffuse</c>) vertex stream; null when the section carries no colour (treated as white).
        /// Consumed only by the engine-derived shader path; the generic shader ignores it.</summary>
        public float[]? Colors { get; init; }
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
            RefreshFilteredParts();

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

        private string _meshFilter = "";
        /// <summary>Filters <see cref="FilteredParts"/> by name substring (the MESHES search box).</summary>
        public string MeshFilter
        {
            get => _meshFilter;
            set { if (SetProperty(ref _meshFilter, value)) RefreshFilteredParts(); }
        }

        /// <summary>The mesh-part shortlist shown in the picker — all <see cref="Parts"/>, filtered by
        /// <see cref="MeshFilter"/>. The list binds to this so the selection still drives the viewport.</summary>
        public ObservableCollection<MeshPart> FilteredParts { get; } = new();

        private void RefreshFilteredParts()
        {
            // Build the desired set in Parts order: everything matching the filter, plus the selected part
            // even if it doesn't match — so typing in the search box never yanks the active mesh out from
            // under the viewport (the list's selection is two-way bound to SelectedPart).
            var target = new List<MeshPart>();
            foreach (MeshPart p in Parts)
            {
                bool matches = _meshFilter.Length == 0 ||
                               p.Name.Contains(_meshFilter, StringComparison.OrdinalIgnoreCase);
                if (matches || ReferenceEquals(p, _selectedPart))
                {
                    target.Add(p);
                }
            }

            // Sync FilteredParts to target in place (never a full Clear) so the selected container — and
            // thus the ListBox selection — survives the update. Both lists follow Parts order.
            for (int i = FilteredParts.Count - 1; i >= 0; i--)
            {
                if (!target.Contains(FilteredParts[i]))
                {
                    FilteredParts.RemoveAt(i);
                }
            }
            for (int i = 0; i < target.Count; i++)
            {
                if (i >= FilteredParts.Count || !ReferenceEquals(FilteredParts[i], target[i]))
                {
                    FilteredParts.Insert(i, target[i]);
                }
            }
        }

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
        // A mesh at or above this LOD is a far billboard/impostor (values seen: 1001-1007) — never drawn up
        // close, so "Detailed" never keeps one.
        private const uint BillboardLod = 1000;

        // Decide which of a system's meshes to build. A CMeshSystem stores every part of a model at every
        // LOD as a separate mesh; the mesh.Lod tag alone is unreliable (the exterior shell can be lod 36
        // while interior rooms are lod 14, or the reverse). The robust signal is the bounding box: meshes
        // that share (near-)equal bounds are the SAME spatial object at different detail — a LOD chain —
        // while meshes with distinct bounds are complementary parts (exterior shell, interior floor and
        // rooms, ramps, attachments) that must ALL be drawn. So group by bbox and keep one mesh per group:
        // the most detailed (max vertices) for "Detailed", the least for "Low". The tolerance is deliberately
        // tight so a genuinely flatter interior floor (e.g. a comm station's main deck) is never mistaken for
        // a shorter exterior LOD and dropped; erring tight can at worst leave one coarse LOD copy overlapping
        // (minor opaque z-fight) rather than delete a wall. Billboards (lod >= 1000) are dropped outright in
        // "Detailed" — their flat bounds wouldn't group with the 3-D model, so they'd stick out of it.
        private static bool[] KeepMeshes(UberModel.MeshSystem sys)
        {
            int n = sys.Meshes.Count;
            var keep = new bool[n];
            if (n == 0) return keep;

            var verts = new int[n];
            var bmin = new Vector3[n];
            var bmax = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var mn = new Vector3(float.MaxValue);
                var mx = new Vector3(float.MinValue);
                int v = 0;
                foreach (UberModel.MeshSection s in sys.Meshes[i].Sections)
                {
                    for (uint k = 0; k < s.VertexCount; k++)
                    {
                        Vector3 p = s.Verts[k].Position;
                        mn = Vector3.Min(mn, p);
                        mx = Vector3.Max(mx, p);
                    }
                    v += (int)s.VertexCount;
                }
                verts[i] = v; bmin[i] = mn; bmax[i] = mx;
            }

            bool low = RenderSettings.Detail == ModelDetail.Low;
            var used = new bool[n];
            for (int i = 0; i < n; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                bool iElig = low || sys.Meshes[i].Lod < BillboardLod;
                int best = iElig && verts[i] > 0 ? i : -1;
                for (int j = i + 1; j < n; j++)
                {
                    if (used[j] || !SimilarExtent(bmin[i], bmax[i], bmin[j], bmax[j])) continue;
                    used[j] = true;
                    bool jElig = low || sys.Meshes[j].Lod < BillboardLod;
                    if (!jElig || verts[j] == 0) continue;
                    if (best < 0 || (low ? verts[j] < verts[best] : verts[j] > verts[best])) best = j;
                }
                if (best >= 0) keep[best] = true;
            }
            return keep;
        }

        // Two meshes are the same spatial object at different detail (a LOD chain) when they have ~the same
        // SIZE and ~the same CENTRE, per axis. Comparing size+centre (not absolute min/max) collapses LOD
        // chains whose simplification shifts/shrinks the box a little — e.g. player models, which ship as a
        // base mesh plus 01..05 at falling detail, all covering the same figure but with boxes that drift by
        // ~10-30% and so escaped an absolute-bounds test, leaving several LODs stacked and z-fighting. It
        // still keeps genuinely distinct parts apart: a facility's flat interior deck differs from its shell
        // in one axis's SIZE, and its interior rooms sit at different CENTRES.
        private static bool SimilarExtent(Vector3 amin, Vector3 amax, Vector3 bmin, Vector3 bmax)
        {
            Vector3 asz = amax - amin, bsz = bmax - bmin;
            Vector3 ac = (amin + amax) * 0.5f, bc = (bmin + bmax) * 0.5f;
            const float tol = 0.35f;
            for (int d = 0; d < 3; d++)
            {
                float sa = Axis(asz, d), sb = Axis(bsz, d);
                float big = MathF.Max(MathF.Max(sa, sb), 1e-3f);
                if (MathF.Abs(sa - sb) > tol * big) return false;                 // sizes differ → different object
                if (MathF.Abs(Axis(ac, d) - Axis(bc, d)) > tol * big) return false; // centres apart → different object
            }
            return true;
        }

        private static float Axis(Vector3 v, int d) => d == 0 ? v.X : d == 1 ? v.Y : v.Z;

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

            // A CMeshSystem ships every part of its model at every LOD as a separate mesh: exterior shell,
            // interior rooms, ramps and attachments, each with a chain of decreasing-detail copies plus far
            // billboards. Drawing them all stacks overlapping shells (z-fighting and, for translucent parts,
            // a "whispy" look). KeepMeshes groups meshes by bounding box and keeps one per group — the whole
            // model, every part at its best LOD, with each part's LOD copies and billboards collapsed away.
            bool[] keepMesh = KeepMeshes(sys);

            var byMaterial = new Dictionary<string, Builder>(StringComparer.OrdinalIgnoreCase);
            for (int mi = 0; mi < sys.Meshes.Count; mi++)
            {
                if (!keepMesh[mi])
                {
                    continue;
                }
                UberModel.Mesh mesh = sys.Meshes[mi];
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
            string file = BeforeBang(source);
            if (!Path.GetExtension(file).Equals(".ubr", StringComparison.OrdinalIgnoreCase)) return false;
            string f = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            // Continents are mapNN.ubr; Core Combat caverns are ugdNN.ubr — both are terrain grids that get a
            // placed scene layer (caverns from groundcover only, see AppendContinentObjects).
            string? prefix = f.StartsWith("map") ? "map" : f.StartsWith("ugd") ? "ugd" : null;
            if (prefix is null || f.Length <= prefix.Length) return false;
            for (int i = prefix.Length; i < f.Length; i++)
            {
                if (!char.IsDigit(f[i])) return false;
            }
            return true;
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
            string uberPath = Path.Combine(assetDir, "uber.ubr");
            if (!File.Exists(uberPath)) return;

            // Scene data lives in maps/map_resources.pak for the main continents and sanctuaries; the battle
            // islands (map96–99) ship theirs under patchmap/<stem>/<stem>_resources.pak; the Core Combat
            // caverns (ugdNN) live in expansion1/expansion1.pak. ResolveResourcePak returns whichever pak
            // holds this zone's scene (its contents or its groundcover).
            PakArchive pak;
            try
            {
                PakArchive? found = ResolveResourcePak(assetDir, stem);
                if (found == null) return;
                pak = found;
            }
            catch { return; }

            // The map_objects manifest (facilities/towers/bridges/warpgate pads). Caverns have none — their
            // whole scene (Vanu modules, crystals, Geowarps, flora) is placed via groundcover instead.
            MpoFile? mpo = null;
            string contentsEntry = "contents_" + stem + ".mpo";
            if (pak.IndexOf(contentsEntry) >= 0)
            {
                try { mpo = MpoFile.Parse(pak.Extract(contentsEntry)); } catch { mpo = null; }
            }

            UberModel lib;
            try { lib = LoadUberCached(uberPath); }
            catch { return; }

            // A full continent places its contents (facilities/towers/bridges/warpgates) plus a large
            // groundcover layer (flora + a few scattered structures) — map03 is ~1.5k contents and ~16k
            // groundcover. These ceilings fit that with headroom; anything past them is dropped (and
            // counted) so the open still succeeds instead of failing.
            const int maxInstances = 60000;
            const long vertBudget = 40_000_000;
            var baseCache = new Dictionary<string, List<MeshSubmesh>>(StringComparer.OrdinalIgnoreCase);
            long vertCount = 0;
            int placed = 0, skipped = 0, missing = 0;

            // Build (and cache) a record's base submeshes once, reused across all its instances. Records are
            // resolved across the stacked model libraries (uber.ubr + the patch/expansion .ubr files), since
            // patch-added assets — warpgate_small, hst, bfr_building, repair_silo, most flora — aren't in uber.
            List<MeshSubmesh> GetSubs(string recordName)
            {
                if (!baseCache.TryGetValue(recordName, out List<MeshSubmesh>? subs))
                {
                    UberModel.MeshSystem? sysN = ResolveMeshSystem(assetDir, lib, VisualRecordName(recordName));
                    subs = sysN != null
                        ? BuildSystemSubmeshes(sysN, Vector3.Zero, _textures, allowRigid: false, out _)
                        : new List<MeshSubmesh>();
                    baseCache[recordName] = subs;
                }
                return subs;
            }

            // Place one instance; on success reports its instance matrix (used to hang composite children).
            bool TryPlace(MapObject obj, List<MeshSubmesh> outList, out Matrix4x4 m)
            {
                m = default;
                if (string.IsNullOrEmpty(obj.Name)) return false;
                if (placed >= maxInstances) { skipped++; return false; }
                List<MeshSubmesh> baseSubs = GetSubs(obj.Name);
                if (baseSubs.Count == 0) { missing++; return false; }
                long instVerts = 0;
                foreach (MeshSubmesh bs in baseSubs) instVerts += bs.VertexCount;
                if (vertCount + instVerts > vertBudget) { skipped++; return false; }
                m = InstanceMatrix(obj);
                foreach (MeshSubmesh bs in baseSubs) outList.Add(TransformSubmesh(bs, m));
                vertCount += instVerts;
                placed++;
                return true;
            }

            // ---- contents_mapNN.mpo : the main placed objects (facilities, towers, bridges, warpgate pads).
            // A warpgate is the flat "warpgate" pad PLUS three standing arches assembled from warpgate_1.lst,
            // each piece placed relative to the pad in its own native frame (V⁻¹·N·V carried through the pad
            // instance m, whose shared V/V⁻¹ cancels).
            var objSubs = new List<MeshSubmesh>();
            if (mpo != null)
            {
                IReadOnlyList<RelativeObject> warpParts = LoadRelativeObjects(pak, "warpgate_1.lst");
                foreach (MapObject obj in mpo.Objects)
                {
                    if (!TryPlace(obj, objSubs, out Matrix4x4 m)) continue;

                    if (warpParts.Count > 0 && obj.Name.Equals("warpgate", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (RelativeObject part in warpParts)
                        {
                            Matrix4x4 partNative =
                                Matrix4x4.CreateScale(part.Scale) *
                                Matrix4x4.CreateRotationZ(part.Yaw) *
                                Matrix4x4.CreateTranslation(part.Position);
                            Matrix4x4 pm = ViewBasisInv * partNative * ViewBasis * m;
                            foreach (MeshSubmesh bs in GetSubs(part.Name)) objSubs.Add(TransformSubmesh(bs, pm));
                        }
                    }
                }
            }
            int contentsPlaced = placed;

            // ---- groundcover_mapNN.lst : flora (trees/rocks) plus scattered structures that aren't in
            // map_objects — the battle-island warpgate_small/hst gates, sanctuary BFR buildings and repair
            // silos, etc. Each pse_exactobject is an absolute world placement, same frame as a map_object.
            var groundSubs = new List<MeshSubmesh>();
            foreach (ExactObject e in LoadExactObjects(pak, "groundcover_" + stem + ".lst"))
            {
                TryPlace(new MapObject(-1, e.Name, e.Position, e.Scale, e.Yaw), groundSubs, out _);
            }
            int groundPlaced = placed - contentsPlaced;

            if (objSubs.Count == 0 && groundSubs.Count == 0) return;

            if (objSubs.Count > 0)
            {
                ComputeBounds(objSubs, out Vector3 omin, out Vector3 omax);
                Parts.Add(new MeshPart { Name = $"Scene objects ({contentsPlaced})", Submeshes = objSubs, BoundsMin = omin, BoundsMax = omax });
                _allSubmeshes.AddRange(objSubs);
            }
            if (groundSubs.Count > 0)
            {
                ComputeBounds(groundSubs, out Vector3 gmin, out Vector3 gmax);
                Parts.Add(new MeshPart { Name = $"Groundcover ({groundPlaced})", Submeshes = groundSubs, BoundsMin = gmin, BoundsMax = gmax });
                _allSubmeshes.AddRange(groundSubs);
            }
            RebuildAggregate();

            string extra = (skipped > 0 ? $", {skipped} skipped (cap)" : "") +
                           (missing > 0 ? $", {missing} unresolved" : "");
            Summary += $" · {contentsPlaced} scene objects, {groundPlaced} groundcover{extra}";
        }

        // Patch/expansion model libraries, highest precedence first. The client applies patches over the
        // base uber.ubr; a handful of records (warpgate_small, hst, bfr_building, repair_silo, most flora)
        // only exist here. Paths are relative to the asset root. Loaded lazily and cached.
        private static readonly string[] FallbackLibs =
        {
            @"patch5\patch5.ubr",
            @"patch4\patch4.ubr",
            @"patch3\patch3.ubr",
            @"patch2\patch2.ubr",
            @"patch1\patch1.ubr",
            @"expansion1\expansion1.ubr",
        };

        // A capitol facility's force-dome shield is placed as its invisible "_physics" collision hull
        // (material force_dome_phy_tex, which carries no texture and so renders as an opaque white dome).
        // Substitute the matching visual dome — identical shape and placement, but built from the
        // translucent energy-shield materials (force_dome_*_inner/_outer, flagged translucent in mat.adb),
        // which the renderer's blend pass draws as a see-through shield.
        private static string VisualRecordName(string name)
        {
            const string physics = "_physics";
            if (name.Length > physics.Length &&
                name.StartsWith("force_dome_", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(physics, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(0, name.Length - physics.Length);
            }
            return name;
        }

        // Resolve a record's mesh system across the stacked model libraries: uber.ubr first (it holds the
        // overwhelming majority), then the patch/expansion libraries for the records uber lacks. Missing
        // libraries are skipped; a name absent everywhere returns null (the caller renders nothing for it).
        private static UberModel.MeshSystem? ResolveMeshSystem(string assetDir, UberModel uber, string name)
        {
            // Groundcover names are sometimes written with a leading '@' or '!' variant marker
            // (e.g. "@deserttreeg", "!zipline"); no record carries that prefix, so the mesh is the same
            // record without it.
            if (name.Length > 0 && (name[0] == '@' || name[0] == '!')) name = name.Substring(1);

            UberModel.MeshSystem? sys = uber.FetchMeshSystem(name);
            if (sys != null) return sys;
            foreach (string rel in FallbackLibs)
            {
                string path = Path.Combine(assetDir, rel);
                if (!File.Exists(path)) continue;
                UberModel? extra;
                try { extra = LoadUberCached(path); }
                catch { continue; }
                sys = extra.FetchMeshSystem(name);
                if (sys != null) return sys;
            }
            return null;
        }

        // Return the resource pak that holds this zone's scene — identified by carrying its contents
        // (contents_<stem>.mpo) or its groundcover (groundcover_<stem>.lst): the shared
        // maps/map_resources.pak for main continents + sanctuaries, patchmap/<stem>/<stem>_resources.pak for
        // the battle islands (map96–99), or expansion1/expansion1.pak for the Core Combat caverns (ugdNN,
        // which are groundcover-only). Null if none of them has it.
        private static PakArchive? ResolveResourcePak(string assetDir, string stem)
        {
            string contents = "contents_" + stem + ".mpo";
            string ground = "groundcover_" + stem + ".lst";
            string[] candidates =
            {
                Path.Combine("maps", "map_resources.pak"),
                Path.Combine("patchmap", stem, stem + "_resources.pak"),
                Path.Combine("expansion1", "expansion1.pak"),
            };
            foreach (string rel in candidates)
            {
                string path = Path.Combine(assetDir, rel);
                if (!File.Exists(path)) continue;
                PakArchive p;
                try { p = PakArchive.Load(File.ReadAllBytes(path)); } catch { continue; }
                if (p.IndexOf(contents) >= 0 || p.IndexOf(ground) >= 0) return p;
            }
            return null;
        }

        // Read a groundcover manifest (pse_exactobject list) from the continent resource pak. Empty if the
        // entry is absent or unreadable.
        private static IReadOnlyList<ExactObject> LoadExactObjects(PakArchive pak, string entry)
        {
            try
            {
                if (pak.IndexOf(entry) < 0)
                {
                    return Array.Empty<ExactObject>();
                }
                return ExactObjectList.Parse(pak.Extract(entry)).Objects;
            }
            catch
            {
                return Array.Empty<ExactObject>();
            }
        }

        // Read a composite-object definition (pse_relativeobject list) from the continent resource pak.
        // Returns an empty list if the entry is absent or unreadable — callers just skip the sub-objects.
        private static IReadOnlyList<RelativeObject> LoadRelativeObjects(PakArchive pak, string entry)
        {
            try
            {
                if (pak.IndexOf(entry) < 0)
                {
                    return Array.Empty<RelativeObject>();
                }
                return RelativeObjectList.Parse(pak.Extract(entry)).Objects;
            }
            catch
            {
                return Array.Empty<RelativeObject>();
            }
        }

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
                Colors = s.Colors, // baked colour is transform-invariant — share the array
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

        // "water" is a materials.adb render-material definition, not a direct texture, so ResolveNamed
        // finds nothing and it falls back to the 1×1 white texture — the flat white "water" plane. Map it
        // to the engine's own water surface texture and draw it translucent so it reads like the original's
        // semi-transparent water. The resolved copy is cached and shared by reference so the renderer's
        // texture dedup collapses the ~800 water tiles to a single GPU texture.
        private readonly record struct AliasTex(byte[] Bgra, int Width, int Height, bool Translucent);

        // Continent water surfaces come under a few base material names (each usually suffixed "+null"):
        // "water" (the open sea), "tide" (the shoreline band). None resolve to a direct texture, so map
        // them to the engine's water texture at ~0.90 opacity — deep water reads solid dark-teal from
        // above; the slight translucency only hints at the bottom in the shallows near shore.
        private static readonly (string Material, string Texture, byte Alpha)[] MaterialAliases =
        {
            ("water", "old_water", 230),
            ("tide", "old_water", 230),
        };

        // "ocean_floor" is the engine's abyssal fill plane far below the water, not a visible seabed —
        // the reference client effectively skips the ocean chunk, so drawing it produced a confusing flat
        // plane beneath the water. Drop it.
        private static bool IsSkippedMaterial(string material) =>
            material.Equals("ocean_floor", StringComparison.OrdinalIgnoreCase);

        private static readonly Dictionary<string, AliasTex?> AliasCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object AliasLock = new();

        private static bool IsForceDomeMaterial(string material) =>
            material.StartsWith("force_dome_", StringComparison.OrdinalIgnoreCase);

        // True if any texel is below the alpha-test threshold (so it would be discarded by the opaque shader).
        private static bool HasTransparentTexels(byte[] bgra)
        {
            for (int i = 3; i < bgra.Length; i += 4)
            {
                if (bgra[i] < 128) return true;
            }
            return false;
        }

        // Decide whether a texture's alpha is a genuine cutout mask (keep the alpha test) versus an opaque
        // material's team-colour / spec / self-illum mask (must be forced opaque). Cutout masks are bimodal —
        // a large fraction of texels at each extreme (fully transparent AND fully opaque) with little in the
        // middle — whereas spec/tint masks are gradients or sit entirely on one side.
        private static bool IsCutoutAlpha(byte[] bgra)
        {
            int n = bgra.Length / 4;
            if (n == 0) return false;
            int trans = 0, opaque = 0, extreme = 0;
            for (int i = 3; i < bgra.Length; i += 4)
            {
                byte a = bgra[i];
                if (a < 128) trans++; else opaque++;
                if (a < 32 || a > 224) extreme++;
            }
            double ft = trans / (double)n, fo = opaque / (double)n, fe = extreme / (double)n;
            return ft >= 0.15 && fo >= 0.15 && fe >= 0.60;
        }

        private static readonly Dictionary<byte[], byte[]> OpaqueTexCache =
            new(ReferenceEqualityComparer.Instance);
        private static readonly object OpaqueTexLock = new();

        // Clone a texture with every alpha forced to 255 so the opaque shader's alpha test never discards it.
        // Keyed on the source array so materials sharing a texture share the one opaque copy (and the
        // renderer's by-reference texture dedup still collapses them to a single GPU upload).
        private static byte[] ForceOpaqueAlpha(byte[] bgra)
        {
            lock (OpaqueTexLock)
            {
                if (OpaqueTexCache.TryGetValue(bgra, out byte[]? cached)) return cached;
            }
            var outb = (byte[])bgra.Clone();
            for (int i = 3; i < outb.Length; i += 4) outb[i] = 255;
            lock (OpaqueTexLock)
            {
                OpaqueTexCache[bgra] = outb;
            }
            return outb;
        }

        private static readonly Dictionary<string, AliasTex> ShieldCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object ShieldLock = new();

        // Re-skin a force-dome texture into a translucent energy-shield look: neutral light-grey RGB (the
        // game tints the real shield by empire, so we stay generic), and alpha capped to a moderate value
        // so the dome is see-through — while preserving the outer mask's alpha pattern (its low-alpha
        // "window" areas stay the most transparent). Cached per material (only a handful of dome materials).
        private static AliasTex? ResolveShieldTex(string material, DdsImage dds)
        {
            lock (ShieldLock)
            {
                if (ShieldCache.TryGetValue(material, out AliasTex cached)) return cached;
            }
            byte[] src = dds.Bgra;
            var outb = new byte[src.Length];
            const byte capAlpha = 110;         // ~43% — visible shell, still clearly see-through
            const byte b = 210, g = 212, r = 216; // BGRA order: light neutral grey
            for (int i = 0; i + 3 < src.Length; i += 4)
            {
                outb[i] = b; outb[i + 1] = g; outb[i + 2] = r;
                byte a = src[i + 3];
                outb[i + 3] = a < capAlpha ? a : capAlpha;
            }
            var result = new AliasTex(outb, dds.Width, dds.Height, true);
            lock (ShieldLock)
            {
                ShieldCache[material] = result;
            }
            return result;
        }

        private static AliasTex? ResolveMaterialAlias(TextureProvider textures, string material)
        {
            lock (AliasLock)
            {
                if (AliasCache.TryGetValue(material, out AliasTex? cached)) return cached;
            }
            // Match the base material name, ignoring a "+<lightmap>" / "+null" suffix (water surfaces
            // ship as "water+null", "tide+null").
            int plus = material.IndexOf('+');
            string baseMat = plus > 0 ? material.Substring(0, plus) : material;
            AliasTex? result = null;
            foreach (var (mat, tex, alpha) in MaterialAliases)
            {
                if (!material.Equals(mat, StringComparison.OrdinalIgnoreCase) &&
                    !baseMat.Equals(mat, StringComparison.OrdinalIgnoreCase)) continue;
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
            public List<uint> Col { get; } = new(); // packed baked colour (D3DCOLOR 0xAARRGGBB); 0xFFFFFFFF = white
            public List<uint> Idx { get; } = new();
            public List<byte> SkinA { get; } = new();
            public List<byte> SkinB { get; } = new();
            public List<float> SkinW { get; } = new();
            public bool AnySkin { get; set; }

            public MeshSubmesh? Build(TextureProvider textures)
            {
                if (Pos.Count == 0 || Idx.Count < 3 || IsSkippedMaterial(Material))
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

                // Baked vertex colour (engine "Diffuse" stream) for the engine-derived shader path. Only
                // emit an array when some vertex is actually tinted — an all-white section stays null and the
                // renderer treats it as white, saving 16 bytes/vertex on the (common) untinted geometry.
                float[]? colors = null;
                bool anyTint = false;
                foreach (uint c in Col) { if (c != 0xFFFFFFFFu) { anyTint = true; break; } }
                if (anyTint)
                {
                    colors = new float[pos.Length * 4];
                    for (int i = 0; i < pos.Length; i++)
                    {
                        uint c = i < Col.Count ? Col[i] : 0xFFFFFFFFu; // D3DCOLOR 0xAARRGGBB
                        int o = i * 4;
                        colors[o + 0] = ((c >> 16) & 0xFF) / 255f; // R
                        colors[o + 1] = ((c >> 8) & 0xFF) / 255f;  // G
                        colors[o + 2] = (c & 0xFF) / 255f;         // B
                        colors[o + 3] = 1f;                        // A: use texture alpha for cutout, not the baked term
                    }
                }

                (DdsImage? dds, string? key) = textures.ResolveNamed(Material);
                byte[]? texBgra = dds?.Bgra;
                int texW = dds?.Width ?? 0, texH = dds?.Height ?? 0;
                string? texName = dds != null ? key : null;
                // Translucency: authoritative from materials.adb (mat_pipeline alpha_sort/effect),
                // plus the "mask" overlay heuristic for shield/beam materials.
                bool translucent = MeshSubmesh.IsMaskTextureName(texName)
                                   || (textures.Materials?.IsTranslucent(Material) ?? false);

                // Fall back to the "water" material alias when there's no direct texture, so continent
                // water renders as translucent water instead of a white plane.
                if (dds == null && ResolveMaterialAlias(textures, Material) is AliasTex alias)
                {
                    texBgra = alias.Bgra;
                    texW = alias.Width;
                    texH = alias.Height;
                    texName = Material;
                    translucent = alias.Translucent;
                }

                // Capitol force-dome shields: the visual dome's inner layer (dustplanet) is a fully opaque
                // texture, so even in the translucent pass it reads as a solid dome. Re-skin force-dome
                // materials to a neutral white/grey shell with a capped, mask-modulated alpha so the shield
                // renders see-through (the game tints it by empire — we keep it generic grey).
                if (dds != null && IsForceDomeMaterial(Material) &&
                    ResolveShieldTex(Material, dds) is AliasTex shield)
                {
                    texBgra = shield.Bgra;
                    texW = shield.Width;
                    texH = shield.Height;
                    texName = Material;
                    translucent = true;
                }

                // Alpha-channel discipline. The opaque shader alpha-tests every textured material (needed for
                // genuine cutouts: foliage, grates, decals). But many opaque materials — vehicle hulls, ammo
                // boxes — store a team-colour / spec / self-illum mask in the alpha channel, not a cutout, so
                // the test punches holes and you see straight through them. Keep the alpha only for textures
                // whose alpha is a real cutout mask (bimodal, big fractions at both 0 and 255); for everything
                // else force alpha opaque so nothing is discarded.
                if (!translucent && texBgra != null && HasTransparentTexels(texBgra) && !IsCutoutAlpha(texBgra))
                {
                    texBgra = ForceOpaqueAlpha(texBgra);
                }
                return new MeshSubmesh
                {
                    Material = Material,
                    Vertices = verts,
                    Colors = colors,
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
                // Pre-lit (Lit-type) sections carry a baked colour and no normal; the rest carry a normal and
                // no colour — feed white so the engine shader lights them instead of tinting them black.
                b.Col.Add(section.HasColor ? vert.Diffuse : 0xFFFFFFFFu);
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
