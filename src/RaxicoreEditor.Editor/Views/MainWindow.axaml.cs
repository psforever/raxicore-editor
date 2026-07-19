using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using RaxicoreEditor.Editor.Documents;
using RaxicoreEditor.Editor.Theming;
using RaxicoreEditor.Editor.Updates;
using RaxicoreEditor.Editor.ViewModels;
using RaxicoreEditor.EngineAssets.Meshes;

namespace RaxicoreEditor.Editor.Views
{
    // ShadUI.Window provides the shadcn-style window chrome (custom title bar + caption buttons).
    public partial class MainWindow : ShadUI.Window
    {
        private readonly MainWindowViewModel _vm = new();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = _vm;
            Title = string.IsNullOrEmpty(AppVersion.Display) ? "Raxicore Editor" : $"Raxicore Editor {AppVersion.Display}";

            // Reflect the persisted status-bar theme (applied at startup in App.Initialize) in the menu.
            if (Application.Current is App app)
            {
                SyncStatusThemeChecks(app.Themes.StatusBar);
            }
            SyncModelDetailChecks(RenderSettings.Detail);
            EngineShadingItem.IsChecked = RenderSettings.EngineShading;
            SkyItem.IsChecked = RenderSettings.Sky;
            SetUpFrameRateMenu();
            AutoUpdateItem.IsChecked = (Application.Current as App)?.Settings.AutoCheckForUpdates ?? true;

            // The in-app updater installs a Windows .exe, so hide it entirely on other platforms.
            if (!OperatingSystem.IsWindows())
            {
                CheckUpdatesItem.IsVisible = false;
                AutoUpdateItem.IsVisible = false;
                UpdateSeparator.IsVisible = false;
            }

            // Auto-check runs once the window is up (never blocks startup; only ever *offers* an update).
            Opened += OnWindowOpened;
        }

        private async void OnWindowOpened(object? sender, EventArgs e)
        {
            Opened -= OnWindowOpened;
            if ((Application.Current as App)?.Settings.AutoCheckForUpdates == true)
            {
                await CheckForUpdatesAsync(silent: true);
            }
        }

        private void OnCheckForUpdates(object? sender, RoutedEventArgs e) => _ = CheckForUpdatesAsync(silent: false);

        private void OnToggleAutoUpdate(object? sender, RoutedEventArgs e)
        {
            bool on = AutoUpdateItem.IsChecked;
            if (Application.Current is App app)
            {
                app.Settings.AutoCheckForUpdates = on;
                app.Settings.Save();
            }
            _vm.Log($"Automatic update check: {(on ? "on" : "off")}.");
        }

