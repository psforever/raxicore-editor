---
layout: page
title: Building & Running
description: Requirements and instructions for building and running Raxicore Editor from source.
---

## Requirements

- The **.NET 10 SDK**
- A **Vulkan-capable GPU** (for the 3D viewport)
- Windows or Linux

## Build & run

```bash
git clone https://github.com/{{ site.repository }}.git
cd raxicore-editor
dotnet build RaxicoreEditor.slnx -c Debug
dotnet run --project src/RaxicoreEditor.Editor
```

Then **File → Open Folder…** and point it at your engine-derived game's install directory.

## Headless validation

The `EngineAssets` format readers carry their own validation harness, run against real shipped
files rather than synthetic fixtures:

```bash
dotnet run --project src/RaxicoreEditor.Editor -- --selftest <path-to-asset-or-folder>
```

Point it at an archive, a single asset file, or a whole install directory to exercise the parsers,
the repack round-trip checks, and the offscreen renderer end-to-end.

## Project layout

```
raxicore-editor/
├─ RaxicoreEditor.slnx
├─ Directory.Build.props                 # net10.0, nullable, explicit usings
└─ src/
   ├─ RaxicoreEditor.EngineAssets/       # pure format I/O class library (no UI/render deps)
   │  ├─ Archives/  Compression/  Databases/
   │  ├─ Maps/  Meshes/  Surfaces/  Textures/  IO/
   └─ RaxicoreEditor.Editor/             # Avalonia app + Silk.NET offscreen renderer
      ├─ Documents/  ViewModels/  Views/  Rendering/  Controls/
```

`RaxicoreEditor.EngineAssets` is a standalone, UI-free library reusable for headless tooling; the
editor references it. See the [supported formats reference]({{ '/formats/' | relative_url }}) for
what it can read and write.

## Tech stack

UI is [Avalonia](https://avaloniaui.net/) 12 with the [ShadUI](https://github.com/accntech/shad-ui)
theme (follows the OS light/dark setting). The 3D viewport uses
[Silk.NET](https://github.com/dotnet/Silk.NET) Vulkan bindings, rendered fully offscreen and
composited into the UI via a `WriteableBitmap`. All dependencies — the app's, and this site's own
fonts — are permissively licensed (MIT / Apache-2.0 / OFL); see
[THIRD-PARTY-NOTICES.md](https://github.com/{{ site.repository }}/blob/main/THIRD-PARTY-NOTICES.md).
