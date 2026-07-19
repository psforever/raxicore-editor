---
layout: page
title: Using the Data in Godot
description: How to load engine-derived meshes, skeletons, animations, and textures decoded by RaxicoreEditor.EngineAssets into a Godot 4 scene.
---

Raxicore Editor's decoding lives in a UI-free C# library, `RaxicoreEditor.EngineAssets` — the
same code the editor uses, with no Avalonia or Vulkan dependency. Because [Godot 4 has first-class
C# / .NET support](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html),
you can reference that library directly from a Godot **C# (.NET)** project and build meshes,
skeletons, and animations at load time. This page walks the mapping.

It assumes you've read the [Supported Formats]({{ '/formats/' | relative_url }}) reference for what
each container holds. Everything here works from the library's native types — `UberModel` (meshes +
skeletons) and `AnimDb` (keyframe clips) — so nothing here depends on the editor app.

> **Two routes.** This guide covers the **direct** route: build Godot resources from the decoded
> data in code. If you'd rather lean on Godot's mature importer, the **interchange** route —
> decode, then emit a glTF and let Godot's native importer handle skinning and animation — is more
> robust for complex scenes. No glTF exporter ships in the library today (OBJ export exists for
> static geometry); the direct route below is what works out of the box.

## The one thing that will bite you: coordinate space

The engine is **Z-up, right-handed**. Godot is **Y-up, right-handed** (−Z forward). Every position,
normal, bone rest, and animation key is authored Z-up, so the whole model needs one consistent
conversion. The mapping is a −90° rotation about X:

```
engine (x, y, z)  →  godot (x, z, −y)
```

The cleanest way to apply it is **once, at the root**: parent your whole imported model under a
`Node3D` whose basis is that rotation, and keep all the *internal* data (vertices, bone rests,
animation keys) in native engine Z-up. Skinning and animation then stay self-consistent in engine
space and only the final display is rotated — you never have to convert a quaternion by hand.

```csharp
var root = new Node3D();
root.Basis = new Basis(new Vector3(1, 0, 0), Mathf.DegToRad(-90)); // Z-up → Y-up
```

Do **not** also convert the per-vertex/per-bone data if you use the root-rotation approach — pick
one or the other, never both.

## Meshes → `ArrayMesh`

Load a system and walk its sections. Skinned soldier meshes are `Deform` / `FatDeform`; static
props are the other vertex types.

```csharp
using RaxicoreEditor.EngineAssets.Meshes;

var model = UberModel.Load(File.ReadAllBytes("uber.ubr"));
var sys   = model.FetchMeshSystem("trmmed");     // a CMeshSystem record

foreach (var mesh in sys.Meshes)
foreach (var section in mesh.Sections)
{
    if (section.VertexCount == 0 || section.IndexCount == 0) continue;

    var verts   = new List<Vector3>();
    var normals = new List<Vector3>();
    var uvs     = new List<Vector2>();
    foreach (var v in section.Verts)
    {
        verts.Add(new Vector3(v.Position.X, v.Position.Y, v.Position.Z)); // native Z-up
        normals.Add(new Vector3(v.Normal.X, v.Normal.Y, v.Normal.Z));
        uvs.Add(new Vector2(v.Uv0.X, v.Uv0.Y));
    }

    // Triangles: sections are either a triangle LIST or a triangle STRIP — honor the flag.
    var indices = new List<int>();
    var idx = section.Indices;
    if (section.IsTriStrip)
        for (int i = 0; i + 2 < idx.Length; i++)          // strip → list, alternating winding
            if ((i & 1) == 0) { indices.Add(idx[i]); indices.Add(idx[i+1]); indices.Add(idx[i+2]); }
            else              { indices.Add(idx[i]); indices.Add(idx[i+2]); indices.Add(idx[i+1]); }
    else
        for (int i = 0; i + 2 < idx.Length; i += 3)
            { indices.Add(idx[i]); indices.Add(idx[i+1]); indices.Add(idx[i+2]); }

    var arrays = new Godot.Collections.Array();
    arrays.Resize((int)Mesh.ArrayType.Max);
    arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
    arrays[(int)Mesh.ArrayType.Normal] = normals.ToArray();
    arrays[(int)Mesh.ArrayType.TexUV]  = uvs.ToArray();
    arrays[(int)Mesh.ArrayType.Index]  = indices.ToArray();

    if (section.HasSkin)
        AddSkinArrays(arrays, section);   // see next section

    arrayMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
}
```

## Skinning → `ARRAY_BONES` / `ARRAY_WEIGHTS`

This is the part that most easily goes wrong, and it's worth stating precisely.

Each skinned vertex is a **two-bone linear blend**. The library exposes it as `BoneA`, `BoneB`, and
a single `Weight`. **`BoneA` is the weighted (dominant) bone** — it receives `Weight` (always in
`[0.5, 1.0]`); `BoneB` receives `1 − Weight`. (If you decode the raw `.ubr` yourself rather than
using the library: the weight belongs to the vertex's *byte1* bone index, not byte0 — getting this
backwards binds most of the body to the pelvis and explodes the mesh the moment it animates.)

Godot wants four bone slots per vertex. Fill the two you have and zero the rest — the weights
already sum to 1, so no renormalization is needed.

