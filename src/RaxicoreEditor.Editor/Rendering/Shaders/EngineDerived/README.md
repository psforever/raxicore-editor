# Engine-derived material shaders — GLSL translations

The 10 vertex + 1 fragment files in this folder are **GLSL 450 translations** of the small
per-material vertex/pixel program set the engine-derived reference client ships (Cg source,
compiled at runtime via `cgD3D8`/D3D8). No Cg source text is stored anywhere in this repository —
only the translated GLSL and its compiled SPIR-V (`.spv`), produced with `glslc` the same way the
editor's own `Rendering/Shaders/mesh.vert`/`mesh.frag` are.

These are **not currently wired into the live `MeshViewportRenderer` pipeline** (which uses its own
generic textured-mesh shader) — they're included as a faithful, standalone reference/conversion,
each self-contained with its own uniform block matching the original technique's parameter list.
Wiring any of them into an actual per-material Vulkan pipeline is a separate future step.

## Files

| File | Original technique |
|---|---|
| `decal.vert` | Static decal: transform + UV passthrough + a constant tint uniform |
| `animated_decal.vert` | Decal + scrolling/scaling UV via an offset+scale uniform (e.g. energy/water FX) |
| `stealth.vert` | Same shape as decal, self-contained — a tinted/cloak-style effect |
| `simplepixel.frag` | The one fragment shader in the set: `vertexColor * texture(uv)` |
| `simple.vert` | Unlit textured pass (outputs a constant white vertex color) |
| `shadow.vert` | World position projected through a projector matrix → UV (projected blob shadow) |
| `reflection.vert` | Eye-space sphere-map reflection UV generation |
| `position.vert` | Plain transform + vertex-color/UV passthrough, no lighting |
| `vertexlight.vert` | One directional light + ambient, per-vertex, untextured |
| `vertexlight4.vert` | Four directional lights (combined multiplicatively, not summed — preserved as authored) + ambient, untextured |

## Translation notes

- **Matrix convention**: each original `mul(M, v)` call is translated literally as GLSL `M * v`,
  preserving the source's call order rather than re-deriving the exact row/column-major storage
  convention the original D3D8/Cg toolchain used for that upload. This is a faithful *code*
  translation, not a re-verified *numeric* one — if these are ever wired into a real pipeline,
  confirm the matrix layout against actual per-draw constant uploads first.
- **`simple.vert`** drops the original's world-space normal/position computation — in the source
  those values are computed but never read again (dead code); the translation is behaviorally
  identical without them. `World` there was declared as a non-square `float3x4`, which doesn't
  port cleanly to GLSL's square `matN` types and — being unused — didn't need to.
- Every uniform block name/field mirrors the original Cg parameter list (renamed only to valid
  GLSL identifiers), so each shader stays independently self-contained rather than forced into the
  app's current single-pipeline push-constant layout.
