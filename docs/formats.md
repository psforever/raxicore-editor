---
layout: page
title: Supported Formats
description: The engine-derived asset container and content formats Raxicore Editor reads and writes.
---

Every format below was recovered from an engine-derived reference client and ported to a UI-free
C# library (`RaxicoreEditor.EngineAssets`), reusable outside the editor for headless tooling.

| Format | Extension(s) | Read | Write | Notes |
|---|---|:---:|:---:|---|
| PACK archive | `.pak` | ✅ | ✅ | v2 container: 28-byte header + directory record, LZO1X-compressed payload records with a length-seeded CRC. The repack writer emits store-mode LZO1X and matching per-record CRCs. |
| FLAT archive | `.fat` / `.fdx` | ✅ | ✅ | A self-describing flat data store (name + length framed inline) with a companion index; the writer emits a matching `.fat` + `.fdx` pair. |
| UberMesh | `.ubr` (`uber` magic) | ✅ | — | Per-`CMeshSection` geometry: positions, UVs, normals, skeletons, and material bindings, decoded straight into GPU-ready submeshes. |
| UberAnim | `anims.ubr` (`ANIM` magic) | ✅ | — | A two-stream keyframe database — a directory stream and a data stream — read together and matched to a model's skeleton by bone name. |
| ASCII database | `.adb` | ✅ | — | `chunky` / `asciidatabase` framed token tables, including the structured `game_objects` object/item registry. |
| Surface tile | `.srf` | ✅ | ✅ | A 128×128 grid of typed cells describing ground cover / terrain surface type. Fully paintable in the editor; edits re-encode losslessly. |
| Map manifest | `contents_mapNN.mpo` | ✅ | — | Per-continent tile and object placement — which static scene objects (bases, towers, warpgates, trees) sit where, and which UberMesh record backs each one. |
| DDS texture | `.dds` | ✅ | — | BC1/BC2/BC3 block-compressed and uncompressed 32-bpp images, decoded for preview and for texturing the 3D viewport. |
| LZO1X | — | ✅ | ✅ | The compression codec every `.pak` record uses; not a file format on its own, but load-bearing for everything inside a PACK archive. |

## What "Write" means here

A ✅ in the **Write** column means the `RaxicoreEditor.EngineAssets` library can re-encode that
exact byte layout — not just export to some other format. PACK and FLAT archives round-trip
losslessly (rebuild → reload → re-extract byte-identical), and surface tiles re-encode in place
after a paint edit.

That's the *library's* capability, not necessarily a button in the editor's UI yet: today, the
**surface-type editor** and **OBJ export** are the only write paths exposed in the app itself.
PACK/FLAT rebuilding exists and is exercised by the project's headless validation harness, but
wiring a "save/repack archive" action into the editor is still on the in-progress edge — see the
[home page]({{ '/' | relative_url }}) status note.

## Detection

Documents are identified **by magic bytes first, file extension second** — an archive entry with
no reliable extension (common inside `.pak` payloads) still opens as the right document type.
Anything unrecognized falls back to a hex view rather than failing to open.
