# Third-Party Notices

Raxicore Editor is intended to be released under the **MIT License**. It depends only on
permissively licensed (MIT / Apache-2.0 / SIL OFL) components so that downstream MIT use stays
unencumbered. No copyleft (GPL/LGPL) dependencies are used.

| Component | Purpose | License |
|---|---|---|
| [.NET 10 runtime & libraries](https://github.com/dotnet/runtime) | Base class library / runtime | MIT |
| [Avalonia](https://github.com/AvaloniaUI/Avalonia) (`Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Controls.DataGrid`, `Avalonia.Fonts.Inter`) | Cross-platform desktop UI | MIT |
| [Inter typeface](https://github.com/rsms/inter) (bundled via `Avalonia.Fonts.Inter`) | UI font | SIL Open Font License 1.1 |
| [ShadUI](https://github.com/accntech/shad-ui) (`ShadUI`) | shadcn-style theme + window chrome (default UI theme) | MIT |
| [Silk.NET](https://github.com/dotnet/Silk.NET) (`Silk.NET.Vulkan`, `Silk.NET.Core`) | Vulkan bindings for the offscreen 3D viewport | MIT |
| [Orbitron](https://github.com/theleagueof/orbitron) (`docs/assets/fonts/Orbitron-Variable.woff2`) | Documentation site display/heading font | SIL Open Font License 1.1 |
| [Exo 2](https://github.com/googlefonts/Exo-2.0) (`docs/assets/fonts/Exo2-Variable.woff2`) | Documentation site body font | SIL Open Font License 1.1 |
| [Share Tech Mono](https://fonts.google.com/specimen/Share+Tech+Mono) (`docs/assets/fonts/ShareTechMono-Regular.woff2`) | Documentation site monospace/code font | SIL Open Font License 1.1 |

## Notes

- The **Inter**, **Orbitron**, **Exo 2**, and **Share Tech Mono** fonts are each licensed under the
  SIL Open Font License 1.1 (OSI-approved, permissive). The OFL applies to the font files
  themselves and does not affect the license of the application or site; bundling OFL fonts in an
  MIT-licensed program/site is standard and permitted. The full license text for each is vendored
  alongside its font file (`docs/assets/fonts/OFL-*.txt`), fetched directly from Google Fonts —
  self-hosted rather than linked at runtime, so the site has no external font-loading dependency.
- The asset-format code in `RaxicoreEditor.EngineAssets` is derived from the project's own
  engine-derived reverse-engineering work and is original to this repository — it is not a
  third-party dependency.
- When adding new dependencies, keep to MIT / Apache-2.0 / BSD / OFL. If a Vulkan memory
  allocator is needed it will be implemented in-house rather than pulling a differently-licensed
  binding.
