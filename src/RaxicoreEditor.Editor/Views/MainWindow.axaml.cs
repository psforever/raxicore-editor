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

            // Reflect the persisted status-bar theme (applied at startup in App.Initialize) in the menu.
            if (Application.Current is App app)
            {
                SyncStatusThemeChecks(app.Themes.StatusBar);
            }
            SyncModelDetailChecks(RenderSettings.Detail);
            EngineShadingItem.IsChecked = RenderSettings.EngineShading;
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
                MeshObjExporter.Result r = MeshObjExporter.Build(part, baseName);
                await File.WriteAllTextAsync(objPath, r.Obj);
                await File.WriteAllTextAsync(Path.Combine(dir, baseName + ".mtl"), r.Mtl);
                int pngs = 0;
                foreach (MeshObjExporter.TextureSidecar tex in r.Textures)
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