        /// <summary>Query GitHub for a newer release and, if found, show the update dialog. In silent mode
        /// (startup) failures and the "up to date" case stay quiet, and a version the user chose to skip is
        /// suppressed; a manual check reports every outcome in the status bar.</summary>
        private async Task CheckForUpdatesAsync(bool silent)
        {
            // The updater downloads and runs the Windows NSIS installer, so it only applies on Windows. On
            // macOS/Linux, updates come through the platform's own package/build.
            if (!OperatingSystem.IsWindows())
            {
                if (!silent)
                {
                    _vm.SetStatus("In-app updates are Windows-only; update this build through your platform's package or a fresh build.");
                }
                return;
            }
            if (!silent)
            {
                _vm.SetStatus("Checking for updates…");
            }
            try
            {
                UpdateInfo? info = await UpdateService.CheckForUpdateAsync();
                if (info is null)
                {
                    if (!silent)
                    {
                        _vm.SetStatus($"You're up to date — Raxicore Editor {AppVersion.Display}.");
                    }
                    return;
                }

                // On the startup check, honour a version the user explicitly skipped.
                string? skipped = (Application.Current as App)?.Settings.SkippedUpdateVersion;
                if (silent && !string.IsNullOrEmpty(skipped) && skipped == info.Version.ToString())
                {
                    return;
                }

                await new UpdateDialog(info).ShowDialog(this);
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    _vm.SetStatus("Update check failed: " + ex.Message);
                }
                _vm.Log("update check failed: " + ex.Message);
            }
        }

        private void OnToggleSky(object? sender, RoutedEventArgs e)
        {
            RenderSettings.Sky = SkyItem.IsChecked;
            if (Application.Current is App app)
            {
                app.Settings.Sky = RenderSettings.Sky;
                app.Settings.Save();
            }
            // Per-frame render setting — no geometry rebuild, just nudge open viewports to redraw.
            RenderSettings.RaiseChanged();
            _vm.Log($"Sky: {(RenderSettings.Sky ? "on" : "off")}.");
        }

        private async void OnOpenFolder(object? sender, RoutedEventArgs e)
        {
            try
            {
                IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(
                    new FolderPickerOpenOptions { Title = "Open asset folder", AllowMultiple = false });
                if (folders.Count > 0)
                {
                    string? path = folders[0].TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        await _vm.MountFolderAsync(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.Log("open folder failed: " + ex.Message);
            }
        }

        private async void OnOpenArchive(object? sender, RoutedEventArgs e)
        {
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { Title = "Open archive / asset", AllowMultiple = false });
                if (files.Count > 0)
                {
                    string? path = files[0].TryGetLocalPath();
                    if (!string.IsNullOrEmpty(path))
                    {
                        await _vm.OpenPathAsync(path);
                    }
                }
            }
            catch (Exception ex)
            {
                _vm.Log("open archive failed: " + ex.Message);
            }
        }

        private async void OnExport(object? sender, RoutedEventArgs e)
        {
            DocumentBase? doc = _vm.SelectedDocument;
            if (doc is null || !doc.CanExport)
            {
                return;
            }

            // File-type enforcement: don't export a text document that fails its format validation.
            if (doc is TextDocument td && !td.IsContentValid)
            {
                _vm.Log($"Export blocked: '{td.Title}' has validation errors ({td.ValidationSummary}). " +
                        "Fix them (see the panel below the editor) before exporting.");
                return;
            }

            try
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(
                    new FilePickerSaveOptions
                    {
                        Title = "Export document",
                        SuggestedFileName = doc.SuggestedFileName,
                    });
                if (file is null)
                {
                    return;
                }
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                await File.WriteAllBytesAsync(path, doc.Export());
                doc.IsDirty = false;
                _vm.Log($"Exported '{doc.Title}' → {path}");
            }
            catch (Exception ex)
            {
                _vm.Log("export failed: " + ex.Message);
            }
        }

        private void OnExit(object? sender, RoutedEventArgs e) => Close();

        private void OnAbout(object? sender, RoutedEventArgs e)
        {
            _ = new AboutDialog().ShowDialog(this);
        }

        private void OnThemeSystem(object? sender, RoutedEventArgs e) => SetVariant(ThemeVariant.Default);
        private void OnThemeLight(object? sender, RoutedEventArgs e) => SetVariant(ThemeVariant.Light);
        private void OnThemeDark(object? sender, RoutedEventArgs e) => SetVariant(ThemeVariant.Dark);

        private void SetVariant(ThemeVariant variant)
        {
            if (Application.Current is App app)
            {
                app.Themes.SetVariant(variant);
            }
            ThemeSystemItem.IsChecked = variant == ThemeVariant.Default;
            ThemeLightItem.IsChecked = variant == ThemeVariant.Light;
            ThemeDarkItem.IsChecked = variant == ThemeVariant.Dark;
        }

        private void OnStatusTradition(object? sender, RoutedEventArgs e) => SetStatusTheme(StatusBarTheme.Tradition);
        private void OnStatusLiberty(object? sender, RoutedEventArgs e) => SetStatusTheme(StatusBarTheme.Liberty);
        private void OnStatusTechnology(object? sender, RoutedEventArgs e) => SetStatusTheme(StatusBarTheme.Technology);
        private void OnStatusDisruption(object? sender, RoutedEventArgs e) => SetStatusTheme(StatusBarTheme.Disruption);

        private void SetStatusTheme(StatusBarTheme theme)
        {
            if (Application.Current is App app)
            {
                app.Themes.SetStatusBarTheme(theme);
                app.Settings.StatusBarTheme = theme.ToString();
                app.Settings.Save();
            }
            SyncStatusThemeChecks(theme);
        }

        private void SyncStatusThemeChecks(StatusBarTheme theme)
        {
            StatusTraditionItem.IsChecked = theme == StatusBarTheme.Tradition;
            StatusLibertyItem.IsChecked = theme == StatusBarTheme.Liberty;
            StatusTechnologyItem.IsChecked = theme == StatusBarTheme.Technology;
            StatusDisruptionItem.IsChecked = theme == StatusBarTheme.Disruption;
        }

        private void OnDetailFull(object? sender, RoutedEventArgs e) => SetModelDetail(ModelDetail.Detailed);
        private void OnDetailLow(object? sender, RoutedEventArgs e) => SetModelDetail(ModelDetail.Low);

        private async void SetModelDetail(ModelDetail detail)
        {
            bool changed = RenderSettings.Detail != detail;
            RenderSettings.Detail = detail;
            if (Application.Current is App app)
            {
                app.Settings.ModelDetail = detail.ToString();
                app.Settings.Save();
            }
            SyncModelDetailChecks(detail);
            if (!changed)
            {
                return;
            }
            _vm.Log($"Model detail: {detail}. Rebuilding open 3D views…");
            try { await _vm.ReloadMeshDocumentsAsync(); }
            catch (Exception ex) { _vm.Log("reload failed: " + ex.Message); }
        }

        private void SyncModelDetailChecks(ModelDetail detail)
        {
            DetailFullItem.IsChecked = detail == ModelDetail.Detailed;
            DetailLowItem.IsChecked = detail == ModelDetail.Low;
        }

        // The framerate-cap radio items and their FPS value (0 = Unlimited).
        private (MenuItem Item, int Fps)[] FrameRateItems() => new[]
        {
            (Fps24Item, 24), (Fps30Item, 30), (Fps60Item, 60), (Fps75Item, 75),
            (Fps120Item, 120), (Fps144Item, 144), (Fps165Item, 165), (Fps240Item, 240),
            (FpsUnlimitedItem, 0),
        };

        // Reflect the persisted cap in the menu, and grey out caps that exceed the monitor's refresh rate
        // (they'd render frames the display can't show). If the refresh rate is unknown (non-Windows or the
        // query failed), every option stays available.
        private void SetUpFrameRateMenu()
        {
            int saved = RenderSettings.FrameCap;
            int? maxHz = MonitorInfo.MaxSupportedRefreshHz();
            int? currentHz = MonitorInfo.CurrentRefreshHz();
            if (maxHz is int max)
            {
                string cur = currentHz is int c && c != max ? $" (currently {c} Hz)" : "";
                ToolTip.SetTip(FrameRateMenu,
                    $"Cap how fast the 3D viewport renders. Your display supports up to {max} Hz{cur}; higher " +
                    "caps are disabled since the display can't show more than that.");
            }
            foreach ((MenuItem item, int fps) in FrameRateItems())
            {
                item.IsChecked = fps == saved;
                if (fps > 0 && maxHz is int cap && fps > cap)
                {
                    item.IsEnabled = false;
                    ToolTip.SetTip(item, $"Above your display's {cap} Hz — it can't show more than {cap} fps.");
                }
            }
        }

        private void SyncFrameCapChecks(int fps)
        {
            foreach ((MenuItem item, int f) in FrameRateItems())
            {
                item.IsChecked = f == fps;
            }
        }

        private void OnFrameCap(object? sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: string tag } || !int.TryParse(tag, out int fps))
            {
                return;
            }
            RenderSettings.FrameCap = fps;
            if (Application.Current is App app)
            {
                app.Settings.FrameRateCap = fps;
                app.Settings.Save();
            }
            SyncFrameCapChecks(fps);
            RenderSettings.RaiseChanged(); // nudge idle viewports so a lowered cap takes effect immediately
            _vm.Log($"Frame rate cap: {(fps == 0 ? "unlimited" : fps + " fps")}.");
        }

        private async void OnToggleEngineShading(object? sender, RoutedEventArgs e)
        {
            bool on = EngineShadingItem.IsChecked;
            RenderSettings.EngineShading = on;
            if (Application.Current is App app)
            {
                app.Settings.EngineShading = on;
                app.Settings.Save();
            }
            _vm.Log($"Engine shaders: {(on ? "on" : "off")}. Refreshing open 3D views…");
            // The colour stream is always uploaded, so this is a pure pipeline swap — but reusing the reload
            // path is the simplest way to force every open viewport to redraw with the new pipeline.
            try { await _vm.ReloadMeshDocumentsAsync(); }
            catch (Exception ex) { _vm.Log("refresh failed: " + ex.Message); }
        }

        private async void OnExportObj(object? sender, RoutedEventArgs e)
        {
            if (_vm.SelectedDocument is not MeshDocument mesh || mesh.SelectedPart is not MeshPart part)
            {
                _vm.Log("Export OBJ: select a 3D mesh first.");
                return;
            }
            try
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export mesh as OBJ",
                    SuggestedFileName = part.Name + ".obj",
                    DefaultExtension = "obj",
                });
                if (file is null)
                {
                    return;
                }
                string? objPath = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(objPath))
                {
                    return;
                }
                string dir = Path.GetDirectoryName(objPath)!;
                string baseName = Path.GetFileNameWithoutExtension(objPath);
                // Stream to disk (a large part's OBJ text can exceed .NET's ~2 GB single-string cap).
                IReadOnlyList<MeshObjExporter.TextureSidecar> texList = await Task.Run(() =>
                {
                    using var objW = new StreamWriter(objPath, false);
                    using var mtlW = new StreamWriter(Path.Combine(dir, baseName + ".mtl"), false);
                    return MeshObjExporter.Write(part, baseName, objW, mtlW);
                });
                int pngs = 0;
                foreach (MeshObjExporter.TextureSidecar tex in texList)
                {
                    try { SavePng(Path.Combine(dir, tex.FileName), tex.Bgra, tex.Width, tex.Height); pngs++; }
                    catch { /* skip an unencodable texture */ }
                }
                _vm.Log($"Exported OBJ '{part.Name}' ({part.TriangleCount} tris) + {pngs} textures → {objPath}");
            }
            catch (Exception ex)
            {
                _vm.Log("OBJ export failed: " + ex.Message);
            }
        }

        private async void OnExportBlend(object? sender, RoutedEventArgs e)
        {
            if (_vm.SelectedDocument is not MeshDocument mesh || mesh.SelectedPart is not MeshPart part)
            {
                _vm.Log("Export .blend: select a 3D mesh first.");
                return;
            }
            string? overridePath = (Application.Current as App)?.Settings.BlenderPath;
            string? blender = MeshBlendExporter.FindBlender(overridePath);
            if (blender is null)
            {
                _vm.Log("Export .blend: no Blender install found. Install Blender, or set its path in settings (BlenderPath).");
                return;
            }
            try
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export mesh as Blender .blend",
                    SuggestedFileName = part.Name + ".blend",
                    DefaultExtension = "blend",
                });
                string? blendPath = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(blendPath))
                {
                    return;
                }
                _vm.Log($"Exporting '{part.Name}' to Blender via {Path.GetFileName(blender)}…");
                // Blender runs as a subprocess (a few seconds) — keep it off the UI thread. The exporter's
                // default PNG encoder is dependency-free and thread-safe.
                await Task.Run(() => MeshBlendExporter.Export(part, blendPath, blender!));
                _vm.Log($"Exported Blender file '{part.Name}' ({part.TriangleCount} tris) → {blendPath}");
            }
            catch (Exception ex)
            {
                _vm.Log(".blend export failed: " + ex.Message);
            }
        }

        private async void OnExportAnim(object? sender, RoutedEventArgs e)
        {
            if (_vm.SelectedDocument is not MeshDocument mesh || mesh.ActiveClip is not AnimRecord clip)
            {
                _vm.Log("Export animation: open a skinned model and select a clip first.");
                return;
            }
            try
            {
                IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "Export animation data",
                    SuggestedFileName = clip.Name + ".csv",
                    DefaultExtension = "csv",
                    FileTypeChoices = new[]
                    {
                        new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
                        new FilePickerFileType("JSON") { Patterns = new[] { "*.json" } },
                    },
                });
                string? path = file?.TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                string text;
                if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    text = AnimClipExporter.ToJson(clip);
                }
                else
                {
                    using var sw = new StringWriter();
                    AnimClipExporter.WriteCsv(clip, sw);
                    text = sw.ToString();
                }
                await File.WriteAllTextAsync(path, text);
                _vm.Log($"Exported animation '{clip.Name}' ({clip.Tracks.Count} bones) → {path}");
            }
            catch (Exception ex)
            {
                _vm.Log("animation export failed: " + ex.Message);
            }
        }

        private static void SavePng(string path, byte[] bgra, int w, int h)
        {
            var bmp = new Avalonia.Media.Imaging.WriteableBitmap(
                new PixelSize(w, h), new Avalonia.Vector(96, 96),
                Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Unpremul);
            using (Avalonia.Platform.ILockedFramebuffer fb = bmp.Lock())
            {
                int row = w * 4;
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(bgra, y * row, IntPtr.Add(fb.Address, y * fb.RowBytes), row);
                }
            }
            bmp.Save(path);
        }

        private async void OnLoadAnimations(object? sender, RoutedEventArgs e)
        {
            if (_vm.SelectedDocument is not MeshDocument mesh)
            {
                return;
            }
            try
            {
                IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
                    new FilePickerOpenOptions { Title = "Open anims.ubr", AllowMultiple = false });
                if (files.Count == 0)
                {
                    return;
                }
                string? path = files[0].TryGetLocalPath();
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }
                byte[] bytes = await File.ReadAllBytesAsync(path);
                if (!AnimDb.IsAnim(bytes))
                {
                    _vm.Log("not an ANIM (.ubr) database: " + path);
                    return;
                }
                AnimDb db = AnimDb.Load(bytes);
                mesh.SetAnimSource(db);
                _vm.Log($"Loaded {db.RecordCount} animations; {mesh.AnimClips.Count} match this skeleton.");
            }
            catch (Exception ex)
            {
                _vm.Log("load animations failed: " + ex.Message);
            }
        }

        // Grabbing the animation scrubber pauses playback so the drag isn't fought by the advancing clock;
        // the two-way bound Value then seeks + re-poses the model.
        private void OnScrubberPressed(object? sender, PointerPressedEventArgs e)
        {
            if ((sender as Control)?.DataContext is MeshDocument doc)
            {
                doc.IsPlaying = false;
            }
        }

        // Swap one material to an empire variant (per-material NC/TR/VS buttons).
        private void OnMaterialEmpire(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string empire, DataContext: MaterialSlot slot } &&
                _vm.SelectedDocument is MeshDocument mesh)
            {
                mesh.ApplyEmpireToMaterial(slot, empire);
            }
        }

        // Flip the whole model to an empire (the "stop showing NC on every model" action).
        private void OnModelEmpire(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: string empire } && _vm.SelectedDocument is MeshDocument mesh)
            {
                mesh.ApplyEmpireToModel(empire);
            }
        }

        // Tab right-click menu. The ContextMenu inherits the tab's DataContext (the DocumentBase).
        private void OnTabClose(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: DocumentBase doc }) _vm.CloseDocument(doc);
        }

        private void OnTabCloseBefore(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: DocumentBase doc }) _vm.CloseDocumentsBefore(doc);
        }

        private void OnTabCloseAfter(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: DocumentBase doc }) _vm.CloseDocumentsAfter(doc);
        }

        private void OnTabCloseOthers(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { DataContext: DocumentBase doc }) _vm.CloseOtherDocuments(doc);
        }

        private void OnTabCloseAll(object? sender, RoutedEventArgs e) => _vm.CloseAllDocuments();

        // Play a single embedded snippet (the ▶ button's DataContext is the AudioClip).
        private void OnPlaySnippet(object? sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: AudioClip clip } && _vm.SelectedDocument is AudioDocument audio)
            {
                audio.PlayClip(clip);
            }
        }

        private async void OnBrowserDoubleTapped(object? sender, TappedEventArgs e)
        {
            if (_vm.SelectedNode is BrowserNode node)
            {
                await _vm.OpenNodeAsync(node);
            }
        }
    }
}