```csharp
static void AddSkinArrays(Godot.Collections.Array arrays, UberModel.MeshSection section)
{
    int n = (int)section.VertexCount;
    var bones   = new int[n * 4];
    var weights = new float[n * 4];
    for (int i = 0; i < n; i++)
    {
        var v = section.Verts[i];
        bones[i*4 + 0] = v.BoneA;   weights[i*4 + 0] = v.Weight;       // dominant
        bones[i*4 + 1] = v.BoneB;   weights[i*4 + 1] = 1f - v.Weight;  // secondary
        // slots 2,3 left as bone 0 / weight 0
    }
    arrays[(int)Mesh.ArrayType.Bones]   = bones;
    arrays[(int)Mesh.ArrayType.Weights] = weights;
}
```

## Skeleton → `Skeleton3D`

Bones carry a name, a parent index, and a **bind-local** position + rotation (parent-relative,
Z-up). The rotation quaternion is stored component order **XYZW**, which is exactly Godot's
`Quaternion(x, y, z, w)` — no reordering. A bone's local transform is rotate-then-translate.

```csharp
var skel = new Skeleton3D();
var bones = sys.Skeletons[0].Bones;
for (int i = 0; i < bones.Count; i++)
{
    var b = bones[i];
    skel.AddBone(b.Name);
    var basis = new Basis(new Quaternion(b.Rotation.X, b.Rotation.Y, b.Rotation.Z, b.Rotation.W));
    skel.SetBoneRest(i, new Transform3D(basis, new Vector3(b.Position.X, b.Position.Y, b.Position.Z)));
}
for (int i = 0; i < bones.Count; i++)
    if (bones[i].Parent >= 0) skel.SetBoneParent(i, bones[i].Parent);   // set parents after all bones exist

// Bind the mesh to the skeleton.
var mi = new MeshInstance3D { Mesh = arrayMesh };
skel.AddChild(mi);
mi.Skin = skel.CreateSkinFromRestTransforms();
mi.Skeleton = mi.GetPathTo(skel);
```

## Animation → `Animation`

`AnimDb.Load(File.ReadAllBytes("anims.ubr"))` gives you records (clips). A clip matches a skeleton
**by bone name**, and each track holds position and rotation keys at **absolute times in seconds**.
The library already renormalizes the quaternions on read, so a key value is drop-in.

Build one Godot `Animation` per clip, adding a position and rotation track per animated bone,
pointed at the skeleton bone path:

```csharp
var db   = AnimDb.Load(File.ReadAllBytes("anims.ubr"));
var clip = db.Records.First(r => r.Name == "ncmlite_base_stinky");

var anim = new Animation { Length = clip.Duration, LoopMode = Animation.LoopModeEnum.Linear };
string skelPath = ".";   // relative to the AnimationPlayer's root; adjust to your scene

foreach (var track in clip.Tracks)
{
    if (track.PosKeys.Count > 0)
    {
        int t = anim.AddTrack(Animation.TrackType.Position3D);
        anim.TrackSetPath(t, $"{skelPath}:{track.Name}");
        foreach (var k in track.PosKeys)
            anim.PositionTrackInsertKey(t, k.Time, new Vector3(k.Value.X, k.Value.Y, k.Value.Z));
    }
    if (track.RotKeys.Count > 0)
    {
        int t = anim.AddTrack(Animation.TrackType.Rotation3D);
        anim.TrackSetPath(t, $"{skelPath}:{track.Name}");
        foreach (var k in track.RotKeys)
            anim.RotationTrackInsertKey(t, k.Time,
                new Quaternion(k.Value.X, k.Value.Y, k.Value.Z, k.Value.W));
    }
}
```

The keys are **absolute local poses**, not deltas — which is exactly what Godot's `Position3D` /
`Rotation3D` bone tracks apply, so they override the rest pose correctly. A bone with no track keeps
its rest transform. Add the finished `Animation` to an `AnimationLibrary` on an `AnimationPlayer`
whose root resolves the bone paths above.

## Textures

`.dds` files are BC1/BC2/BC3 (or uncompressed 32-bpp). Godot imports DDS natively, so the simplest
path is to hand the bytes to `Image` / `ImageTexture` and assign the result to a
`StandardMaterial3D.AlbedoTexture`. Material names are on each `MeshSection` (`MaterialName`) — use
them to look up the right texture per surface. If you prefer, the library can also decode a DDS to a
BGRA pixel buffer you wrap in an `ImageTexture` directly.

## Gotchas at a glance

| Concern | What to do |
|---|---|
| Coordinate space | Engine Z-up → Godot Y-up; apply `(x,y,z)→(x,z,−y)` **once** (root basis `RotationX(−90°)`). |
| Skin weight | `BoneA` is dominant and takes `Weight ∈ [0.5,1]`; `BoneB` takes `1 − Weight`. Getting A/B backwards explodes the mesh on animation. |
| Weight normalization | Not needed — the two weights already sum to 1; zero the unused Godot slots. |
| Quaternion order | Stored **XYZW** = Godot `Quaternion(x,y,z,w)`; no reordering, and `AnimDb` already renormalizes. |
| Bind transforms | Bone position/rotation are **parent-relative** (local). Set all bones, then parents, then rests. |
| Topology | Sections are triangle **list or strip** — check `IsTriStrip` and flip winding on odd strip triangles. |
| Clip ↔ skeleton | Matched by **bone name**; times are absolute seconds; a track-less bone stays at rest. |

## Reference

- [Supported Formats]({{ '/formats/' | relative_url }}) — the container/content formats and what each holds.
- [Building & Running]({{ '/building/' | relative_url }}) — set up the library and editor from source.
- The `RaxicoreEditor.EngineAssets` C# library — the decoding used above, reusable in any .NET host including a Godot C# project.
